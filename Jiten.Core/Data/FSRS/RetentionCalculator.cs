namespace Jiten.Core.Data.FSRS;

/// <summary>
/// Computes measured ("true") retention from review history.
///
/// A review only counts toward retention when at least one full day has elapsed
/// since that card's previous review. This filters out same-day learning and
/// relearning steps as well as new-card first exposures, leaving the reviews that
/// actually test recall — the same approach Anki uses for its true-retention stat,
/// derived from the log sequence since we do not persist the pre-review state.
///
/// Pass = any rating other than Again. Maturity is taken from the elapsed interval:
/// young is 1–20 days, mature is 21 days or more.
/// </summary>
public static class RetentionCalculator
{
    public const int MatureThresholdDays = 21;

    /// <param name="Rating">FSRS rating 1..4 (Again..Easy). Only used for the answer-button and per-period grade tallies.</param>
    /// <param name="DurationMs">Review duration in milliseconds, when recorded.</param>
    public readonly record struct ReviewEntry(long CardId, DateTime ReviewUtc, bool IsAgain, int Rating = 0, int? DurationMs = null);

    public record RetentionBucket(int Total, int Passed)
    {
        public double? Retention => Total > 0 ? (double)Passed / Total : null;
    }

    public record RetentionWindow(RetentionBucket Overall, RetentionBucket Young, RetentionBucket Mature);

    /// <param name="Grades">Every rated review in the period, retention-qualifying or not, indexed [again, hard, good, easy].</param>
    public record PeriodRetention(string Period, RetentionBucket Overall, RetentionBucket Young, RetentionBucket Mature, int[] Grades);

    /// <summary>Per-category answer-button tallies, indexed [again, hard, good, easy].</summary>
    public record AnswerButtons(int[] Learning, int[] Young, int[] Mature);

    /// <summary>One per local hour (0..23). Count is every review; Retention only the retention-qualifying ones.</summary>
    public record HourlyBucket(int Count, RetentionBucket Retention);

    public record ReviewTimeStats(int[] Buckets, double? AverageSeconds, double TotalHours, int Count);

    /// <summary>The three time-window views (trailing 30/90 days, all-time) of a per-window block.</summary>
    public record StatWindows<T>(T Last30, T Last90, T All);

    /// <summary>User-local "today" rollup. Reviews and Again count every press; Retention only the retention-qualifying ones.</summary>
    public record TodayStats(int Reviews, int Again, RetentionBucket Retention, int Minutes, int NewCards);

    public record RetentionResult(
        RetentionWindow Last30,
        RetentionWindow Last90,
        RetentionWindow AllTime,
        IReadOnlyList<PeriodRetention> Weekly,
        IReadOnlyList<PeriodRetention> Monthly,
        StatWindows<AnswerButtons> AnswerButtons,
        StatWindows<IReadOnlyList<HourlyBucket>> Hourly,
        StatWindows<ReviewTimeStats> ReviewTime,
        TodayStats Today);

    // Review-time histogram edges in seconds: 0–1, 1–2, 2–3, 3–5, 5–8, 8–12, 12–20, 20–30, 30–60, 60+.
    private static readonly double[] ReviewTimeEdgesSeconds = [1, 2, 3, 5, 8, 12, 20, 30, 60];
    private const double ReviewTimeCapSeconds = 120;

    private sealed class Accumulator
    {
        public int OverallTotal, OverallPassed;
        public int YoungTotal, YoungPassed;
        public int MatureTotal, MaturePassed;
        public readonly int[] Grades = new int[4];

        public void Add(bool passed, bool mature)
        {
            OverallTotal++;
            if (passed) OverallPassed++;
            if (mature)
            {
                MatureTotal++;
                if (passed) MaturePassed++;
            }
            else
            {
                YoungTotal++;
                if (passed) YoungPassed++;
            }
        }

        public RetentionWindow ToWindow() => new(
            new RetentionBucket(OverallTotal, OverallPassed),
            new RetentionBucket(YoungTotal, YoungPassed),
            new RetentionBucket(MatureTotal, MaturePassed));
    }

    private sealed class AnswerButtonAccumulator
    {
        private readonly int[] _learning = new int[4];
        private readonly int[] _young = new int[4];
        private readonly int[] _mature = new int[4];

        /// <param name="category">0 = learning, 1 = young, 2 = mature.</param>
        public void Add(int category, int ratingIndex)
        {
            var target = category == 0 ? _learning : category == 2 ? _mature : _young;
            target[ratingIndex]++;
        }

