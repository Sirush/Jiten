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
    public static bool Recompute(FsrsCard card, IReadOnlyList<FsrsReviewLog> logs,
                                 FsrsScheduler scheduler, FsrsScheduler replayScheduler,
                                 bool preserveTerminalState = true)
    {
        if (logs.Count == 0)
            return false;

        var overrideState = preserveTerminalState
                            && card.State is FsrsState.Mastered or FsrsState.Blacklisted or FsrsState.Suspended
            ? card.State
            : (FsrsState?)null;

        var ordered = logs.OrderBy(l => l.ReviewDateTime).ThenBy(l => l.ReviewLogId).ToList();

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

        card.State = overrideState ?? tempCard.State;
        card.Step = tempCard.Step;
        card.Stability = tempCard.Stability;
        card.Difficulty = tempCard.Difficulty;
        card.Due = tempCard.Due;
        card.LastReview = tempCard.LastReview;
        card.Lapses = lapses;

        return true;
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
