using System.Net;
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
    /// Hands every provider with novels due a check to <see cref="SweepProvider"/> on its site's metadata queue,
    /// where polls share the site's throttle with imports instead of blocking a default worker.
    /// </summary>
    [Queue("default")]
    public async Task Sweep()
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var now = DateTimeOffset.UtcNow;
        var dueByProvider = await context.WebNovelSources
                                         .Where(s => s.SyncEnabled && s.NextCheckAt <= now)
                                         .GroupBy(s => s.Provider)
                                         .Select(g => new { Provider = g.Key, Count = g.Count() })
                                         .ToListAsync();

        if (dueByProvider.Count == 0)
        {
            logger.LogInformation("WebNovelSweep: nothing due");
            return;
        }

        QueueProviderPolls(dueByProvider.Select(d => (d.Provider, d.Count)));
    }

    internal void QueueProviderPolls(IEnumerable<(WebNovelProvider Provider, int Count)> dueByProvider)
    {
        foreach (var (provider, count) in dueByProvider)
        {
            if (!sourceResolver.IsSupported(provider))
            {
                logger.LogWarning("WebNovelSweep: provider {Provider} is not enabled, skipping {Count} novels",
                                  provider, count);
                continue;
            }

            backgroundJobs.Enqueue<WebNovelSyncSweepJob>(job => job.SweepProvider(provider));
            logger.LogInformation("WebNovelSweep: queued a poll of {Count} {Provider} novels", count, provider);
        }
    }

    /// <summary>
    /// Polls one provider's due novels and enqueues a fetch for the ones worth syncing.
    ///
    /// A batch-pollable source (Syosetu) answers for 1,000 novels in two API calls; any other source costs one work
    /// page per novel. Either way no episode page is touched until there is something to fetch.
    ///
    /// Polling is daily but syncs are batched (<see cref="WebNovelSchedule.ShouldSync"/>): a sync reparses
    /// the subdeck and rewrites the parent's aggregated words however few episodes it ingests, so a
    /// daily-updating serial is left to accumulate ~15 episodes (or two weeks) between syncs.
    /// </summary>
    [WebNovelQueue(WebNovelQueueKind.Metadata)]
    public async Task SweepProvider(WebNovelProvider provider)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var now = DateTimeOffset.UtcNow;
        var tracked = await context.WebNovelSources
                                   .Where(s => s.Provider == provider && s.SyncEnabled && s.NextCheckAt <= now)
                                   .ToListAsync();

        if (tracked.Count == 0)
            return;

        var dirty = await PollAsync(provider, tracked);
        await context.SaveChangesAsync();

        logger.LogInformation("WebNovelSweep: polled {Due} {Provider} novels, {Dirty} due a sync",
                              tracked.Count, provider, dirty);
    }

    /// <summary>
    /// Updates each novel's poll state in place and enqueues the syncs; the caller saves.
    /// </summary>
    /// <returns>How many novels were handed to a sync</returns>
    internal async Task<int> PollAsync(WebNovelProvider provider, List<WebNovelSource> tracked)
    {
        var source = sourceResolver.Resolve(provider);
        var stableEpisodeIds = WebNovelProviders.Get(provider).StableEpisodeIds;

        var (polled, errors) = source is IBatchPollableSource batch
            ? (await batch.BatchPollAsync(tracked.Select(s => s.SourceId)), new Dictionary<string, string>())
            : await PollIndividuallyAsync(source, tracked);

        var dirty = 0;

        foreach (var novel in tracked)
        {
            if (!polled.TryGetValue(novel.SourceId, out var info))
            {
                var error = errors.GetValueOrDefault(novel.SourceId);
                novel.ConsecutiveFailures++;
                novel.LastError = WebNovelImportJob.Truncate(error ?? "Not found at the source.", 1000);
                novel.NextCheckAt = WebNovelSchedule.NextCheckAfterFailure(novel.ConsecutiveFailures);
                logger.LogWarning("WebNovelSweep: {Provider}/{SourceId} could not be polled: {Error}",
                                  provider, novel.SourceId, novel.LastError);
                continue;
            }

            novel.CompletedAtSource = info.IsCompleted;
            novel.OnHiatusAtSource = info.IsOnHiatus;

            if (WebNovelLedger.PositionalShrinkError(stableEpisodeIds, info.EpisodeCount, novel.LastEpisodeCount) is { } shrink)
            {
                novel.ConsecutiveFailures++;
                novel.LastError = shrink;
                novel.NextCheckAt = WebNovelSchedule.NextCheckAfterFailure(novel.ConsecutiveFailures);
                logger.LogWarning("WebNovelSweep: {SourceId} episode count dropped from {Known} to {Polled}",
                                  novel.SourceId, novel.LastEpisodeCount, info.EpisodeCount);
                continue;
            }

            if (WebNovelSchedule.ShouldSync(novel, info))
            {
                dirty++;
                var deckId = novel.DeckId;
                backgroundJobs.Enqueue<WebNovelFetchJob>(job => job.Sync(provider, deckId));

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

    /// <summary>
    /// One failing novel must not stop the rest: a 404/410 counts as gone from the source, anything else is
    /// recorded as that novel's error.
    /// </summary>
    private async Task<(Dictionary<string, WebNovelInfo> Polled, Dictionary<string, string> Errors)>
        PollIndividuallyAsync(IWebNovelSource source, List<WebNovelSource> tracked)
    {
        var polled = new Dictionary<string, WebNovelInfo>(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var novel in tracked)
        {
            try
            {
                polled[novel.SourceId] = await source.GetInfoAsync(novel.SourceId);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "WebNovelSweep: polling {Provider}/{SourceId} failed", source.Provider, novel.SourceId);
                errors[novel.SourceId] = ex.Message;
            }
        }

        return (polled, errors);
    }
}