        public AnswerButtons ToResult() => new(_learning, _young, _mature);
    }

    private sealed class HourlyAccumulator
    {
        private readonly int[] _counts = new int[24];
        private readonly int[] _retentionTotal = new int[24];
        private readonly int[] _retentionPassed = new int[24];

        public void AddReview(int hour) => _counts[hour]++;

        public void AddRetention(int hour, bool passed)
        {
            _retentionTotal[hour]++;
            if (passed) _retentionPassed[hour]++;
        }

        public IReadOnlyList<HourlyBucket> ToResult() =>
            Enumerable.Range(0, 24)
                .Select(h => new HourlyBucket(_counts[h], new RetentionBucket(_retentionTotal[h], _retentionPassed[h])))
                .ToList();
    }

    /// <param name="logs">All review logs for the user (any order).</param>
    /// <param name="offsetHours">User-local timezone offset, for day/month bucketing.</param>
    /// <param name="nowUtc">Current time, defining the trailing 30/90-day windows.</param>
    public static RetentionResult Compute(IEnumerable<ReviewEntry> logs, double offsetHours, DateTime nowUtc)
    {
        var last30Cutoff = nowUtc.AddDays(-30);
        var last90Cutoff = nowUtc.AddDays(-90);

        var all = new Accumulator();
        var last30 = new Accumulator();
        var last90 = new Accumulator();
        var weekly = new SortedDictionary<string, Accumulator>(StringComparer.Ordinal);
        var monthly = new SortedDictionary<string, Accumulator>(StringComparer.Ordinal);

        // Answer-button tallies per category, indexed [again, hard, good, easy], one set per window.
        var btnAll = new AnswerButtonAccumulator();
        var btn90 = new AnswerButtonAccumulator();
        var btn30 = new AnswerButtonAccumulator();

        var hourlyAll = new HourlyAccumulator();
        var hourly90 = new HourlyAccumulator();
        var hourly30 = new HourlyAccumulator();

        var localToday = nowUtc.AddHours(offsetHours).Date;
        int todayRetentionTotal = 0, todayRetentionPassed = 0;

        // First qualifying review per (card, local day) only.
        var seenPerDay = new HashSet<(long, long)>();

        var materialized = logs as IReadOnlyCollection<ReviewEntry> ?? logs.ToList();

        foreach (var cardGroup in materialized.GroupBy(l => l.CardId))
        {
            DateTime? previous = null;
            foreach (var entry in cardGroup.OrderBy(l => l.ReviewUtc))
            {
                var localTime = entry.ReviewUtc.AddHours(offsetHours);
                var localDate = localTime.Date;
                var in30 = entry.ReviewUtc >= last30Cutoff;
                var in90 = entry.ReviewUtc >= last90Cutoff;

                hourlyAll.AddReview(localTime.Hour);
                if (in90) hourly90.AddReview(localTime.Hour);
                if (in30) hourly30.AddReview(localTime.Hour);

                var ratingIndex = entry.Rating is >= 1 and <= 4 ? entry.Rating - 1 : -1;
                if (ratingIndex >= 0)
                {
                    // 0 = learning (first review or <1d gap), 1 = young, 2 = mature.
                    int category;
                    if (previous is not { } p2)
                        category = 0;
                    else
                    {
                        var gap = (entry.ReviewUtc - p2).TotalDays;
                        category = gap < 1 ? 0 : gap >= MatureThresholdDays ? 2 : 1;
                    }

                    btnAll.Add(category, ratingIndex);
                    if (in90) btn90.Add(category, ratingIndex);
                    if (in30) btn30.Add(category, ratingIndex);

                    PeriodBucket(monthly, MonthKey(localDate)).Grades[ratingIndex]++;
                    PeriodBucket(weekly, WeekKey(localDate)).Grades[ratingIndex]++;
                }

                if (previous is { } prev)
                {
                    var elapsedDays = (entry.ReviewUtc - prev).TotalDays;
                    if (elapsedDays >= 1 && seenPerDay.Add((entry.CardId, localDate.Ticks)))
                    {
                        var passed = !entry.IsAgain;
                        var mature = elapsedDays >= MatureThresholdDays;

                        all.Add(passed, mature);
                        if (in30) last30.Add(passed, mature);
                        if (in90) last90.Add(passed, mature);

                        PeriodBucket(monthly, MonthKey(localDate)).Add(passed, mature);
                        PeriodBucket(weekly, WeekKey(localDate)).Add(passed, mature);

                        hourlyAll.AddRetention(localTime.Hour, passed);
                        if (in90) hourly90.AddRetention(localTime.Hour, passed);
                        if (in30) hourly30.AddRetention(localTime.Hour, passed);

                        if (localDate == localToday)
                        {
                            todayRetentionTotal++;
                            if (passed) todayRetentionPassed++;
                        }
                    }
                }

                previous = entry.ReviewUtc;
            }
        }

        static string MonthKey(DateTime localDate) => localDate.ToString("yyyy-MM");

        // Week keyed by its Monday (local), so labels are stable, sortable dates.
        static string WeekKey(DateTime localDate) =>
            localDate.AddDays(-(((int)localDate.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd");

        static Accumulator PeriodBucket(SortedDictionary<string, Accumulator> buckets, string key)
        {
            if (!buckets.TryGetValue(key, out var acc))
                buckets[key] = acc = new Accumulator();
            return acc;
        }

        // Periods with presses but no retention-qualifying review would plot as empty points.
        static List<PeriodRetention> ToSeries(SortedDictionary<string, Accumulator> buckets) => buckets
            .Where(kv => kv.Value.OverallTotal > 0)
            .Select(kv => new PeriodRetention(
                kv.Key,
                new RetentionBucket(kv.Value.OverallTotal, kv.Value.OverallPassed),
                new RetentionBucket(kv.Value.YoungTotal, kv.Value.YoungPassed),
                new RetentionBucket(kv.Value.MatureTotal, kv.Value.MaturePassed),
                kv.Value.Grades))
            .ToList();

        var answerButtons = new StatWindows<AnswerButtons>(btn30.ToResult(), btn90.ToResult(), btnAll.ToResult());

        var hourly = new StatWindows<IReadOnlyList<HourlyBucket>>(hourly30.ToResult(), hourly90.ToResult(), hourlyAll.ToResult());

        var reviewTime = new StatWindows<ReviewTimeStats>(
            ComputeReviewTime(materialized.Where(e => e.ReviewUtc >= last30Cutoff)),
            ComputeReviewTime(materialized.Where(e => e.ReviewUtc >= last90Cutoff)),
            ComputeReviewTime(materialized));

        var today = ComputeToday(materialized, offsetHours, localToday, new RetentionBucket(todayRetentionTotal, todayRetentionPassed));

        return new RetentionResult(
            last30.ToWindow(), last90.ToWindow(), all.ToWindow(), ToSeries(weekly), ToSeries(monthly),
            answerButtons, hourly, reviewTime, today);
    }

    private static ReviewTimeStats ComputeReviewTime(IEnumerable<ReviewEntry> logs)
    {
        var buckets = new int[ReviewTimeEdgesSeconds.Length + 1];
        var count = 0;
        double cappedSecondsSum = 0;
        double rawSecondsSum = 0;
        foreach (var entry in logs)
        {
            if (entry.DurationMs is not { } ms) continue;
            count++;
            var seconds = ms / 1000.0;
            rawSecondsSum += seconds;
            cappedSecondsSum += Math.Min(seconds, ReviewTimeCapSeconds);

            var bucket = ReviewTimeEdgesSeconds.Length;
            for (var i = 0; i < ReviewTimeEdgesSeconds.Length; i++)
            {
                if (seconds < ReviewTimeEdgesSeconds[i]) { bucket = i; break; }
            }
            buckets[bucket]++;
        }

        return new ReviewTimeStats(
            buckets,
            count > 0 ? cappedSecondsSum / count : null,
            rawSecondsSum / 3600.0,
            count);
    }

    private static TodayStats ComputeToday(IEnumerable<ReviewEntry> logs, double offsetHours, DateTime localToday, RetentionBucket retention)
    {
        var reviews = 0;
        var again = 0;
        long durationMsSum = 0;

        // A card counts as "new today" when its first-ever review falls today.
        var firstReviewPerCard = new Dictionary<long, DateTime>();
        foreach (var entry in logs)
        {
            if (!firstReviewPerCard.TryGetValue(entry.CardId, out var existing) || entry.ReviewUtc < existing)
                firstReviewPerCard[entry.CardId] = entry.ReviewUtc;

            if (entry.ReviewUtc.AddHours(offsetHours).Date != localToday) continue;
            reviews++;
            if (entry.IsAgain) again++;
            if (entry.DurationMs is { } ms) durationMsSum += ms;
        }

        var newCards = firstReviewPerCard.Values.Count(first => first.AddHours(offsetHours).Date == localToday);

        return new TodayStats(
            reviews,
            again,
            retention,
            (int)Math.Round(durationMsSum / 60000.0),
            newCards);
    }
}
