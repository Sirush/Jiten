using FluentAssertions;
using Jiten.Core.Data.FSRS;

namespace Jiten.Tests;

public class FsrsV7PropertyTests
{
    private static readonly double[] W = FsrsConstants.DefaultParametersV7;

    private static readonly FsrsMemoryStateV7[] States =
    [
        new(0.2, 7.0, 0.1),
        new(2.5, 5.0, 1.0),
        new(15.0, 3.0, 4.0),
        new(120.0, 8.0, 60.0),
        new(3000.0, 1.5, 3000.0),
    ];

    private static readonly double[] Retentions = [0.5, 0.7, 0.8, 0.85, 0.9, 0.95, 0.99];

    [Fact]
    public void NextInterval_ReachesDesiredRetention()
    {
        foreach (var state in States)
        foreach (var dr in Retentions)
        {
            var t = FsrsHelperV7.NextInterval(state, dr, W);
            // Heavy-tailed curves can stay above a low target for longer than the 100-year stability cap.
            if (FsrsHelperV7.Retrievability(FsrsHelperV7.StabilityMax, state, W) > dr)
                t.Should().BeApproximately(FsrsHelperV7.StabilityMax, 1e-6, $"{state} @ {dr}");
            else
                FsrsHelperV7.Retrievability(t, state, W).Should().BeApproximately(dr, 1e-3, $"{state} @ {dr}");
        }
    }

    [Fact]
    public void NextInterval_ShrinksAsDesiredRetentionRises()
    {
        foreach (var state in States)
        {
            var intervals = Retentions.Select(dr => FsrsHelperV7.NextInterval(state, dr, W)).ToArray();
            intervals.Should().BeInDescendingOrder(state.ToString());
        }
    }

    [Fact]
    public void NextInterval_GrowsWithStability()
    {
        var intervals = new[] { 0.5, 2, 10, 50, 250, 1000 }
                        .Select(s => FsrsHelperV7.NextInterval(new FsrsMemoryStateV7(s, 5, s * 0.8), 0.9, W))
                        .ToArray();
        intervals.Should().BeInAscendingOrder();
    }

    [Fact]
    public void NextInterval_IsZeroAtTheRetentionCeiling()
        => FsrsHelperV7.NextInterval(States[2], 0.9999, W).Should().Be(0);

    [Fact]
    public void NegativeElapsedTime_ActsAsZero()
    {
        foreach (var state in States)
        foreach (var rating in Enum.GetValues<FsrsRating>())
            FsrsHelperV7.NextState(state, -3, rating, W).Should().Be(FsrsHelperV7.NextState(state, 0, rating, W));
    }

    [Fact]
    public void Again_CapsFastTraceBelowSlowTrace()
    {
        foreach (var state in States)
        foreach (var deltaT in new[] { 0.0, 0.01, 1, 30 })
        {
            var next = FsrsHelperV7.NextState(state, deltaT, FsrsRating.Again, W);
            next.StabilityFast.Should().BeLessThanOrEqualTo(Math.Max(next.Stability * 0.8, FsrsHelperV7.StabilityMin) + 1e-12, $"{state} dt={deltaT}");
        }
    }

    [Fact]
    public void Again_NeverRaisesStability()
    {
        foreach (var state in States)
            FsrsHelperV7.NextState(state, 5, FsrsRating.Again, W).Stability.Should().BeLessThanOrEqualTo(state.Stability);
    }

    [Fact]
    public void InitialState_FastTraceStartsAtEightyPercent()
    {
        foreach (var rating in Enum.GetValues<FsrsRating>())
        {
            var state = FsrsHelperV7.InitialState(rating, W);
            state.Stability.Should().Be(W[(int)rating - 1]);
            state.StabilityFast.Should().BeApproximately(state.Stability * 0.8, 1e-12);
        }
    }

    /// <summary>Default w31 &lt; 0.5 and w32 &gt; 0.3 both make a harder card forget faster.</summary>
    [Fact]
    public void HigherDifficulty_LowersRecallUnderDefaults()
    {
        foreach (var t in new[] { 1.0, 10, 60 })
        {
            var easy = FsrsHelperV7.Retrievability(t, new FsrsMemoryStateV7(20, 2, 5), W);
            var hard = FsrsHelperV7.Retrievability(t, new FsrsMemoryStateV7(20, 9, 5), W);
            hard.Should().BeLessThan(easy, $"t={t}");
        }
    }

    [Fact]
    public void Retrievability_StaysInsideTheSquashedRange()
    {
        foreach (var state in States)
        foreach (var t in new[] { 0.0, 1e-6, 1, 1e5 })
            FsrsHelperV7.Retrievability(t, state, W).Should().BeInRange(1e-5, 1 - 1e-5);
    }

    [Fact]
    public void Derivative_MatchesFiniteDifference()
    {
        foreach (var state in States)
        foreach (var t in new[] { 0.01, 1, 20, 400 })
        {
            const double h = 1e-6;
            var numeric = (FsrsHelperV7.Retrievability(t + h, state, W) - FsrsHelperV7.Retrievability(t - h, state, W)) / (2 * h);
            FsrsHelperV7.RetrievabilityAndDerivative(t, state, W).Derivative
                        .Should().BeApproximately(numeric, Math.Max(Math.Abs(numeric) * 1e-4, 1e-9), $"{state} t={t}");
        }
    }

    [Fact]
    public void ClampParameters_LeavesDefaultsUntouched()
    {
        var w = W.ToArray();
        FsrsHelperV7.ClampParameters(w);
        w.Should().Equal(W);
    }

    [Fact]
    public void ClampParameters_RepairsNonFiniteAndOrdering()
    {
        var w = W.ToArray();
        w[5] = double.NaN;
        w[2] = 0.01;
        w[25] = 0.8;
        w[26] = 0.3;

        FsrsHelperV7.ClampParameters(w);

        w[5].Should().Be(0.001);
        w[2].Should().Be(w[1]);
        w[26].Should().Be(0.8);
    }
}
