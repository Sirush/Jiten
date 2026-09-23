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

    private static FsrsTrainingItem Item(long lastReviewTicks, params (int Rating, double DeltaT)[] reviews)
        => new(reviews.Select(r => new FsrsTrainingReview(r.Rating, r.DeltaT)).ToArray(), lastReviewTicks);

    [Fact]
    public void FilterOutliers_CutsRareAndLongFirstGapsBackToTheirSameDayReviews()
    {
        const long end = 1_000 * TimeSpan.TicksPerDay;
        var common = Enumerable.Range(0, 200).Select(_ => Item(end, (3, 0), (3, 3.2), (3, 10))).ToList();
        var sameDayAfter = Enumerable.Range(0, 30).Select(_ => Item(end, (3, 0), (3, 0.01), (3, 7.5), (3, 0.02))).ToList();
        var rare = Enumerable.Range(0, 3).Select(_ => Item(end, (3, 0), (3, 0.02), (3, 50.5), (3, 5))).ToList();
        var longGap = Enumerable.Range(0, 30).Select(_ => Item(end, (3, 0), (3, 150), (3, 20))).ToList();
        var sameDayOnly = Item(end, (3, 0), (3, 0.01));
        var items = common.Concat(sameDayAfter).Concat(rare).Concat(longGap).Append(sameDayOnly).ToList();

        var filtered = FsrsOptimizerV7.FilterOutliers(items);

        filtered.Should().HaveCount(items.Count);
        filtered.Take(230).Should().Equal(common.Concat(sameDayAfter));
        filtered.Skip(230).Take(3).Should().AllSatisfy(i =>
        {
            i.Reviews.Should().Equal(new FsrsTrainingReview(3, 0), new FsrsTrainingReview(3, 0.02));
            i.LastReviewTicks.Should().Be(end - (long)(55.5 * TimeSpan.TicksPerDay));
        });
        filtered.Skip(233).Take(30).Should().AllSatisfy(i => i.Reviews.Should().Equal(new FsrsTrainingReview(3, 0)));
        filtered[^1].Should().Be(sameDayOnly);
    }

    [Fact]
    public void FilterOutliers_KeepsEasyGapsUpToAYear()
    {
        var items = Enumerable.Range(0, 100).Select(_ => Item(0, (4, 0), (4, 5)))
                              .Concat(Enumerable.Range(0, 30).Select(_ => Item(0, (4, 0), (4, 200))))
                              .ToList();

        FsrsOptimizerV7.FilterOutliers(items).Should().Equal(items);
    }

    [Fact]
    public void Optimize_FallsBackToDefaultsWithTooFewReviews()
        => FsrsOptimizerV7.Optimize(SampleItems(2, 3)).Parameters.Should().Equal(FsrsConstants.DefaultParametersV7);
}
