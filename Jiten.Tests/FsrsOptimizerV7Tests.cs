using FluentAssertions;
using Jiten.Core.Data.FSRS;

namespace Jiten.Tests;

public class FsrsOptimizerV7Tests
{
    private static List<FsrsTrainingItem> SampleItems(int cards, int seed)
    {
        var rng = new Random(seed);
        var items = new List<FsrsTrainingItem>();
        var start = new DateTime(2026, 1, 1).Ticks;
        for (var c = 0; c < cards; c++)
        {
            var length = rng.Next(2, 9);
            var reviews = new FsrsTrainingReview[length];
            reviews[0] = new FsrsTrainingReview(rng.Next(1, 5), 0);
            var elapsed = 0.0;
            for (var i = 1; i < length; i++)
            {
                var deltaT = rng.NextDouble() < 0.3 ? rng.NextDouble() * 0.05 : Math.Round(rng.NextDouble() * 40 + 1, 2);
                var rating = rng.NextDouble() < 0.2 ? 1 : rng.Next(2, 5);
                reviews[i] = new FsrsTrainingReview(rating, deltaT);
                elapsed += deltaT;
            }

            items.Add(new FsrsTrainingItem(reviews, start + (long)((c + elapsed) * TimeSpan.TicksPerDay)));
        }

        return items;
    }

    private static double[] PerturbedParameters()
    {
        var w = FsrsConstants.DefaultParametersV7.ToArray();
        // Off the neutral values and clip bounds, so every partial is live and a central difference stays inside the domain.
        w[8] = 0.3; w[29] = 0.2; w[16] = 0.9; w[31] = 0.6; w[32] = 0.4; w[33] = 0.45; w[14] = 1.2; w[22] = 1.3;
        return w;
    }

    [Fact]
    public void Gradients_MatchFiniteDifferences()
    {
        var items = SampleItems(40, 7);
        var w = PerturbedParameters();
        var (_, gradients) = FsrsOptimizerV7.WeightedLossAndGradients(items, w);

        for (var i = 0; i < w.Length; i++)
        {
            var h = Math.Max(Math.Abs(w[i]) * 1e-6, 1e-7);
            var plus = w.ToArray(); plus[i] += h;
            var minus = w.ToArray(); minus[i] -= h;
            var numeric = (FsrsOptimizerV7.WeightedLossAndGradients(items, plus).Loss - FsrsOptimizerV7.WeightedLossAndGradients(items, minus).Loss) / (2 * h);
            gradients[i].Should().BeApproximately(numeric, Math.Max(Math.Abs(numeric) * 1e-3, 1e-5), $"w{i}");
        }
    }

    [Fact]
    public void SchedulePenaltyGradients_MatchFiniteDifferences()
    {
        var w = PerturbedParameters();
        var gradients = FsrsOptimizerV7.SchedulePenaltyGradients(w);
        FsrsOptimizerV7.SchedulePenaltyValue(w).Should().BeGreaterThan(0);

        for (var i = 0; i < w.Length; i++)
        {
            var h = Math.Max(Math.Abs(w[i]) * 1e-5, 1e-6);
            var plus = w.ToArray(); plus[i] += h;
            var minus = w.ToArray(); minus[i] -= h;
            var numeric = (FsrsOptimizerV7.SchedulePenaltyValue(plus) - FsrsOptimizerV7.SchedulePenaltyValue(minus)) / (2 * h);
            gradients[i].Should().BeApproximately(numeric, Math.Max(Math.Abs(numeric) * 5e-2, 1e-4), $"w{i}");
        }
    }

    [Fact]
    public void Optimize_ReducesLossAndStaysInBounds()
    {
        var items = SampleItems(600, 11);
        var defaults = FsrsConstants.DefaultParametersV7;
        var result = FsrsOptimizerV7.Optimize(items);

        result.Parameters.Should().HaveCount(34);
        var clamped = result.Parameters.ToArray();
        FsrsHelperV7.ClampParameters(clamped);
        clamped.Should().Equal(result.Parameters);
        result.Parameters[..4].Should().BeInAscendingOrder();

        var trained = FsrsOptimizerV7.WeightedLossAndGradients(items, result.Parameters).Loss;
        var untrained = FsrsOptimizerV7.WeightedLossAndGradients(items, defaults).Loss;
        trained.Should().BeLessThan(untrained);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(12)]
    public void Optimize_FallsBackToDefaultsWithTooFewReviews(int cards)
    {
        var result = FsrsOptimizerV7.Optimize(SampleItems(cards, 3));

        result.ReviewCount.Should().BeLessThan(64);
        result.Parameters.Should().Equal(FsrsConstants.DefaultParametersV7);
    }

    [Fact]
    public void Optimize_FallsBackToDefaultsWhenEveryTargetIsAFirstLongTermReview()
    {
        var items = Enumerable.Range(0, 200)
                              .Select(c => new FsrsTrainingItem([new FsrsTrainingReview(3, 0), new FsrsTrainingReview(c % 5 == 0 ? 1 : 3, 1 + c % 9)],
                                                                (1_000L + c) * TimeSpan.TicksPerDay))
                              .ToList();

        FsrsOptimizerV7.Optimize(items).Parameters.Should().Equal(FsrsConstants.DefaultParametersV7);
    }
}
