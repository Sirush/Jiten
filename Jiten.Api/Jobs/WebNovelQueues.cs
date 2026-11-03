using Hangfire.Common;
using Hangfire.States;
using Jiten.Core.Data.WebNovel;
using Jiten.Core.WebNovel;

namespace Jiten.Api.Jobs;

public enum WebNovelQueueKind
{
    /// <summary>
    /// Imports, syncs and rebuilds: hours-long runs of episode fetches
    /// </summary>
    Fetch,

    /// <summary>
    /// Metadata refreshes and update polls, a request or two each, so they never wait out an import
    /// </summary>
    Metadata
}

/// <summary>
/// Two single-worker queues per site. Imports and syncs for a site serialise on its fetch queue, so the politeness
/// rate holds however many novels are queued; different sites still run concurrently.
/// </summary>
public static class WebNovelQueues
{
    public static string For(WebNovelSite site, WebNovelQueueKind kind) => kind switch
    {
        WebNovelQueueKind.Fetch => $"webnovel-{site.Key}",
        _ => $"webnovel-{site.Key}-metadata"
    };

    public static IEnumerable<string> All =>
        WebNovelProviders.Sites.SelectMany(site => Enum.GetValues<WebNovelQueueKind>().Select(kind => For(site, kind)));
}

/// <summary>
/// Routes a job to its site's queue, read from the job's <see cref="WebNovelProvider"/> argument. A plain
/// [Queue] can't: one method serves every site. Runs on every election, so retries land on the same queue.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WebNovelQueueAttribute(WebNovelQueueKind kind) : JobFilterAttribute, IElectStateFilter
{
    public void OnStateElection(ElectStateContext context)
    {
        if (context.CandidateState is EnqueuedState enqueued && QueueFor(context.BackgroundJob.Job, kind) is { } queue)
            enqueued.Queue = queue;
    }

    private static string? QueueFor(Job job, WebNovelQueueKind kind) =>
        job.Args.OfType<WebNovelProvider>().Select(p => (WebNovelProvider?)p).FirstOrDefault() is { } provider &&
        WebNovelProviders.TryGet(provider, out var descriptor)
            ? WebNovelQueues.For(descriptor.Site, kind)
            : null;
}
