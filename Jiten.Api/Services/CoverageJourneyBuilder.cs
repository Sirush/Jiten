using Jiten.Api.Dtos;

namespace Jiten.Api.Services;

public readonly record struct DeckWordEntry(int WordId, byte ReadingIndex, int Occurrences);

/// <summary>
/// One card (or word-set membership) holding one maturity state over <c>[Start, End)</c>; <c>End</c> null
/// means it still holds. A card's segments are contiguous and non-overlapping, so it counts once at any
/// instant. Cards produce at most three: young, mature, then a live tail that differs only after a lapse.
/// </summary>
public readonly record struct KnownSegment(DateOnly Start, DateOnly? End, bool IsMature);

/// <summary>
/// Pure series construction for the coverage journey. Kept free of EF and IO so the coverage
/// arithmetic can be unit-tested directly.
/// </summary>
public static class CoverageJourneyBuilder
{
    /// <summary>Beyond this many points the chart is unreadable and the payload pointless; the range start is folded instead.</summary>
    public const int MaxPoints = 200;

    private const int WeeklySpanLimitDays = 370;

    /// <summary>Trailing window for the growth headline delta; a fixed span, not a bucket count, so weekly and monthly series report the same period.</summary>
    public const int RecentGainDays = 30;

    private static readonly int[] MilestoneThresholds = [50, 60, 75, 80, 85, 90, 95, 98];

    /// <summary>
    /// Collapses one pair's segments into non-overlapping intervals, mature winning any overlap. A word with
    /// several kanji forms is reached by one card per form once kana forms are expanded, and live coverage
    /// unions those, so segments left appended would count that word's occurrences once per card.
    /// </summary>
    public static List<KnownSegment> MergePairSegments(List<KnownSegment> segments)
    {
        if (segments.Count < 2)
            return segments;

        var mature = MergeRuns(segments, true);
        var young = SubtractRuns(MergeRuns(segments, false), mature);

        var merged = new List<KnownSegment>(mature.Count + young.Count);
        foreach (var run in mature) merged.Add(ToSegment(run, true));
        foreach (var run in young) merged.Add(ToSegment(run, false));
        return merged;
    }

    /// <summary>An open-ended segment carries <see cref="int.MaxValue"/> as its end so runs compare as plain ints.</summary>
    private static List<(int Start, int End)> MergeRuns(List<KnownSegment> segments, bool isMature)
    {
        var runs = new List<(int Start, int End)>();
        foreach (var segment in segments)
        {
            if (segment.IsMature != isMature) continue;

            var end = segment.End?.DayNumber ?? int.MaxValue;
            if (segment.Start.DayNumber < end)
                runs.Add((segment.Start.DayNumber, end));
        }

        if (runs.Count < 2)
            return runs;

        runs.Sort();
        var merged = new List<(int Start, int End)>(runs.Count);
        var current = runs[0];
        for (var i = 1; i < runs.Count; i++)
        {
            if (runs[i].Start <= current.End)
            {
                current = (current.Start, Math.Max(current.End, runs[i].End));
                continue;
            }

            merged.Add(current);
            current = runs[i];
        }

        merged.Add(current);
        return merged;
    }

    private static List<(int Start, int End)> SubtractRuns(List<(int Start, int End)> runs, List<(int Start, int End)> holes)
    {
        if (holes.Count == 0 || runs.Count == 0)
            return runs;

        var result = new List<(int Start, int End)>(runs.Count);
        foreach (var run in runs)
        {
            var start = run.Start;
            foreach (var hole in holes)
            {
                if (hole.End <= start) continue;
                if (hole.Start >= run.End) break;

                if (hole.Start > start) result.Add((start, hole.Start));
                start = hole.End;
                if (start >= run.End) break;
            }

            if (start < run.End) result.Add((start, run.End));
        }

        return result;
    }

    private static KnownSegment ToSegment((int Start, int End) run, bool isMature) =>
        new(DateOnly.FromDayNumber(run.Start), run.End == int.MaxValue ? null : DateOnly.FromDayNumber(run.End), isMature);

