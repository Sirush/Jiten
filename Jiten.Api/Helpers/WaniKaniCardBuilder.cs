using Jiten.Core.Data.FSRS;

namespace Jiten.Api.Helpers;

/// <param name="Reviews">Reviews WaniKani counted for the word, failed ones included.</param>
/// <param name="Incorrect">Wrong answers across meaning and reading; several can belong to one review.</param>
public sealed record WaniKaniProgress(
    int Stage,
    DateTime? StartedAt,
    DateTime? PassedAt,
    DateTime? BurnedAt,
    DateTime? AvailableAt,
    int Reviews,
    int Incorrect);

/// <summary>WaniKani keeps no review log, so a card's history is rebuilt from its stage, its real milestone dates and its answer totals.</summary>
public static class WaniKaniCardBuilder
{
    public const int BurnedStage = 9;

    private const int GuruStage = 5;
    private const int MaxFailures = 40;

    /// <summary>Days WaniKani waits before the review at each stage, indexed by stage; stage 0 is a lesson.</summary>
    private static readonly double[] StageIntervalDays = [0, 4 / 24.0, 8 / 24.0, 1, 2, 7, 14, 30, 120];

    public static bool IsStudiedStage(int stage) => stage is >= 1 and <= BurnedStage;

    public static FsrsCard Build(string userId, (int WordId, byte ReadingIndex) form, WaniKaniProgress progress, DateTime now, FsrsScheduler scheduler)
    {
        var card = new FsrsCard(userId, form.WordId, form.ReadingIndex);
        var logs = new List<FsrsReviewLog>();
        foreach (var (rating, at) in ReconstructReviews(progress, now))
        {
            var review = scheduler.ReviewCard(card, rating, at);
            card = review.UpdatedCard;
            logs.Add(review.ReviewLog);
        }

        card.Lapses = FsrsReplay.CountLapses(logs, scheduler);
        if (progress.Stage >= BurnedStage)
            card.State = FsrsState.Mastered;

        card.ReviewLogs = logs;
        foreach (var log in logs)
            log.Card = card;
        return card;
    }

    /// <summary>
    /// The lesson, then the shortest climb to the current stage with the failures the totals imply, each failure followed by the review that regained the stage.
    /// Dates run from the lesson to the last review, through the Guru pass when known, spaced in proportion to WaniKani's intervals.
    /// </summary>
    public static List<(FsrsRating Rating, DateTime At)> ReconstructReviews(WaniKaniProgress progress, DateTime now)
    {
        var stage = Math.Clamp(progress.Stage, 1, BurnedStage);

        var surplusReviews = Math.Max(0, progress.Reviews - (stage - 1));
        var failures = Math.Min(Math.Min(Math.Max(0, progress.Incorrect), MaxFailures), stage == 1 ? surplusReviews : surplusReviews / 2);
        var failuresAt = new int[BurnedStage + 1];
        if (stage == 1)
            failuresAt[1] = failures;
        else
        {
            var failableStages = Math.Min(stage, BurnedStage - 1) - 1;
            for (var i = 0; i < failures; i++)
                failuresAt[2 + i % failableStages]++;
        }

        var steps = new List<(FsrsRating Rating, double Offset)> { (FsrsRating.Good, 0) };
        var offset = 0.0;
        double? guruOffset = null;
        var current = 1;
        while (true)
        {
            for (var i = 0; i < failuresAt[current]; i++)
            {
                offset += StageIntervalDays[current];
                steps.Add((FsrsRating.Again, offset));
                if (current == 1) continue;
                offset += StageIntervalDays[current - 1];
                steps.Add((FsrsRating.Good, offset));
            }

            if (current == stage) break;

            offset += StageIntervalDays[current];
            steps.Add((FsrsRating.Good, offset));
            current++;
            if (current == GuruStage) guruOffset ??= offset;
        }

        var span = offset;
        var end = Utc(stage >= BurnedStage ? progress.BurnedAt : progress.AvailableAt?.AddDays(-StageIntervalDays[stage]));
        var start = Utc(progress.StartedAt);
        if (end > now) end = now;
        if (start > now) start = null;
        end ??= start is { } known ? Min(known.AddDays(span), now) : now;
        if (start == null || start >= end)
            start = span > 0 ? end.Value.AddDays(-span) : end;

        var guru = Utc(progress.PassedAt);
        var hasGuruAnchor = guruOffset is > 0 && guruOffset < span && guru > start && guru < end;

        DateTime At(double stepOffset)
        {
            if (span <= 0) return start.Value;
            if (!hasGuruAnchor) return Lerp(start.Value, end.Value, stepOffset / span);
            return stepOffset <= guruOffset!.Value
                ? Lerp(start.Value, guru!.Value, stepOffset / guruOffset.Value)
                : Lerp(guru!.Value, end.Value, (stepOffset - guruOffset.Value) / (span - guruOffset.Value));
        }

        var reviews = new List<(FsrsRating Rating, DateTime At)>(steps.Count);
        DateTime? previous = null;
        foreach (var (rating, stepOffset) in steps)
        {
            var at = TruncateToSecond(At(stepOffset));
            if (previous != null && at <= previous) at = previous.Value.AddSeconds(1);
            reviews.Add((rating, at));
            previous = at;
        }

        return reviews;
    }

    private static DateTime? Utc(DateTime? value) => value?.ToUniversalTime();

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static DateTime Lerp(DateTime from, DateTime to, double fraction) => from + (to - from) * Math.Clamp(fraction, 0, 1);

    private static DateTime TruncateToSecond(DateTime value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
}
