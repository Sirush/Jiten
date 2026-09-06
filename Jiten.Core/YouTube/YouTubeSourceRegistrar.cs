using Jiten.Core.Data;
using Jiten.Core.Data.YouTube;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core.YouTube;

/// <summary>
/// Creates the parent deck and source row for a channel or playlist, and seeds the ledger from a listing.
/// The parent starts empty; subdecks and words arrive as the ledger drains.
/// </summary>
public class YouTubeSourceRegistrar(IDbContextFactory<JitenDbContext> contextFactory)
{
    public async Task<string?> CheckConflictsAsync(YouTubeSourceInfo info, string? originalTitle = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        if (await context.YouTubeSources.AnyAsync(s => s.SourceKind == info.Kind && s.SourceId == info.SourceId))
            return $"{info.Kind} {info.SourceId} is already tracked.";

        var title = Normalise(originalTitle) ?? info.Title;

        // InsertDeck dedupes on (OriginalTitle, MediaType) and would silently bind the ledger to the other deck
        if (await context.Decks.AnyAsync(d => d.OriginalTitle == title && d.MediaType == MediaType.YouTube))
            return $"A YouTube deck titled '{title}' already exists. Rename or remove it first.";

        return null;
    }

    public async Task<int> RegisterAsync(YouTubeSourceInfo info, YouTubeSourceFilters filters, byte[] cover,
                                         YouTubeSourceTitles? titles = null)
    {
        var conflict = await CheckConflictsAsync(info, titles?.OriginalTitle);
        if (conflict != null)
            throw new InvalidOperationException(conflict);

        var releaseDate = titles?.ReleaseDate
                          ?? (info.OldestUploadAt != null ? DateOnly.FromDateTime(info.OldestUploadAt.Value.UtcDateTime) : default);

        var parent = new Deck
        {
            MediaType = MediaType.YouTube,
            OriginalTitle = Normalise(titles?.OriginalTitle) ?? info.Title,
            RomajiTitle = Normalise(titles?.RomajiTitle),
            EnglishTitle = Normalise(titles?.EnglishTitle),
            ReleaseDate = releaseDate,
            Description = info.Description is { Length: > 2000 } ? info.Description[..2000] : info.Description,
            DifficultyOverride = -1,
            CreationDate = DateTimeOffset.UtcNow,
            LastUpdate = DateTimeOffset.UtcNow,
            Links = [new Link { LinkType = LinkType.YouTube, Url = YouTubeUrlParser.SourceUrl(info.Kind, info.SourceId) }]
        };
        foreach (var link in parent.Links)
            link.Deck = parent;

        foreach (var alias in new[] { info.ChannelName, info.Title }.Where(a => !string.IsNullOrEmpty(a) && a != parent.OriginalTitle).Distinct())
            parent.Titles.Add(new DeckTitle { Title = alias!, TitleType = DeckTitleType.Alias });

        await JitenHelper.InsertDeck(contextFactory, parent, cover);
        if (parent.DeckId == 0)
            throw new InvalidOperationException($"The parent deck for '{info.Title}' was not created.");

        await using var context = await contextFactory.CreateDbContextAsync();
        context.YouTubeSources.Add(new YouTubeSource
        {
            DeckId = parent.DeckId,
            SourceKind = info.Kind,
            SourceId = info.SourceId,
            ChannelName = YouTubeDrainService.Truncate(info.ChannelName, 200),
            ChannelId = info.ChannelId,
            TitleFilterInclude = filters.TitleInclude,
            TitleFilterExclude = filters.TitleExclude,
            MinRuntimeSeconds = filters.MinRuntimeSeconds,
            MaxRuntimeSeconds = filters.MaxRuntimeSeconds,
            NextCheckAt = DateTimeOffset.UtcNow.AddDays(7),
            SyncEnabled = true
        });
        await context.SaveChangesAsync();

        await SeedLedgerAsync(parent.DeckId, info.Videos.Select(v => (v.VideoId, v.Title, v.DurationSeconds, (DateTimeOffset?)null)));
        return parent.DeckId;
    }

    /// <summary>Removes the per-upload staging directory a dashboard cover was parked in.</summary>
    public static void DeleteStagedCover(string? coverPath)
    {
        if (string.IsNullOrEmpty(coverPath))
            return;

        try
        {
            var directory = Path.GetDirectoryName(coverPath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Staging leftovers are harmless
        }
    }

    private static string? Normalise(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Adds every unseen video as Pending. Known videos are left alone whatever their status.
    /// </summary>
    public async Task<int> SeedLedgerAsync(int sourceDeckId,
                                           IEnumerable<(string VideoId, string Title, int? DurationSeconds, DateTimeOffset? UploadedAt)> videos)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var known = (await context.YouTubeVideos
                                  .Where(v => v.SourceDeckId == sourceDeckId)
                                  .Select(v => v.VideoId)
                                  .ToListAsync()).ToHashSet();

        var added = 0;
        foreach (var video in videos)
        {
            if (!known.Add(video.VideoId))
                continue;

            context.YouTubeVideos.Add(new YouTubeVideo
            {
                SourceDeckId = sourceDeckId,
                VideoId = video.VideoId,
                Status = YouTubeVideoStatus.Pending,
                Title = YouTubeDrainService.Truncate(video.Title, 500),
                RuntimeSeconds = video.DurationSeconds,
                UploadedAt = video.UploadedAt,
                LastCheckedAt = DateTimeOffset.MinValue
            });
            added++;
        }

        if (added > 0)
            await context.SaveChangesAsync();

        return added;
    }
}