    public static JourneyDto BuildDeckJourney(
        int deckId,
        IReadOnlyList<DeckWordEntry> deckWords,
        IReadOnlyDictionary<(int WordId, byte ReadingIndex), List<KnownSegment>> segmentsByPair,
        int wordCount,
        int uniqueWordCount,
        DateOnly today)
    {
        var dto = new JourneyDto { DeckId = deckId };

        var matched = new List<(int Occurrences, List<KnownSegment> Segments)>();
        DateOnly? earliest = null;
        foreach (var dw in deckWords)
        {
            if (!segmentsByPair.TryGetValue((dw.WordId, dw.ReadingIndex), out var segments) || segments.Count == 0)
                continue;

            matched.Add((dw.Occurrences, segments));
            foreach (var segment in segments)
                if (earliest is null || segment.Start < earliest) earliest = segment.Start;
        }

        if (earliest is null)
            return dto;

        var buckets = BuildBuckets(earliest.Value, today);
        dto.Granularity = buckets.GranularityName;
        var count = buckets.Starts.Count;

        var matureOcc = new long[count + 1];
        var matureUnique = new int[count + 1];
        var youngOcc = new long[count + 1];
        var youngUnique = new int[count + 1];

        foreach (var (occurrences, segments) in matched)
        {
            foreach (var segment in segments)
            {
                if (!buckets.TryRange(segment, out var from, out var to)) continue;

                if (segment.IsMature)
                {
                    matureOcc[from] += occurrences;
                    matureOcc[to] -= occurrences;
                    matureUnique[from]++;
                    matureUnique[to]--;
                }
                else
                {
                    youngOcc[from] += occurrences;
                    youngOcc[to] -= occurrences;
                    youngUnique[from]++;
                    youngUnique[to]--;
                }
            }
        }

        long runMatureOcc = 0, runYoungOcc = 0;
        int runMatureUnique = 0, runYoungUnique = 0;

        for (var i = 0; i < count; i++)
        {
            runMatureOcc += matureOcc[i];
            runYoungOcc += youngOcc[i];
            runMatureUnique += matureUnique[i];
            runYoungUnique += youngUnique[i];

            dto.Points.Add(new JourneyPointDto
            {
                Date = buckets.Starts[i],
                Coverage = Percent(runMatureOcc, wordCount),
                CombinedCoverage = Percent(runMatureOcc + runYoungOcc, wordCount),
                UniqueCoverage = Percent(runMatureUnique, uniqueWordCount),
                CombinedUniqueCoverage = Percent(runMatureUnique + runYoungUnique, uniqueWordCount),
                KnownWords = runMatureUnique,
                KnownWordsCombined = runMatureUnique + runYoungUnique
            });
        }

        dto.Milestones = BuildMilestones(dto.Points);

        var first = dto.Points[0];
        var last = dto.Points[^1];
        dto.StartDate = first.Date;
        dto.StartCoverage = first.Coverage;
        dto.CurrentCoverage = last.Coverage;
        dto.StartUniqueCoverage = first.UniqueCoverage;
        dto.CurrentUniqueCoverage = last.UniqueCoverage;
        dto.HasEnoughHistory = dto.Points.Count >= 2;

        return dto;
    }

    /// <summary>
    /// Cards in each maturity state as of the end of every bucket. A real state history rather than
    /// today's known set back-dated, so it declines when cards lapse.
    /// </summary>
    public static GlobalGrowthDto BuildGlobalGrowth(IReadOnlyList<KnownSegment> segments, DateOnly today)
    {
        var dto = new GlobalGrowthDto();
        if (segments.Count == 0)
            return dto;

        var start = segments.Min(s => s.Start);
        var buckets = BuildBuckets(start, today);
        dto.Granularity = buckets.GranularityName;

        var count = buckets.Starts.Count;
        var matureDelta = new int[count + 1];
        var youngDelta = new int[count + 1];

        foreach (var segment in segments)
        {
            if (!buckets.TryRange(segment, out var from, out var to)) continue;

            var delta = segment.IsMature ? matureDelta : youngDelta;
            delta[from]++;
            delta[to]--;
        }

        int mature = 0, young = 0;
        for (var i = 0; i < count; i++)
        {
            mature += matureDelta[i];
            young += youngDelta[i];
            dto.Points.Add(new GrowthPointDto
            {
                Date = buckets.Starts[i],
                KnownWords = mature,
                KnownWordsCombined = mature + young
            });
        }

        dto.HasEnoughHistory = dto.Points.Count >= 2;
        dto.RecentGain = CountKnownOn(segments, today) - CountKnownOn(segments, today.AddDays(-RecentGainDays));
        return dto;
    }

