using Jiten.Core.Data.FSRS;

namespace Jiten.Tests;

public class FsrsWorkloadSimulatorTests
{
    private static readonly DateTime Now = new(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc);
    private readonly double[] _parameters = FsrsConstants.DefaultParameters;

    private static FsrsScheduler Scheduler(double retention, double[] parameters) =>
        new(desiredRetention: retention, parameters: parameters, enableFuzzing: false);

    private static FsrsCard ReviewCard(double stability, double difficulty = 5.0) => new()
    {
        State = FsrsState.Review,
        Step = null,
        Stability = stability,
        Difficulty = difficulty,
        LastReview = Now.AddDays(-stability),
        Due = Now,
    };

    private static FsrsCard NewLearningCard(DateTime due) => new()
    {
        State = FsrsState.Learning,
        Step = 0,
        Stability = null,
        Difficulty = null,
        LastReview = null,
        Due = due,
    };

    [Fact]
    public void EmptyPopulation_ReturnsZero()
    {
        var avg = FsrsWorkloadSimulator.AverageReviewsPerDay([], Scheduler(0.9, _parameters), 365, Now);
        Assert.Equal(0, avg);
    }

    [Fact]
    public void IsDeterministic_SameInputsSameResult()
    {
        var cards = Enumerable.Range(0, 100).Select(_ => ReviewCard(10)).ToList();

        var a = FsrsWorkloadSimulator.AverageReviewsPerDay(cards, Scheduler(0.9, _parameters), 365, Now);
        var b = FsrsWorkloadSimulator.AverageReviewsPerDay(cards, Scheduler(0.9, _parameters), 365, Now);

        Assert.Equal(a, b);
    }

    [Fact]
    public void HigherRetention_GeneratesMoreReviews()
    {
        var cards = Enumerable.Range(0, 300).Select(_ => ReviewCard(10)).ToList();

        var low = FsrsWorkloadSimulator.AverageReviewsPerDay(cards, Scheduler(0.80, _parameters), 365, Now, passes: 8);
        var mid = FsrsWorkloadSimulator.AverageReviewsPerDay(cards, Scheduler(0.90, _parameters), 365, Now, passes: 8);
        var high = FsrsWorkloadSimulator.AverageReviewsPerDay(cards, Scheduler(0.95, _parameters), 365, Now, passes: 8);

        Assert.True(low < mid, $"expected {low} < {mid}");
        Assert.True(mid < high, $"expected {mid} < {high}");
    }

    [Fact]
    public void NewLearningCard_GeneratesReviews()
    {
        var cards = new List<FsrsCard> { NewLearningCard(Now) };

        var avg = FsrsWorkloadSimulator.AverageReviewsPerDay(cards, Scheduler(0.9, _parameters), 365, Now, passes: 8);

        Assert.True(avg > 0, "a brand-new learning card should generate at least one review over a year");
    }

    [Fact]
    public void OverdueCard_IsReviewedWithinHorizon()
    {
        var overdue = ReviewCard(10);
        overdue.Due = Now.AddDays(-30); // long overdue
        overdue.LastReview = Now.AddDays(-40);

        var avg = FsrsWorkloadSimulator.AverageReviewsPerDay([overdue], Scheduler(0.9, _parameters), 365, Now, passes: 8);

        Assert.True(avg > 0, "an overdue card should still produce reviews inside the horizon");
    }

    [Fact]
    public void Project_ReviewsPerDay_MatchesLegacyHelper()
    {
        var cards = Enumerable.Range(0, 100).Select(_ => ReviewCard(10)).ToList();

        var projection = FsrsWorkloadSimulator.Project(cards, Scheduler(0.9, _parameters), 365, Now);
        var legacy = FsrsWorkloadSimulator.AverageReviewsPerDay(cards, Scheduler(0.9, _parameters), 365, Now);

        Assert.Equal(legacy, projection.ReviewsPerDay);
    }

    [Fact]
    public void Project_HigherRetention_MemorizesMore()
    {
        var cards = Enumerable.Range(0, 300).Select(_ => ReviewCard(10)).ToList();

        var low = FsrsWorkloadSimulator.Project(cards, Scheduler(0.80, _parameters), 365, Now, passes: 8);
        var high = FsrsWorkloadSimulator.Project(cards, Scheduler(0.95, _parameters), 365, Now, passes: 8);

        Assert.True(low.Memorized < high.Memorized, $"expected {low.Memorized} < {high.Memorized}");
        // Memorized is a stock, bounded above by the population size.
        Assert.True(high.Memorized <= cards.Count, $"expected {high.Memorized} <= {cards.Count}");
    }

    [Fact]
    public void Project_MaturityBuckets_SumToTotalReviews()
    {
        var cards = Enumerable.Range(0, 300).Select(_ => ReviewCard(10)).ToList();

        var p = FsrsWorkloadSimulator.Project(cards, Scheduler(0.9, _parameters), 365, Now, passes: 8);

        Assert.Equal(p.ReviewsPerDay, p.LearningPerDay + p.YoungPerDay + p.MaturePerDay, 6);
    }

    [Fact]
    public void Project_LongIntervalCards_CountAsMature()
    {
        // Cards last reviewed 60 days ago with a 60-day stability stay on ~60-day intervals at 0.9, so every
        // review's gap is well past the 21-day mature threshold.
        var cards = Enumerable.Range(0, 200).Select(_ =>
        {
            var c = ReviewCard(60);
            c.LastReview = Now.AddDays(-60);
            return c;
        }).ToList();

        var p = FsrsWorkloadSimulator.Project(cards, Scheduler(0.9, _parameters), 365, Now, passes: 8);

        Assert.True(p.MaturePerDay > p.YoungPerDay + p.LearningPerDay,
            $"expected mature {p.MaturePerDay} to dominate young {p.YoungPerDay} + learning {p.LearningPerDay}");
    }

    [Fact]
    public void Workload_ScalesRoughlyLinearlyWithPopulation()
    {
        var n = Enumerable.Range(0, 200).Select(_ => ReviewCard(10)).ToList();
        var twoN = Enumerable.Range(0, 400).Select(_ => ReviewCard(10)).ToList();

        var avgN = FsrsWorkloadSimulator.AverageReviewsPerDay(n, Scheduler(0.9, _parameters), 365, Now, passes: 8);
        var avg2N = FsrsWorkloadSimulator.AverageReviewsPerDay(twoN, Scheduler(0.9, _parameters), 365, Now, passes: 8);

        var ratio = avg2N / avgN;
        Assert.InRange(ratio, 1.8, 2.2);
    }
}
