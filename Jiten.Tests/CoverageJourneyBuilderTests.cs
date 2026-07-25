using FluentAssertions;
using Jiten.Api.Services;

namespace Jiten.Tests;

public class CoverageJourneyBuilderTests
{
    private static readonly DateOnly Today = new(2026, 7, 24);

    /// <summary>Each word held one state from its date onwards, which is the shape most cards have.</summary>
    private static Dictionary<(int WordId, byte ReadingIndex), List<KnownSegment>> Learned(
        params (int WordId, DateOnly Date, bool Mature)[] entries) =>
        entries.ToDictionary(e => (e.WordId, (byte)0), e => new List<KnownSegment> { new(e.Date, null, e.Mature) });

    private static Dictionary<(int WordId, byte ReadingIndex), List<KnownSegment>> Timeline(
        int wordId, params KnownSegment[] segments) =>
        new() { [(wordId, (byte)0)] = segments.ToList() };

    private static List<DeckWordEntry> DeckWords(params (int WordId, int Occurrences)[] words) =>
        words.Select(w => new DeckWordEntry(w.WordId, 0, w.Occurrences)).ToList();

    [Fact]
    public void Series_IsMonotonic_AndEndsAtDirectlyComputedCoverage()
    {
        var deckWords = DeckWords((1, 50), (2, 30), (3, 20));
        var learned = Learned(
            (1, Today.AddDays(-300), true),
            (2, Today.AddDays(-100), true));

        var journey = CoverageJourneyBuilder.BuildDeckJourney(7, deckWords, learned, 100, 3, Today);

        journey.Points.Select(p => p.Coverage).Should().BeInAscendingOrder();
        journey.Points.Select(p => p.CombinedCoverage).Should().BeInAscendingOrder();
        journey.Points[^1].Coverage.Should().BeApproximately(80f, 0.01f);
        journey.Points[^1].UniqueCoverage.Should().BeApproximately(200f / 3f, 0.01f);
        journey.Points[^1].KnownWords.Should().Be(2);
        journey.CurrentCoverage.Should().BeApproximately(80f, 0.01f);
        journey.HasEnoughHistory.Should().BeTrue();
    }

    [Fact]
    public void YoungWords_CountOnlyTowardsTheCombinedSeries()
    {
        var deckWords = DeckWords((1, 40), (2, 60));
        var learned = Learned(
            (1, Today.AddDays(-60), true),
            (2, Today.AddDays(-30), false));

        var journey = CoverageJourneyBuilder.BuildDeckJourney(1, deckWords, learned, 100, 2, Today);

        journey.Points[^1].Coverage.Should().BeApproximately(40f, 0.01f);
        journey.Points[^1].CombinedCoverage.Should().BeApproximately(100f, 0.01f);
        journey.Points[^1].KnownWords.Should().Be(1);
        journey.Points[^1].KnownWordsCombined.Should().Be(2);
    }

    [Fact]
    public void Bucketing_IsWeeklyUnderAYearAndMonthlyBeyond()
    {
        var deckWords = DeckWords((1, 1));

        var shortJourney = CoverageJourneyBuilder.BuildDeckJourney(
            1, deckWords, Learned((1, Today.AddDays(-369), true)), 1, 1, Today);
        var longJourney = CoverageJourneyBuilder.BuildDeckJourney(
            1, deckWords, Learned((1, Today.AddDays(-370), true)), 1, 1, Today);

        shortJourney.Granularity.Should().Be("weekly");
        shortJourney.Points.Should().OnlyContain(p => p.Date.DayOfWeek == DayOfWeek.Monday);
        longJourney.Granularity.Should().Be("monthly");
        longJourney.Points.Should().OnlyContain(p => p.Date.Day == 1);
        longJourney.Points.Should().HaveCount(13);
    }

