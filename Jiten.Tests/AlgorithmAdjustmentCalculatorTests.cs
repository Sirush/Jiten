using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Core.Difficulty;

namespace Jiten.Tests;

public class AlgorithmAdjustmentCalculatorTests
{
    // Expected values are corrected minus raw for real decks, from eval/refit_correction.py in the JapaneseDifficulty repo.
    [Theory]
    [InlineData(MediaType.Anime, 1.12, 1128, 59198, 0.39)]
    [InlineData(MediaType.Anime, 1.68, 191, 7610, -1.09)]
    [InlineData(MediaType.Anime, 4.26, 1506, 90361, 0.00)]
    [InlineData(MediaType.VisualNovel, 4.43, 3108, 812087, -0.11)]
    [InlineData(MediaType.VisualNovel, 2.51, 1237, 440077, -0.71)]
    [InlineData(MediaType.Novel, 3.11, 934, 47683, -0.84)]
    [InlineData(MediaType.Manga, 2.05, 982, 47082, 0.01)]
    public void Compute_MatchesFittedReference(MediaType mediaType, double model, int uniqueKanji, int characters, double expected)
    {
        var adjustment = AlgorithmAdjustmentCalculator.Compute(mediaType, (decimal)model, uniqueKanji, characters);

        adjustment.Should().BeApproximately((decimal)expected, 0.02m);
    }

    [Fact]
    public void Compute_MoreKanjiVarietyAtSameLength_IsHarder()
    {
        var sparse = AlgorithmAdjustmentCalculator.Compute(MediaType.Novel, 2.5m, 1200, 300_000);
        var rich = AlgorithmAdjustmentCalculator.Compute(MediaType.Novel, 2.5m, 2600, 300_000);

        rich.Should().BeGreaterThan(sparse);
    }

    [Fact]
    public void Compute_HardDeck_GetsNoRatingsOffset()
    {
        // Above 3.5 the offset is fully faded, so prose and anime differ only by their intercepts.
        var prose = AlgorithmAdjustmentCalculator.Compute(MediaType.Novel, 4.6m, 3200, 400_000);
        var anime = AlgorithmAdjustmentCalculator.Compute(MediaType.Anime, 4.6m, 3200, 400_000);

        (anime - prose).Should().BeApproximately(0.45m, 0.02m);
    }

    [Theory]
    [InlineData(0.0, 10, 100)]
    [InlineData(0.2, 15, 5_000_000)]
    [InlineData(5.0, 4000, 200)]
    [InlineData(4.9, 3500, 2_000_000)]
    public void Compute_KeepsTheResultOnTheScale(double model, int uniqueKanji, int characters)
    {
        foreach (var mediaType in Enum.GetValues<MediaType>())
        {
            var adjustment = AlgorithmAdjustmentCalculator.Compute(mediaType, (decimal)model, uniqueKanji, characters);

            ((decimal)model + adjustment).Should().BeInRange(0m, 5m);
        }
    }

    [Fact]
    public void Compute_DeckWithoutText_IsLeftAlone()
    {
        AlgorithmAdjustmentCalculator.Compute(MediaType.Novel, 2.5m, 0, 0).Should().Be(0m);
    }

    [Theory]
    [InlineData(4.8, 0.5, 5.0)]
    [InlineData(0.3, -0.9, 0.0)]
    [InlineData(2.0, 0.4, 2.4)]
    public void Apply_ClampsToTheScale(double value, double adjustment, double expected)
    {
        AlgorithmAdjustmentCalculator.Apply((decimal)value, (decimal)adjustment).Should().Be((decimal)expected);
    }
}
