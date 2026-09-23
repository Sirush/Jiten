namespace Jiten.Core.Data.FSRS;

/// <summary>Rebuilds a card's schedule from its review log.</summary>
public static class FsrsReplay
{
    /// <summary>
    /// Replays <paramref name="logs"/> onto <paramref name="card"/>, overwriting its schedule fields.
    /// Returns false and leaves the card untouched when there is nothing to replay.
    /// </summary>
    /// <param name="scheduler">Schedules the final review, whose due date is the one that survives.</param>
    /// <param name="replayScheduler">
    /// Schedules every earlier review. Their due dates are superseded by the next review, so fuzzing or
    /// balancing them would only register phantom load in the balancer's histogram.
    /// </param>
    /// <param name="preserveTerminalState">
    /// Keeps an existing Mastered/Blacklisted/Suspended state instead of the replayed one, and routes the
    /// final review through <paramref name="replayScheduler"/> as well, since such a card never comes due.
    /// </param>
    /// <param name="preserveSchedule">Rebuilds only the memory state and lapses; a model switch must not move due dates the user did not ask to move.</param>
    public static bool Recompute(FsrsCard card, IReadOnlyList<FsrsReviewLog> logs,
                                 FsrsScheduler scheduler, FsrsScheduler replayScheduler,
                                 bool preserveTerminalState = true, bool preserveSchedule = false)
    {
        var ordered = logs.Where(l => l.Rating.IsValid())
                          .OrderBy(l => l.ReviewDateTime).ThenBy(l => l.ReviewLogId).ToList();
        if (ordered.Count == 0)
            return false;

        var overrideState = preserveTerminalState
                            && card.State is FsrsState.Mastered or FsrsState.Blacklisted or FsrsState.Suspended
            ? card.State
            : (FsrsState?)null;

        var tempCard = new FsrsCard(card.UserId, card.WordId, card.ReadingIndex);
        var lapses = 0;

        for (var i = 0; i < ordered.Count; i++)
        {
            var log = ordered[i];
            var isSurvivingPlacement = i == ordered.Count - 1 && overrideState == null;
            var activeScheduler = isSurvivingPlacement ? scheduler : replayScheduler;

            var prevState = tempCard.State;
            var review = activeScheduler.ReviewCard(tempCard, log.Rating, AsUtc(log.ReviewDateTime), log.ReviewDuration);
            if (prevState == FsrsState.Review && log.Rating == FsrsRating.Again)
                lapses++;
            tempCard = review.UpdatedCard;
        }

        card.Stability = tempCard.Stability;
        card.Difficulty = tempCard.Difficulty;
        card.StabilityFast = tempCard.StabilityFast;
        card.LastReview = tempCard.LastReview;
        card.Lapses = lapses;
        if (preserveSchedule)
            return true;

        card.State = overrideState ?? tempCard.State;
        card.Step = tempCard.Step;
        card.Due = tempCard.Due;

        return true;
    }

    /// <summary>
    /// The state, due date and last review each of <paramref name="finalSchedulers"/> would give the card after a
    /// replay, sharing one pass over the earlier reviews; null when there is nothing to replay.
    /// </summary>
    /// <remarks>Earlier reviews' memory state depends only on ratings and timestamps, so only the final placement differs between schedulers.</remarks>
    public static (FsrsState State, DateTime Due, DateTime? LastReview)[]? ProjectFinalPlacements(
        IReadOnlyList<FsrsReviewLog> logs, FsrsScheduler replayScheduler, IReadOnlyList<FsrsScheduler> finalSchedulers)
    {
        var ordered = logs.Where(l => l.Rating.IsValid())
                          .OrderBy(l => l.ReviewDateTime).ThenBy(l => l.ReviewLogId).ToList();
        if (ordered.Count == 0)
            return null;

        var tempCard = new FsrsCard("", 0, 0);
        for (var i = 0; i < ordered.Count - 1; i++)
            tempCard = replayScheduler.ReviewCard(tempCard, ordered[i].Rating, AsUtc(ordered[i].ReviewDateTime), ordered[i].ReviewDuration).UpdatedCard;

        var last = ordered[^1];
        var placements = new (FsrsState, DateTime, DateTime?)[finalSchedulers.Count];
        for (var i = 0; i < finalSchedulers.Count; i++)
        {
            var placed = finalSchedulers[i].ReviewCard(tempCard, last.Rating, AsUtc(last.ReviewDateTime), last.ReviewDuration).UpdatedCard;
            placements[i] = (placed.State, placed.Due, placed.LastReview);
        }

        return placements;
    }

    /// <summary>Fits S and D from a source that records no FSRS version (backups) to the owner's model: FSRS-7 replays them, FSRS-6 drops the fast trace.</summary>
    public static void AdoptMemoryModel(FsrsCard card, IReadOnlyList<FsrsReviewLog> logs, FsrsScheduler scheduler)
    {
        if (scheduler.Version != FsrsVersion.V7)
        {
            card.StabilityFast = null;
            return;
        }

        Recompute(card, logs, scheduler, scheduler, preserveSchedule: true);
    }

    /// <summary>
    /// The lapse count a history implies, for a card whose schedule comes from elsewhere and must not be
    /// overwritten. Replays onto a throwaway card so one implementation defines what a lapse is.
    /// </summary>
    public static int CountLapses(IReadOnlyList<FsrsReviewLog> logs, FsrsScheduler scheduler)
    {
        var probe = new FsrsCard("", 0, 0);
        Recompute(probe, logs, scheduler, scheduler, preserveTerminalState: false);
        return probe.Lapses;
    }

    /// <summary>Stored review timestamps are UTC; SQLite hands them back as Unspecified, which the scheduler rejects.</summary>
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