    [Fact]
    public void BucketsWithoutNewWords_StillEmitFlatPoints()
    {
        var deckWords = DeckWords((1, 50), (2, 50));
        var learned = Learned(
            (1, Today.AddDays(-56), true),
            (2, Today, true));

        var journey = CoverageJourneyBuilder.BuildDeckJourney(1, deckWords, learned, 100, 2, Today);

        journey.Points.Should().HaveCount(9);
        journey.Points.Take(8).Should().OnlyContain(p => Math.Abs(p.Coverage - 50f) < 0.01f);
        journey.Points[^1].Coverage.Should().BeApproximately(100f, 0.01f);
    }

    [Fact]
    public void Milestones_FireOncePerThresholdAtTheFirstBucketReachingIt()
    {
        var deckWords = DeckWords((1, 50), (2, 32), (3, 10), (4, 5));
        var learned = Learned(
            (1, Today.AddDays(-21), true),
            (2, Today.AddDays(-14), true),
            (3, Today.AddDays(-7), true),
            (4, Today, true));

        var journey = CoverageJourneyBuilder.BuildDeckJourney(1, deckWords, learned, 100, 4, Today);

        var total = journey.Milestones.Where(m => !m.Unique).ToList();
        // Coverage runs 50 -> 82 -> 92 -> 97, so 98 is never reached and several thresholds share a bucket.
        total.Select(m => m.Threshold).Should().Equal(50, 60, 75, 80, 85, 90, 95);
        total.Should().OnlyHaveUniqueItems(m => m.Threshold);
        total.Single(m => m.Threshold == 50).ReachedAt.Should().Be(journey.Points[0].Date);
        total.Single(m => m.Threshold == 80).ReachedAt.Should().Be(journey.Points[1].Date);
        total.Single(m => m.Threshold == 90).ReachedAt.Should().Be(journey.Points[2].Date);
        total.Single(m => m.Threshold == 95).ReachedAt.Should().Be(journey.Points[3].Date);
    }

    [Fact]
    public void Milestones_AreEmittedForBothMetrics()
    {
        var deckWords = DeckWords((1, 99), (2, 1));
        var learned = Learned((1, Today.AddDays(-14), true));

        var journey = CoverageJourneyBuilder.BuildDeckJourney(1, deckWords, learned, 100, 2, Today);

        journey.Milestones.Where(m => !m.Unique).Select(m => m.Threshold).Should().Equal(50, 60, 75, 80, 85, 90, 95, 98);
        // Half the deck's distinct words are known, so unique coverage only ever crosses 50.
        journey.Milestones.Where(m => m.Unique).Select(m => m.Threshold).Should().Equal(50);
    }

    [Fact]
    public void Coverage_IsClampedWhenOccurrencesExceedWordCount()
    {
        var deckWords = DeckWords((1, 120));
        var learned = Learned((1, Today.AddDays(-14), true));

        var journey = CoverageJourneyBuilder.BuildDeckJourney(1, deckWords, learned, 100, 1, Today);

        journey.Points[^1].Coverage.Should().Be(100f);
        journey.Points[^1].CombinedCoverage.Should().Be(100f);
    }

    [Fact]
    public void NoKnownWords_ReportsNoHistoryAndNoPoints()
    {
        var journey = CoverageJourneyBuilder.BuildDeckJourney(
            1, DeckWords((1, 10)), Learned(), 10, 1, Today);

        journey.HasEnoughHistory.Should().BeFalse();
        journey.Points.Should().BeEmpty();
        journey.StartDate.Should().BeNull();
        journey.CurrentCoverage.Should().Be(0);
    }

    [Fact]
    public void SingleBucket_ReportsNotEnoughHistory()
    {
        var journey = CoverageJourneyBuilder.BuildDeckJourney(
            1, DeckWords((1, 10)), Learned((1, Today, true)), 10, 1, Today);

        journey.Points.Should().HaveCount(1);
        journey.HasEnoughHistory.Should().BeFalse();
        journey.CurrentCoverage.Should().Be(100f);
    }

