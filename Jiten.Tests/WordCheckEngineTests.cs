using FluentAssertions;
using Jiten.Api.Services.WordCheck;

namespace Jiten.Tests;

public class WordCheckEngineTests
{
    private static readonly int[] ProbeRanks = [150, 300, 500, 800, 1200, 1800, 2500, 3500, 5000, 7000, 9000, 11000];

    /// <summary>Three answers per rank: all known below <paramref name="edge"/>, none above.</summary>
    private static List<WordCheckObservation> KnownBelow(int edge)
        => ProbeRanks.SelectMany(rank => Enumerable.Repeat(new WordCheckObservation(rank, rank < edge), 3)).ToList();

    [Theory]
    [InlineData(WordCheckLevel.Beginner, 500)]
    [InlineData(WordCheckLevel.Intermediate, 2000)]
    [InlineData(WordCheckLevel.Advanced, 5000)]
    [InlineData(null, 1000)]
    public void FirstWindow_SurroundsTheLevelStart(WordCheckLevel? level, int startRank)
    {
        var (min, max) = WordCheckEngine.FirstWindow(level);

        min.Should().BeLessThan(startRank);
        max.Should().BeGreaterThan(startRank).And.BeLessThanOrEqualTo(WordCheckEngine.MaxProbeRank);
    }

    [Fact]
    public void NextWindow_SurroundsTheEstimate()
    {
        var (min, max) = WordCheckEngine.NextWindow(2000);

        min.Should().BeInRange(500, 1999);
        max.Should().BeInRange(2001, 5000);
    }

    [Fact]
    public void NextWindow_AtTheTop_AsksTheHardestWords()
        => WordCheckEngine.NextWindow(WordCheckEngine.MaxCutoffRank).Should().Be((5000, WordCheckEngine.MaxProbeRank));

    [Fact]
    public void NextWindow_NothingKnownYet_StaysOnTheMostCommonWords()
        => WordCheckEngine.NextWindow(0).Max.Should().BeLessThan(500);

    [Fact]
    public void ProbeQuestions_CountsEveryRoundWithItsTraps()
        => WordCheckEngine.ProbeQuestions.Should().Be(38);

    [Fact]
    public void TrapSlots_LeaveRealWordsFirstAndBetweenTraps()
    {
        var random = new Random(1);
        for (var run = 0; run < 500; run++)
        {
            var slots = WordCheckEngine.TrapSlots(WordCheckEngine.WordsPerRound, 2, random);

            slots.Should().HaveCount(2).And.OnlyHaveUniqueItems().And.BeInAscendingOrder();
            slots.Should().OnlyContain(s => s >= WordCheckEngine.RealWordsBeforeTrap && s <= WordCheckEngine.WordsPerRound);
        }
    }

    [Fact]
    public void TrapSlots_ShortRound_StillPlacesATrap()
        => WordCheckEngine.TrapSlots(1, 1, new Random(1)).Should().Equal(1);

    [Fact]
    public void Cutoff_EverythingKnown_ReachesTheTop()
        => WordCheckEstimator.Cutoff(KnownBelow(int.MaxValue), 0).Should().Be(WordCheckEngine.MaxCutoffRank);

    [Fact]
    public void Cutoff_NothingKnown_MarksNothing()
        => WordCheckEstimator.Cutoff(KnownBelow(0), 0).Should().Be(0);

    [Fact]
    public void Cutoff_LandsNearTheEdgeOfWhatIsKnown()
        => WordCheckEstimator.Cutoff(KnownBelow(3000), 0).Should().BeInRange(1800, 3500);

    [Fact]
    public void Cutoff_RisesWithTheEdge()
        => WordCheckEstimator.Cutoff(KnownBelow(6000), 0).Should().BeGreaterThan(WordCheckEstimator.Cutoff(KnownBelow(1500), 0));

    [Fact]
    public void Cutoff_OneMissOnACommonWord_BarelyMovesTheResult()
    {
        var clean = KnownBelow(3000);
        var withMiss = clean.Append(new WordCheckObservation(300, false)).ToList();

        WordCheckEstimator.Cutoff(withMiss, 0).Should().BeGreaterThan(WordCheckEstimator.Cutoff(clean, 0) * 3 / 4);
    }

    [Fact]
    public void Cutoff_ScatteredKnowledgeAboveTheEdge_ExtendsTheRange()
    {
        var sharp = KnownBelow(2000);
        var scattered = sharp.Select(o => o.Rank is 2500 or 3500 ? o with { Known = true } : o).ToList();

        WordCheckEstimator.Cutoff(scattered, 0).Should().BeGreaterThan(WordCheckEstimator.Cutoff(sharp, 0));
    }

    [Fact]
    public void Cutoff_FalseAlarmsLowerTheResult()
        => WordCheckEstimator.Cutoff(KnownBelow(3000), 0.3).Should().BeLessThan(WordCheckEstimator.Cutoff(KnownBelow(3000), 0));

    [Fact]
    public void Cutoff_MissesNearTheEdge_LowerTheResult()
    {
        var before = KnownBelow(3000);
        var after = before.Concat(Enumerable.Repeat(new WordCheckObservation(2200, false), 5)).ToList();

        WordCheckEstimator.Cutoff(after, 0).Should().BeLessThan(WordCheckEstimator.Cutoff(before, 0));
    }

    [Fact]
    public void FalseAlarmRate_IsTheShareOfTrapsClaimed()
    {
        WordCheckEngine.FalseAlarmRate(0, 0).Should().Be(0);
        WordCheckEngine.FalseAlarmRate(6, 2).Should().BeApproximately(1 / 3.0, 1e-9);
    }
}
