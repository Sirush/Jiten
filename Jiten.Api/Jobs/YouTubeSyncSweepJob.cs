using Hangfire;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.YouTube;
using Jiten.Core.YouTube;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Jiten.Api.Jobs;

public class YouTubeSyncSweepJob(
    IDbContextFactory<JitenDbContext> contextFactory,
    YouTubeFeedReader feedReader,
    YouTubeSourceRegistrar registrar,
    IBackgroundJobClient backgroundJobs,
    IOptions<YouTubeOptions> options,
    ILogger<YouTubeSyncSweepJob> logger)
{
    /// <summary>
    /// Polls the Atom feed of every source past its check time, adds unseen uploads as Pending and re-queues
    /// young NoManualSubs videos. The feed is public and never bot-checked, so detection never depends on the
    /// fetch egress; when server fetching is on, a drain is enqueued for each source with pending rows.
    /// </summary>
    [Queue("default")]
    public async Task Sweep()
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var now = DateTimeOffset.UtcNow;
        var due = await context.YouTubeSources
                               .Where(s => s.SyncEnabled && s.NextCheckAt <= now)
                               .ToListAsync();

        foreach (var source in due)
            await CheckSourceAsync(context, source);

        await context.SaveChangesAsync();

        var pendingSources = await context.YouTubeVideos
                                          .Where(v => v.Status == YouTubeVideoStatus.Pending)
                                          .Select(v => v.SourceDeckId)
                                          .Distinct()
                                          .ToListAsync();

        if (options.Value.ServerFetch)
        {
            foreach (var deckId in pendingSources)
                backgroundJobs.Enqueue<YouTubeFetchJob>(job => job.Drain(deckId));
        }

        logger.LogInformation("YouTubeSweep: checked {Due} sources, {Pending} have pending videos (server fetch {Mode})",
                              due.Count, pendingSources.Count, options.Value.ServerFetch ? "on" : "off");
    }

    /// <summary>
    /// Admin sync-now: one feed check, then a drain when the server fetches.
    /// </summary>
    [Queue("default")]
    public async Task SyncOne(int deckId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var source = await context.YouTubeSources.FirstOrDefaultAsync(s => s.DeckId == deckId);
        if (source == null)
            return;

        await CheckSourceAsync(context, source);
        await context.SaveChangesAsync();

        if (options.Value.ServerFetch)
            backgroundJobs.Enqueue<YouTubeFetchJob>(job => job.Drain(deckId));
    }

    private async Task CheckSourceAsync(JitenDbContext context, YouTubeSource source)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var entries = await feedReader.ReadAsync(source.SourceKind, source.SourceId);

            var added = await registrar.SeedLedgerAsync(source.DeckId,
                                                        entries.Select(e => (e.VideoId, e.Title, (int?)null, (DateTimeOffset?)e.Published)));

            var newest = entries.Count > 0 ? entries.Max(e => e.Published) : (DateTimeOffset?)null;
            if (newest != null && (source.LastSourceUpdate == null || newest > source.LastSourceUpdate))
                source.LastSourceUpdate = newest;

            var rechecks = await context.YouTubeVideos
                                        .Where(v => v.SourceDeckId == source.DeckId && v.Status == YouTubeVideoStatus.NoManualSubs)
                                        .ToListAsync();
            var requeued = 0;
            foreach (var video in rechecks.Where(v => YouTubeSchedule.ShouldRecheck(v, now)))
            {
                video.Status = YouTubeVideoStatus.Pending;
                requeued++;
            }

            source.NextCheckAt = YouTubeSchedule.NextCheck(source);
            source.ConsecutiveFailures = 0;
            source.LastError = null;

            logger.LogInformation("YouTubeSweep: {DeckId} feed had {Entries} entries, {Added} new, {Requeued} re-checks",
                                  source.DeckId, entries.Count, added, requeued);
        }
        catch (Exception ex)
        {
            source.ConsecutiveFailures++;
            source.LastError = YouTubeDrainService.Truncate(ex.Message, 1000);
            source.NextCheckAt = YouTubeSchedule.NextCheckAfterFailure(source.ConsecutiveFailures);
            logger.LogWarning(ex, "YouTubeSweep: feed check failed for {DeckId}", source.DeckId);
        }
    }
}
