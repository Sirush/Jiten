using FluentAssertions;
using Jiten.Core.Services;

namespace Jiten.Tests;

public class WordsForCoverageTests
{
    private static List<long> Words(params (int Rank, int Occurrences)[] words) =>
        words.Select(w => ((long)w.Rank << 32) | (uint)w.Occurrences).ToList();

    private static int[] Thresholds(List<long> words, params double[] levels)
    {
        var total = words.Sum(w => w & 0xFFFFFFFF);
        var result = new int[levels.Length];
        WordsForCoverage.ThresholdRanks(words, total, levels, result);
        return result;
    }

    [Fact]
    public void ThresholdRanks_ReturnsRankWhereCumulativeShareCrossesEachLevel()
    {
        var words = Words((300, 10), (1, 50), (20, 30), (5000, 10));

        Thresholds(words, 0.5, 0.8, 0.9, 1.0).Should().Equal(1, 20, 300, 5000);
    }

    [Fact]
    public void ThresholdRanks_SeveralLevelsCrossedByOneWord_ShareItsRank()
    {
        var words = Words((7, 95), (900, 5));

        Thresholds(words, 0.85, 0.9, 0.95, 0.99).Should().Equal(7, 7, 7, 900);
    }

    [Fact]
    public void ThresholdRanks_UnrankedWordsSortLast()
    {
        var words = Words((int.MaxValue, 50), (10, 50));

        Thresholds(words, 0.5, 0.9).Should().Equal(10, int.MaxValue);
    }

    [Fact]
    public void Median_InterpolatesEvenCounts()
    {
        WordsForCoverage.Median([4, 1, 3, 2]).Should().Be(2.5);
        WordsForCoverage.Median([5, 1, 3]).Should().Be(3);
    }
}