    private static int CountKnownOn(IReadOnlyList<KnownSegment> segments, DateOnly day)
    {
        var count = 0;
        foreach (var segment in segments)
        {
            if (segment.Start > day) continue;
            if (segment.End is { } end && end <= day) continue;
            count++;
        }

        return count;
    }

    private static List<JourneyMilestoneDto> BuildMilestones(IReadOnlyList<JourneyPointDto> points)
    {
        var milestones = new List<JourneyMilestoneDto>();
        foreach (var threshold in MilestoneThresholds)
        {
            AddFirstCrossing(threshold, false, p => p.Coverage);
            AddFirstCrossing(threshold, true, p => p.UniqueCoverage);
        }

        return milestones;

        void AddFirstCrossing(int threshold, bool unique, Func<JourneyPointDto, float> metric)
        {
            foreach (var point in points)
            {
                if (metric(point) < threshold) continue;
                milestones.Add(new JourneyMilestoneDto { Threshold = threshold, ReachedAt = point.Date, Unique = unique });
                return;
            }
        }
    }

    private static float Percent(long known, int total)
    {
        if (total <= 0) return 0f;
        // The live coverage SQL clamps too: some decks' WordCount disagrees with the sum of DeckWords.Occurrences.
        return (float)Math.Min(known * 100.0 / total, 100.0);
    }

    private sealed class BucketRange
    {
        public required bool Weekly { get; init; }
        public required List<DateOnly> Starts { get; init; }

        /// <summary>Each bucket is read on its own last day, so the final bucket reports the state as of today.</summary>
        public required DateOnly[] EvalDates { get; init; }

        public string GranularityName => Weekly ? "weekly" : "monthly";

        /// <summary>Half-open bucket range a segment is observed in; false when no bucket-end falls inside it.</summary>
        public bool TryRange(KnownSegment segment, out int from, out int to)
        {
            from = FirstBucketAtOrAfter(segment.Start);
            to = segment.End is { } end ? FirstBucketAtOrAfter(end) : Starts.Count;
            return from < to;
        }

        private int FirstBucketAtOrAfter(DateOnly date)
        {
            int lo = 0, hi = EvalDates.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (EvalDates[mid] < date) lo = mid + 1;
                else hi = mid;
            }

            return lo;
        }
    }

    private static BucketRange BuildBuckets(DateOnly start, DateOnly today)
    {
        if (start > today) start = today;

        var weekly = today.DayNumber - start.DayNumber < WeeklySpanLimitDays;
        var starts = new List<DateOnly>();

        if (weekly)
        {
            for (var d = WeekStart(start); d <= today; d = d.AddDays(7))
                starts.Add(d);
        }
        else
        {
            var first = MonthStart(start);
            var last = MonthStart(today);
            var months = ((last.Year - first.Year) * 12) + last.Month - first.Month + 1;
            // A history longer than MaxPoints months folds everything older into the first bucket rather
            // than dropping it, so the opening point still carries the coverage already reached by then.
            if (months > MaxPoints) first = last.AddMonths(-(MaxPoints - 1));
            for (var d = first; d <= last; d = d.AddMonths(1))
                starts.Add(d);
        }

        if (starts.Count == 0)
            starts.Add(weekly ? WeekStart(today) : MonthStart(today));

        var evalDates = new DateOnly[starts.Count];
        for (var i = 0; i < starts.Count; i++)
        {
            var next = weekly ? starts[i].AddDays(7) : starts[i].AddMonths(1);
            var last = next.AddDays(-1);
            evalDates[i] = last > today ? today : last;
        }

        return new BucketRange { Weekly = weekly, Starts = starts, EvalDates = evalDates };
    }

    private static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private static DateOnly MonthStart(DateOnly date) => new(date.Year, date.Month, 1);
}
