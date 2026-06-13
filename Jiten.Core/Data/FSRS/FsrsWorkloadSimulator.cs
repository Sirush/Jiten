namespace Jiten.Core.Data.FSRS;

/// <summary>
/// Forward simulator that projects how a card population behaves over a horizon under a given desired
/// retention: how many reviews it generates per day (split by maturity), and how many cards are still
/// memorized at the end. Used to give "what-if" cost/benefit feedback on the desired-retention control.
///
/// It is a thin harness around <see cref="FsrsScheduler.ReviewCard"/>: each card is stepped forward
/// through its due dates over a horizon, drawing a pass/fail outcome from the card's actual retrievability
/// and letting the real scheduler advance stability / difficulty / state (including learning and relearning
/// sub-day steps). This guarantees the simulated dynamics match real scheduling exactly instead of
/// re-deriving the FSRS math. The simulator is pure (no DB, no wall clock) — the caller passes the clock and
/// seed so results are deterministic and reproducible.
///
/// Each review is bucketed by maturity using the inter-review gap (the same rule as
/// <see cref="RetentionCalculator"/>: &lt;1d = learning, ≥21d = mature, else young), so the caller can cost
/// reviews with per-maturity review speed measured the same way from real history.
/// </summary>
public static class FsrsWorkloadSimulator
{
    /// <summary>
    /// Number of Monte-Carlo passes over the population. The headline averages are sums of per-card
    /// outcomes so they are already stable; extra passes mainly smooth the pass/fail noise.
    /// </summary>
    public const int DefaultPasses = 4;

    private const int PerCardSafetyCap = 100_000;

    /// <summary>
    /// Projected workload and retained-knowledge for a population under one desired retention.
    /// The per-day figures are flows (generated demand, NOT capped by any daily limit) split by maturity;
    /// <see cref="ReviewsPerDay"/> is their sum. <see cref="Memorized"/> is a stock: the expected number of
    /// cards still recalled at the end of the horizon (sum of per-card retrievability).
    /// </summary>
    public readonly record struct WorkloadProjection(
        double ReviewsPerDay,
        double LearningPerDay,
        double YoungPerDay,
        double MaturePerDay,
        double Memorized);

    /// <summary>
    /// Projects reviews/day (by maturity) and memorized-at-horizon for the given seed cards under the
    /// retention configured on <paramref name="scheduler"/>.
    /// </summary>
    /// <param name="seeds">Working copies of the cards to project (existing cards and/or injected new cards).</param>
    /// <param name="scheduler">Scheduler carrying the candidate desired retention and parameters (fuzzing disabled).</param>
    /// <param name="horizonDays">How many days forward to simulate.</param>
    /// <param name="now">UTC simulation start.</param>
    /// <param name="passes">Monte-Carlo passes to average over.</param>
    /// <param name="seed">Base RNG seed (use the same seed across retentions so differences are purely from retention).</param>
    public static WorkloadProjection Project(
        IReadOnlyList<FsrsCard> seeds,
        FsrsScheduler scheduler,
        int horizonDays,
        DateTime now,
        int passes = DefaultPasses,
        int seed = 12345)
    {
        if (seeds.Count == 0 || horizonDays <= 0 || passes <= 0)
            return default;

        if (now.Kind != DateTimeKind.Utc)
            now = DateTime.SpecifyKind(now, DateTimeKind.Utc);

        var horizonEnd = now.AddDays(horizonDays);
        long totalLearning = 0, totalYoung = 0, totalMature = 0;
        double totalMemorized = 0;

        for (var pass = 0; pass < passes; pass++)
        {
            var rng = new Random(seed + pass);
            foreach (var card in seeds)
            {
                var (learning, young, mature, finalCard) = SimulateCard(card, scheduler, now, horizonEnd, rng);
                totalLearning += learning;
                totalYoung += young;
                totalMature += mature;
                totalMemorized += scheduler.GetCardRetrievability(finalCard, horizonEnd);
            }
        }

        double PerDay(long total) => (double)total / horizonDays / passes;
        return new WorkloadProjection(
            PerDay(totalLearning + totalYoung + totalMature),
            PerDay(totalLearning),
            PerDay(totalYoung),
            PerDay(totalMature),
            totalMemorized / passes);
    }

    /// <summary>
    /// Average number of reviews generated per day over the horizon. Back-compat convenience over
    /// <see cref="Project"/> for callers that only need the total workload flow.
    /// </summary>
    public static double AverageReviewsPerDay(
        IReadOnlyList<FsrsCard> seeds,
        FsrsScheduler scheduler,
        int horizonDays,
        DateTime now,
        int passes = DefaultPasses,
        int seed = 12345)
        => Project(seeds, scheduler, horizonDays, now, passes, seed).ReviewsPerDay;

    private static (long Learning, long Young, long Mature, FsrsCard FinalCard) SimulateCard(
        FsrsCard seed, FsrsScheduler scheduler, DateTime now, DateTime horizonEnd, Random rng)
    {
        var card = seed.Clone();
        var retention = scheduler.DesiredRetention;

        var clock = EnsureUtc(card.Due);
        if (clock < now)
            clock = now;

        long learning = 0, young = 0, mature = 0;
        var iterations = 0;

        while (clock <= horizonEnd && iterations++ < PerCardSafetyCap)
        {
            // Maturity from the inter-review gap (days since the card's previous review), matching
            // RetentionCalculator's stats exactly. card.LastReview holds the previous review instant here
            // (the real one before the sim starts; the simulated one on later iterations).
            if (card.LastReview is not { } prev)
            {
                learning++;
            }
            else
            {
                var gap = (clock - EnsureUtc(prev)).TotalDays;
                if (gap < 1) learning++;
                else if (gap >= RetentionCalculator.MatureThresholdDays) mature++;
                else young++;
            }

            // Draw pass/fail from the card's actual retrievability at review time (its position on the
            // forgetting curve), not a flat target rate — so overdue cards fail more and same-day learning
            // steps pass almost always. A first-ever exposure has no elapsed-time signal (LastReview == null
            // → retrievability 0), so fall back to the target rate there.
            var r = card.LastReview != null ? scheduler.GetCardRetrievability(card, clock) : retention;
            var passed = rng.NextDouble() < r;
            var rating = passed ? FsrsRating.Good : FsrsRating.Again;

            var (updated, _) = scheduler.ReviewCard(card, rating, clock);
            card = updated;

            if (card.Due == DateTime.MaxValue)
                break;

            var next = EnsureUtc(card.Due);
            // Guard against a non-advancing schedule (would otherwise spin on the same instant).
            if (next <= clock)
                next = clock.AddMinutes(1);
            clock = next;
        }

        return (learning, young, mature, card);
    }

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
