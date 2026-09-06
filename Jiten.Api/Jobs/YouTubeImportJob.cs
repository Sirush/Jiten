using Hangfire;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.YouTube;
using Jiten.Core.YouTube;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Jiten.Api.Jobs;

public class YouTubeImportJob(
    IDbContextFactory<JitenDbContext> contextFactory,
    YtDlpClient client,
    YouTubeSourceRegistrar registrar,
    IBackgroundJobClient backgroundJobs,
    IOptions<YouTubeOptions> options,
    ILogger<YouTubeImportJob> logger)
{
    /// <summary>
    /// Admin add: resolves and enumerates the source (one yt-dlp listing call, minutes for a large channel),
    /// creates the parent deck and seeds the whole ledger as Pending.
    /// </summary>
    [Queue(YouTubeQueues.Fetch)]
    [AutomaticRetry(Attempts = 0)]
    public async Task AddSource(string url, YouTubeSourceFilters filters, YouTubeSourceTitles? titles, string? coverPath)
    {
        var info = await client.ResolveSourceAsync(url);
        if (titles?.ReleaseDate == null)
            info.OldestUploadAt = await client.GetOldestUploadDateAsync(info);

        var cover = !string.IsNullOrEmpty(coverPath) && File.Exists(coverPath)
            ? await File.ReadAllBytesAsync(coverPath)
            : await client.DownloadImageAsync(info.CoverUrl) ?? [];

        var deckId = await registrar.RegisterAsync(info, filters, cover, titles);
        YouTubeSourceRegistrar.DeleteStagedCover(coverPath);

        logger.LogInformation("YouTubeImport: registered {Kind} {SourceId} as deck {DeckId} with {Count} pending videos",
                              info.Kind, info.SourceId, deckId, info.Videos.Count);

        if (options.Value.ServerFetch)
            backgroundJobs.Enqueue<YouTubeFetchJob>(job => job.Drain(deckId));
    }

    /// <summary>Fetch order is arbitrary (video id), so the visible order is rebuilt from upload dates</summary>
    public static void ReorderByUploadDate(List<Deck> children)
    {
        var order = 1;
        foreach (var child in children.OrderBy(c => c.ReleaseDate).ThenBy(c => c.DeckId))
            child.DeckOrder = order++;
    }

    private static int? Median(List<int> values)
    {
        if (values.Count == 0)
            return null;
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    /// <summary>
    /// Full re-enumeration of a tracked source, adding videos the 15-entry feed never showed.
    /// </summary>
    [Queue(YouTubeQueues.Fetch)]
    [AutomaticRetry(Attempts = 0)]
    public async Task Bootstrap(int deckId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var source = await context.YouTubeSources.AsNoTracking().FirstOrDefaultAsync(s => s.DeckId == deckId);
        if (source == null)
            return;

        var info = await client.ResolveSourceAsync(YouTubeUrlParser.SourceUrl(source.SourceKind, source.SourceId));
        var added = await registrar.SeedLedgerAsync(deckId, info.Videos.Select(v => (v.VideoId, v.Title, v.DurationSeconds, (DateTimeOffset?)null)));

        logger.LogInformation("YouTubeImport: bootstrap of deck {DeckId} listed {Count} videos, {Added} new", deckId, info.Videos.Count, added);

        if (added > 0 && options.Value.ServerFetch)
            backgroundJobs.Enqueue<YouTubeFetchJob>(job => job.Drain(deckId));
    }

    /// <summary>
    /// Recurring pass over rows the home CLI fetched; a server drain enqueues <see cref="ImportFetched"/> directly.
    /// </summary>
    [Queue("default")]
    public async Task ImportAllFetched()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var sources = await context.YouTubeVideos
                                   .Where(v => v.Status == YouTubeVideoStatus.Fetched)
                                   .Select(v => v.SourceDeckId)
                                   .Distinct()
                                   .ToListAsync();

        foreach (var deckId in sources)
            await ImportFetched(deckId);
    }

    /// <summary>
    /// Parses every Fetched subdeck of a source and re-aggregates the parent.
    /// </summary>
    [Queue("default")]
    [AutomaticRetry(Attempts = 1)]
    public async Task ImportFetched(int sourceDeckId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var fetched = await context.YouTubeVideos
                                   .Where(v => v.SourceDeckId == sourceDeckId && v.Status == YouTubeVideoStatus.Fetched && v.ChildDeckId != null)
                                   .ToListAsync();
        if (fetched.Count == 0)
            return;

        var childIds = fetched.Select(v => v.ChildDeckId!.Value).ToList();

        var parent = await context.Decks.FirstAsync(d => d.DeckId == sourceDeckId);
        var children = await context.Decks
                                    .Where(d => d.ParentDeckId == sourceDeckId)
                                    .ToListAsync();
        ReorderByUploadDate(children);

        parent.RuntimeSeconds = children.Sum(c => c.RuntimeSeconds ?? 0);
        parent.MedianChildRuntimeSeconds = Median(children.Select(c => c.RuntimeSeconds).OfType<int>().ToList());
        // An explicit or listing-derived parent date wins; only an unset one follows the oldest imported video
        var dated = children.Where(c => c.ReleaseDate != default).Select(c => c.ReleaseDate).ToList();
        if (parent.ReleaseDate == default && dated.Count > 0)
            parent.ReleaseDate = dated.Min();
        parent.LastUpdate = DateTimeOffset.UtcNow;

        foreach (var video in fetched)
            video.Status = YouTubeVideoStatus.Imported;

        var source = await context.YouTubeSources.FirstAsync(s => s.DeckId == sourceDeckId);
        source.LastSyncedAt = DateTimeOffset.UtcNow;

        await context.SaveChangesAsync();

        // Parses by DeckId and re-aggregates the parent's words, coverage and difficulty
        backgroundJobs.Enqueue<ParseNewSubdecksJob>(job => job.ParseNewSubdecks(sourceDeckId, childIds));

        logger.LogInformation("YouTubeImport: source {DeckId} queued {Count} subdecks for parsing", sourceDeckId, childIds.Count);
    }
}