    [Fact]
    public void HistoriesLongerThanTheCap_FoldTheirTailIntoTheFirstBucket()
    {
        var deckWords = DeckWords((1, 60), (2, 40));
        var learned = Learned(
            (1, Today.AddYears(-25), true),
            (2, Today, true));

        var journey = CoverageJourneyBuilder.BuildDeckJourney(1, deckWords, learned, 100, 2, Today);

        journey.Points.Should().HaveCount(CoverageJourneyBuilder.MaxPoints);
        journey.Points[0].Coverage.Should().BeApproximately(60f, 0.01f);
        journey.Points[^1].Coverage.Should().BeApproximately(100f, 0.01f);
    }

    [Fact]
    public void ZeroWordCountDeck_ProducesZeroCoverageRatherThanDividingByZero()
    {
        var journey = CoverageJourneyBuilder.BuildDeckJourney(
            1, DeckWords((1, 0)), Learned((1, Today.AddDays(-14), true)), 0, 0, Today);

        journey.Points.Should().OnlyContain(p => p.Coverage == 0 && p.UniqueCoverage == 0);
    }

    [Fact]
    public void DeckCoverage_MovesFromYoungToMatureAtTheCrossing()
    {
        var deckWords = DeckWords((1, 60));
        var segments = Timeline(1,
            new KnownSegment(Today.AddDays(-21), Today.AddDays(-7), false),
            new KnownSegment(Today.AddDays(-7), null, true));

        var journey = CoverageJourneyBuilder.BuildDeckJourney(1, deckWords, segments, 100, 1, Today);

        // Counted in the combined band throughout, but only mature once its interval crossed.
        journey.Points.Select(p => p.Coverage).Should().Equal(0f, 0f, 60f, 60f);
        journey.Points.Select(p => p.CombinedCoverage).Should().Equal(60f, 60f, 60f, 60f);
    }

    [Fact]
    public void DeckCoverage_FallsWhenAWordLapses()
    {
        var deckWords = DeckWords((1, 40), (2, 30));
        var segments = new Dictionary<(int WordId, byte ReadingIndex), List<KnownSegment>>
        {
            [(1, 0)] =
            [
                new KnownSegment(Today.AddDays(-21), Today.AddDays(-7), true),
                new KnownSegment(Today.AddDays(-7), null, false)
            ],
            [(2, 0)] = [new KnownSegment(Today.AddDays(-21), null, true)]
        };

        var journey = CoverageJourneyBuilder.BuildDeckJourney(1, deckWords, segments, 100, 2, Today);

        journey.Points.Select(p => p.Coverage).Should().Equal(70f, 70f, 30f, 30f);
        // The lapsed word is still known, so the combined line holds.
        journey.Points.Select(p => p.CombinedCoverage).Should().Equal(70f, 70f, 70f, 70f);
        journey.CurrentCoverage.Should().Be(30f);
    }

    [Fact]
    public void MergePairSegments_CountsAWordReachedByTwoCardsOnce()
    {
        var deckWords = DeckWords((1, 40));
        // Two kanji forms of one word, both expanding onto the same kana pair.
        var segments = CoverageJourneyBuilder.MergePairSegments(
        [
            new KnownSegment(Today.AddDays(-21), null, true),
            new KnownSegment(Today.AddDays(-14), null, true)
        ]);

        var journey = CoverageJourneyBuilder.BuildDeckJourney(
            1, deckWords, new Dictionary<(int, byte), List<KnownSegment>> { [(1, 0)] = segments }, 100, 1, Today);

        journey.Points.Select(p => p.Coverage).Should().Equal(40f, 40f, 40f, 40f);
        journey.Points[^1].KnownWords.Should().Be(1);
    }

