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

    public readonly record struct ReviewEntry(long CardId, DateTime ReviewUtc, bool IsAgain);

    public record RetentionBucket(int Total, int Passed)
    {
        public double? Retention => Total > 0 ? (double)Passed / Total : null;
    }

    public record RetentionWindow(RetentionBucket Overall, RetentionBucket Young, RetentionBucket Mature);

    public record PeriodRetention(string Period, RetentionBucket Overall, RetentionBucket Young, RetentionBucket Mature);

    public record RetentionResult(
        RetentionWindow Last30,
        RetentionWindow Last90,
        RetentionWindow AllTime,
        IReadOnlyList<PeriodRetention> Weekly,
        IReadOnlyList<PeriodRetention> Monthly);

    private sealed class Accumulator
    {
        public int OverallTotal, OverallPassed;
        public int YoungTotal, YoungPassed;
        public int MatureTotal, MaturePassed;

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

        // First qualifying review per (card, local day) only.
        var seenPerDay = new HashSet<(long, long)>();

        foreach (var cardGroup in logs.GroupBy(l => l.CardId))
        {
            DateTime? previous = null;
            foreach (var entry in cardGroup.OrderBy(l => l.ReviewUtc))
            {
                if (previous is { } prev)
                {
                    var elapsedDays = (entry.ReviewUtc - prev).TotalDays;
                    if (elapsedDays >= 1)
                    {
                        var localDate = entry.ReviewUtc.AddHours(offsetHours).Date;
                        if (seenPerDay.Add((entry.CardId, localDate.Ticks)))
                        {
                            var passed = !entry.IsAgain;
                            var mature = elapsedDays >= MatureThresholdDays;

                            all.Add(passed, mature);
                            if (entry.ReviewUtc >= last30Cutoff) last30.Add(passed, mature);
                            if (entry.ReviewUtc >= last90Cutoff) last90.Add(passed, mature);

                            var monthKey = localDate.ToString("yyyy-MM");
                            if (!monthly.TryGetValue(monthKey, out var macc))
                                monthly[monthKey] = macc = new Accumulator();
                            macc.Add(passed, mature);

                            // Week keyed by its Monday (local), so labels are stable, sortable dates.
                            var weekStart = localDate.AddDays(-(((int)localDate.DayOfWeek + 6) % 7));
                            var weekKey = weekStart.ToString("yyyy-MM-dd");
                            if (!weekly.TryGetValue(weekKey, out var wacc))
                                weekly[weekKey] = wacc = new Accumulator();
                            wacc.Add(passed, mature);
                        }
                    }
                }

                previous = entry.ReviewUtc;
            }
        }

        static List<PeriodRetention> ToSeries(SortedDictionary<string, Accumulator> buckets) => buckets
            .Select(kv => new PeriodRetention(
                kv.Key,
                new RetentionBucket(kv.Value.OverallTotal, kv.Value.OverallPassed),
                new RetentionBucket(kv.Value.YoungTotal, kv.Value.YoungPassed),
                new RetentionBucket(kv.Value.MatureTotal, kv.Value.MaturePassed)))
            .ToList();

        return new RetentionResult(last30.ToWindow(), last90.ToWindow(), all.ToWindow(), ToSeries(weekly), ToSeries(monthly));
    }
}
