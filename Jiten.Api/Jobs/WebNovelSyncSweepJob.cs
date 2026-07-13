using Hangfire;
using Jiten.Core;
using Jiten.Core.Data.WebNovel;
using Jiten.Core.WebNovel;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

public class WebNovelSyncSweepJob(
    IDbContextFactory<JitenDbContext> contextFactory,
    IWebNovelSourceResolver sourceResolver,
    IBackgroundJobClient backgroundJobs,
    ILogger<WebNovelSyncSweepJob> logger)
{
    /// <summary>
    /// Polls every tracked novel's update state and enqueues a fetch for the ones worth syncing.
    ///
    /// For Syosetu this is two API calls per 1,000 novels — the metadata API reports episode count and last
    /// update, so no episode or table-of-contents page is touched until there is something to fetch.
    ///
    /// Polling is daily but syncs are batched (<see cref="WebNovelSchedule.ShouldSync"/>): a sync reparses
    /// the subdeck and rewrites the parent's aggregated words however few episodes it ingests, so a
    /// daily-updating serial is left to accumulate ~15 episodes (or two weeks) between syncs.
    /// </summary>
    [Queue("default")]
    public async Task Sweep()
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var now = DateTimeOffset.UtcNow;
        var due = await context.WebNovelSources
                               .Where(s => s.SyncEnabled && s.NextCheckAt <= now)
                               .ToListAsync();

        if (due.Count == 0)
        {
            logger.LogInformation("WebNovelSweep: nothing due");
            return;
        }

        var dirtyCount = 0;

        foreach (var group in due.GroupBy(s => s.Provider))
        {
            if (!sourceResolver.IsSupported(group.Key))
            {
                logger.LogWarning("WebNovelSweep: provider {Provider} is not enabled, skipping {Count} novels",
                                  group.Key, group.Count());
                continue;
            }

            try
            {
                dirtyCount += await SweepProviderAsync(context, sourceResolver.Resolve(group.Key), group.ToList());
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "WebNovelSweep: polling {Provider} failed", group.Key);
            }
        }

        await context.SaveChangesAsync();

        logger.LogInformation("WebNovelSweep: polled {Due} novels, {Dirty} due a sync", due.Count, dirtyCount);
    }

    private async Task<int> SweepProviderAsync(JitenDbContext context, IWebNovelSource source, List<WebNovelSource> tracked)
    {
        var polled = source is IBatchPollableSource batch
            ? await batch.BatchPollAsync(tracked.Select(s => s.SourceId))
            : await PollIndividuallyAsync(source, tracked);

        var dirty = 0;

        foreach (var novel in tracked)
        {
            if (!polled.TryGetValue(novel.SourceId, out var info))
            {
                // The work was deleted or made private at the source
                novel.ConsecutiveFailures++;
                novel.LastError = "Not found at the source.";
                novel.NextCheckAt = WebNovelSchedule.NextCheckAfterFailure(novel.ConsecutiveFailures);
                logger.LogWarning("WebNovelSweep: {SourceId} was not returned by the source", novel.SourceId);
                continue;
            }

            novel.CompletedAtSource = info.IsCompleted;
            novel.OnHiatusAtSource = info.IsOnHiatus;

            // Fewer episodes than ingested means deletions/renumbering at the source: every later episode
            // shifted position, so a sync would append the wrong text. Alert instead of syncing.
            if (info.EpisodeCount < novel.LastEpisodeCount)
            {
                novel.ConsecutiveFailures++;
                novel.LastError = $"The source lists {info.EpisodeCount} episodes but {novel.LastEpisodeCount} are " +
                                  "ingested — episodes were deleted or renumbered. Rebuild the affected subdecks or re-import.";
                novel.NextCheckAt = WebNovelSchedule.NextCheckAfterFailure(novel.ConsecutiveFailures);
                logger.LogWarning("WebNovelSweep: {SourceId} episode count dropped from {Known} to {Polled}",
                                  novel.SourceId, novel.LastEpisodeCount, info.EpisodeCount);
                continue;
            }

            if (WebNovelSchedule.ShouldSync(novel, info))
            {
                dirty++;
                var deckId = novel.DeckId;
                backgroundJobs.Enqueue<WebNovelFetchJob>(job => job.Sync(deckId));

                // The fetch job owns the next check time from here
                continue;
            }

            novel.NextCheckAt = WebNovelSchedule.NextCheck(info.IsCompleted);

            // The novel polled healthy — a stale failure state (e.g. temporarily private) shouldn't linger
            novel.ConsecutiveFailures = 0;
            novel.LastError = null;

            // Only mark as synced when nothing is pending. A below-threshold backlog keeps LastSyncedAt at
            // the last ingest, so the max-lag clock in ShouldSync keeps running instead of resetting daily.
            if (!WebNovelSchedule.IsDirty(novel, info))
                novel.LastSyncedAt = DateTimeOffset.UtcNow;
        }

        return dirty;
    }

    private static async Task<Dictionary<string, WebNovelInfo>> PollIndividuallyAsync(IWebNovelSource source,
                                                                                      List<WebNovelSource> tracked)
    {
        var polled = new Dictionary<string, WebNovelInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var novel in tracked)
            polled[novel.SourceId] = await source.GetInfoAsync(novel.SourceId);

        return polled;
    }
}