    [Fact]
    public void MergePairSegments_LetsMatureWinAnOverlapWithYoung()
    {
        var segments = CoverageJourneyBuilder.MergePairSegments(
        [
            new KnownSegment(Today.AddDays(-28), null, false),
            new KnownSegment(Today.AddDays(-14), null, true)
        ]);

        segments.Should().BeEquivalentTo(new[]
        {
            new KnownSegment(Today.AddDays(-14), null, true),
            new KnownSegment(Today.AddDays(-28), Today.AddDays(-14), false)
        });
    }

    [Fact]
    public void MergePairSegments_KeepsAYoungRunSplitAroundAClosedMatureRun()
    {
        var segments = CoverageJourneyBuilder.MergePairSegments(
        [
            new KnownSegment(Today.AddDays(-28), null, false),
            new KnownSegment(Today.AddDays(-21), Today.AddDays(-7), true)
        ]);

        segments.Should().BeEquivalentTo(new[]
        {
            new KnownSegment(Today.AddDays(-21), Today.AddDays(-7), true),
            new KnownSegment(Today.AddDays(-28), Today.AddDays(-21), false),
            new KnownSegment(Today.AddDays(-7), null, false)
        });
    }

    [Fact]
    public void MergePairSegments_LeavesOneCardsOwnTimelineAlone()
    {
        KnownSegment[] timeline =
        [
            new(Today.AddDays(-28), Today.AddDays(-14), false),
            new(Today.AddDays(-14), null, true)
        ];

        CoverageJourneyBuilder.MergePairSegments(timeline.ToList()).Should().BeEquivalentTo(timeline);
    }

    [Fact]
    public void GlobalGrowth_CountsCardsHoldingEachStateAtTheEndOfEveryBucket()
    {
        var segments = new List<KnownSegment>
        {
            new(Today.AddDays(-21), null, true),
            new(Today.AddDays(-14), null, true),
            new(Today.AddDays(-14), null, false),
            new(Today, null, true)
        };

        var growth = CoverageJourneyBuilder.BuildGlobalGrowth(segments, Today);

        growth.Granularity.Should().Be("weekly");
        growth.Points.Should().HaveCount(4);
        growth.Points.Select(p => p.KnownWords).Should().Equal(1, 2, 2, 3);
        growth.Points.Select(p => p.KnownWordsCombined).Should().Equal(1, 3, 3, 4);
        growth.HasEnoughHistory.Should().BeTrue();
    }

    [Fact]
    public void GlobalGrowth_FallsWhenACardLeavesMaturity()
    {
        var segments = new List<KnownSegment>
        {
            // Mature for two weeks, then young from a week ago onwards.
            new(Today.AddDays(-21), Today.AddDays(-7), true),
            new(Today.AddDays(-7), null, false)
        };

        var growth = CoverageJourneyBuilder.BuildGlobalGrowth(segments, Today);

        growth.Points.Select(p => p.KnownWords).Should().Equal(1, 1, 0, 0);
        // The card never stops being known, it just stops being mature.
        growth.Points.Select(p => p.KnownWordsCombined).Should().Equal(1, 1, 1, 1);
    }

    [Fact]
    public void GlobalGrowth_IgnoresSegmentsThatCloseBeforeTheFirstBucketIsRead()
    {
        var segments = new List<KnownSegment>
        {
            new(Today.AddDays(-21), null, true),
            // Opened and closed inside one bucket, so no bucket-end ever observes it.
            new(Today.AddDays(-20), Today.AddDays(-19), false)
        };

        var growth = CoverageJourneyBuilder.BuildGlobalGrowth(segments, Today);

        growth.Points.Select(p => p.KnownWordsCombined).Should().AllBeEquivalentTo(1);
    }

    [Fact]
    public void GlobalGrowth_WithNoKnownWords_IsEmpty()
    {
        var growth = CoverageJourneyBuilder.BuildGlobalGrowth([], Today);

        growth.Points.Should().BeEmpty();
        growth.HasEnoughHistory.Should().BeFalse();
    }
}
