using Jiten.Core.Data.WebNovel;

namespace Jiten.Core.WebNovel;

public static class WebNovelSchedule
{
    /// <summary>
    /// Slightly under a day so a daily sweep always picks an active novel up
    /// </summary>
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromHours(20);

    /// <summary>
    /// Finished works still get the occasional epilogue or 番外編
    /// </summary>
    private static readonly TimeSpan CompletedInterval = TimeSpan.FromDays(30);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromDays(16);

    public static DateTimeOffset NextCheck(bool completedAtSource) =>
        DateTimeOffset.UtcNow + (completedAtSource ? CompletedInterval : ActiveInterval);

    /// <summary>
    /// Backs off exponentially after failures. Repeated failures usually mean the site's markup changed,
    /// so there is no point retrying hard.
    /// </summary>
    public static DateTimeOffset NextCheckAfterFailure(int consecutiveFailures)
    {
        var backoff = TimeSpan.FromDays(Math.Pow(2, Math.Clamp(consecutiveFailures, 1, 4)));
        return DateTimeOffset.UtcNow + (backoff > MaxBackoff ? MaxBackoff : backoff);
    }

    /// <summary>
    /// A sync is only worth its cost once this many episodes have accumulated: each one reparses the open
    /// subdeck and rewrites the parent's aggregated word data, whether it ingests 1 episode or 20.
    /// ~15 median episodes is a quarter of a subdeck — enough to visibly move the deck's statistics.
    /// </summary>
    public const int MinEpisodesForSync = 15;

    /// <summary>
    /// Slow novels still land within this window, however few episodes accumulated. Matches the Narou API
    /// docs' cache-expiry guidance.
    /// </summary>
    public static readonly TimeSpan MaxSyncLag = TimeSpan.FromDays(14);

    /// <summary>
    /// A novel has pending changes when the source reports episodes we haven't ingested, or a newer
    /// timestamp than the one we last saw (a revision to an existing episode).
    /// </summary>
    public static bool IsDirty(WebNovelSource tracked, WebNovelInfo polled) =>
        polled.EpisodeCount > tracked.LastEpisodeCount ||
        (polled.LastUpdatedAt != null &&
         (tracked.LastSourceUpdate == null || polled.LastUpdatedAt > tracked.LastSourceUpdate));

    /// <summary>
    /// Whether pending changes are worth a sync yet: enough episodes accumulated, or anything at all has
    /// been pending for <see cref="MaxSyncLag"/>. The sweep must leave <c>LastSyncedAt</c> untouched while
    /// a below-threshold backlog exists, so the lag clock measures time since the last ingest.
    /// </summary>
    public static bool ShouldSync(WebNovelSource tracked, WebNovelInfo polled)
    {
        if (!IsDirty(tracked, polled))
            return false;

        if (polled.EpisodeCount - tracked.LastEpisodeCount >= MinEpisodesForSync)
            return true;

        return tracked.LastSyncedAt == null || DateTimeOffset.UtcNow - tracked.LastSyncedAt >= MaxSyncLag;
    }
}
