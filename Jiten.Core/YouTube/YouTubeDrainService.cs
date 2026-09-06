using ImageMagick;
using Jiten.Core.Data;
using Jiten.Core.Data.YouTube;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jiten.Core.YouTube;

public delegate Task<(long DurationMs, long MoraCount)> SpeechStatsComputer(List<SubtitleItem> items);

public class YouTubeDrainResult
{
    public int Checked { get; set; }
    public int Fetched { get; set; }
    public int Skipped { get; set; }
    public bool Blocked { get; set; }
    public string? Error { get; set; }
    public List<int> FetchedChildDeckIds { get; } = new();
}

/// <summary>
/// Drains a source's Pending ledger rows: fetches each video, and for accepted ones creates the child deck row
/// with its raw text, link and thumbnail, leaving the row <see cref="YouTubeVideoStatus.Fetched"/> for the parse
/// step. Runs identically on the server and from the home CLI; only the parse step needs the server.
/// </summary>
public class YouTubeDrainService(
    IDbContextFactory<JitenDbContext> contextFactory,
    YtDlpClient client,
    SpeechStatsComputer speechStats,
    string workRoot,
    TimeSpan delayBetweenVideos,
    ILogger? logger = null)
{
    private readonly YouTubeVideoFetcher _fetcher = new(client);

    public async Task<YouTubeDrainResult> DrainAsync(int sourceDeckId, int maxVideos, CancellationToken cancellationToken = default)
    {
        var result = new YouTubeDrainResult();

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var source = await context.YouTubeSources.FirstOrDefaultAsync(s => s.DeckId == sourceDeckId, cancellationToken);
        if (source == null)
        {
            result.Error = $"Deck {sourceDeckId} is not a tracked YouTube source.";
            return result;
        }

        var pending = await context.YouTubeVideos
                                   .Where(v => v.SourceDeckId == sourceDeckId && v.Status == YouTubeVideoStatus.Pending)
                                   .OrderBy(v => v.LastCheckedAt)
                                   .Take(maxVideos)
                                   .ToListAsync(cancellationToken);

        if (pending.Count == 0)
            return result;

        var workDirectory = Path.Combine(workRoot, sourceDeckId.ToString());
        Directory.CreateDirectory(workDirectory);

        var byId = pending.ToDictionary(v => v.VideoId);
        var filters = YouTubeSourceFilters.From(source);

        foreach (var chunk in pending.Chunk(_fetcher.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var requests = chunk.Select(v => new YouTubeFetchRequest(v.VideoId, v.Title, v.RuntimeSeconds)).ToList();
            var batch = await _fetcher.FetchManyAsync(requests, workDirectory, filters, cancellationToken);

            foreach (var (videoId, outcome) in batch.Outcomes)
            {
                var video = byId[videoId];
                result.Checked++;

                if (outcome.FetchFailed)
                {
                    video.LastCheckedAt = DateTimeOffset.UtcNow;
                    video.SkipReason = Truncate(outcome.SkipReason ?? "fetch-error", 500);
                    await context.SaveChangesAsync(cancellationToken);
                    result.Skipped++;
                    logger?.LogWarning("YouTubeDrain: {VideoId} fetch failed: {Error}", video.VideoId, outcome.SkipReason);
                    continue;
                }

                var childDeckId = await ApplyOutcomeAsync(context, source, video, outcome, cancellationToken);
                if (childDeckId == null)
                {
                    result.Skipped++;
                    logger?.LogInformation("YouTubeDrain: {VideoId} {Reason}", video.VideoId, video.SkipReason);
                }
                else
                {
                    result.Fetched++;
                    result.FetchedChildDeckIds.Add(childDeckId.Value);
                    logger?.LogInformation("YouTubeDrain: {VideoId} fetched as deck {ChildDeckId} ({Chars} chars)",
                                           video.VideoId, childDeckId, outcome.Cleaned?.CharacterCount);
                }
            }

            if (batch.BlockedMessage != null)
            {
                // The IP is refused, not the video: leave the unreached rows Pending and stop the whole drain
                result.Blocked = true;
                result.Error = batch.BlockedMessage;
                source.ConsecutiveFailures++;
                source.LastError = Truncate(batch.BlockedMessage, 1000);
                source.NextCheckAt = YouTubeSchedule.NextCheckAfterFailure(source.ConsecutiveFailures);
                await context.SaveChangesAsync(cancellationToken);
                logger?.LogWarning("YouTubeDrain: source {DeckId} stopped, egress is bot-checked: {Error}", sourceDeckId, batch.BlockedMessage);
                return result;
            }

            await Task.Delay(delayBetweenVideos, cancellationToken);
        }

        source.ConsecutiveFailures = 0;
        source.LastError = null;
        await context.SaveChangesAsync(cancellationToken);

        TryDelete(workDirectory);
        return result;
    }

    /// <summary>
    /// Records a fetch verdict on the ledger row; for an accepted video also creates the child deck. Used by the
    /// local drain and by the ingest endpoint the home CLI uploads to. Returns the child deck id when created.
    /// </summary>
    public async Task<int?> ApplyOutcomeAsync(JitenDbContext context, YouTubeSource source, YouTubeVideo video,
                                              YouTubeFetchOutcome outcome, CancellationToken cancellationToken = default)
    {
        ApplyInfo(video, outcome.Info);
        video.LastCheckedAt = DateTimeOffset.UtcNow;

        if (!outcome.Accepted)
        {
            video.Status = outcome.Status;
            video.SkipReason = Truncate(outcome.SkipReason ?? "", 500);
            await context.SaveChangesAsync(cancellationToken);
            return null;
        }

        var nextOrder = await context.Decks
                                     .Where(d => d.ParentDeckId == source.DeckId)
                                     .Select(d => (int?)d.DeckOrder)
                                     .MaxAsync(cancellationToken) ?? 0;

        var child = await CreateChildAsync(context, source, video, outcome, nextOrder + 1, cancellationToken);
        video.Status = YouTubeVideoStatus.Fetched;
        video.SkipReason = null;
        video.ChildDeckId = child.DeckId;
        await context.SaveChangesAsync(cancellationToken);
        return child.DeckId;
    }

    private async Task<Deck> CreateChildAsync(JitenDbContext context, YouTubeSource source, YouTubeVideo video,
                                              YouTubeFetchOutcome outcome, int deckOrder, CancellationToken cancellationToken)
    {
        var info = outcome.Info!;
        var extractor = new SubtitleExtractor();
        var text = await extractor.Extract(outcome.CleanedSrtPath!);

        var items = await extractor.ExtractItems(outcome.CleanedSrtPath!);
        var (durationMs, moraCount) = items.Count > 0 ? await speechStats(items) : (0, 0);

        var child = new Deck
        {
            ParentDeckId = source.DeckId,
            MediaType = MediaType.YouTube,
            OriginalTitle = Truncate(info.Title, 500),
            DeckOrder = deckOrder,
            RuntimeSeconds = info.DurationSeconds,
            SpeechDuration = durationMs,
            SpeechMoraCount = moraCount,
            SentenceCount = 0,
            DifficultyOverride = -1,
            CreationDate = DateTimeOffset.UtcNow,
            LastUpdate = DateTimeOffset.UtcNow,
            ReleaseDate = info.UploadedAt != null ? DateOnly.FromDateTime(info.UploadedAt.Value.UtcDateTime) : default,
            RawText = new DeckRawText(text),
            SubtitleTrack = items.Count > 0 ? DeckSubtitleTrack.FromItems(items) : null
        };
        child.Links.Add(new Link { LinkType = LinkType.YouTube, Url = YouTubeUrlParser.VideoUrl(video.VideoId), Deck = child });

        context.Decks.Add(child);
        await context.SaveChangesAsync(cancellationToken);

        var coverUrl = await UploadThumbnailAsync(child.DeckId, video.VideoId, info.ThumbnailUrl, cancellationToken);
        if (coverUrl != null)
        {
            child.CoverName = coverUrl;
            await context.SaveChangesAsync(cancellationToken);
        }

        return child;
    }

    private async Task<string?> UploadThumbnailAsync(int deckId, string videoId, string? thumbnailUrl, CancellationToken cancellationToken)
    {
        var bytes = await client.DownloadVideoThumbnailAsync(videoId, thumbnailUrl, cancellationToken);
        if (bytes == null)
            return null;

        try
        {
            using var image = new MagickImage(bytes);
            // Thumbnails stay 16:9; the card renders them landscape
            image.Resize(480, 270);
            image.Strip();
            image.Quality = 85;
            image.Format = MagickFormat.Jpeg;
            return await BunnyCdnHelper.UploadFile(image.ToByteArray(), $"{deckId}/cover.jpg");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "YouTubeDrain: thumbnail upload failed for {VideoId}", videoId);
            return null;
        }
    }

    public static void ApplyInfo(YouTubeVideo video, YouTubeVideoInfo? info)
    {
        if (info == null)
            return;

        video.Title = Truncate(info.Title, 500);
        video.UploadedAt = info.UploadedAt ?? video.UploadedAt;
        video.RuntimeSeconds = info.DurationSeconds ?? video.RuntimeSeconds;
        video.PlayableInEmbed = info.PlayableInEmbed;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Staging leftovers are harmless
        }
    }

    public static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
