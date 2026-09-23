using System.Diagnostics;
using FluentAssertions;
using Jiten.Core.Data.FSRS;

namespace Jiten.Tests;

public class FsrsSchedulerV7Tests
{
    private static readonly double[] W = FsrsConstants.DefaultParametersV7;
    private static readonly DateTime Start = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private static FsrsScheduler Scheduler(bool fuzz = false) => new(parameters: W, enableFuzzing: fuzz);

    [Fact]
    public void Version_FollowsParameterCount()
    {
        Scheduler().Version.Should().Be(FsrsVersion.V7);
        new FsrsScheduler().Version.Should().Be(FsrsVersion.V6);
    }

    [Fact]
    public void FirstReview_UsesTheInitialState()
    {
        var (card, _) = Scheduler().ReviewCard(new FsrsCard("u", 1, 0), FsrsRating.Good, Start);
        var expected = FsrsHelperV7.InitialState(FsrsRating.Good, W);

        card.Stability.Should().Be(expected.Stability);
        card.Difficulty.Should().Be(expected.Difficulty);
        card.StabilityFast.Should().Be(expected.StabilityFast);
    }

    [Fact]
    public void Reviews_FollowTheModelWithFractionalElapsedTime()
    {
        var scheduler = Scheduler();
        var times = new[] { Start, Start.AddMinutes(10), Start.AddHours(30), Start.AddDays(5.5) };
        var ratings = new[] { FsrsRating.Again, FsrsRating.Good, FsrsRating.Good, FsrsRating.Hard };

        var card = new FsrsCard("u", 1, 0);
        var state = FsrsHelperV7.InitialState(ratings[0], W);
        card = scheduler.ReviewCard(card, ratings[0], times[0]).UpdatedCard;
        for (var i = 1; i < times.Length; i++)
        {
            state = FsrsHelperV7.NextState(state, (times[i] - times[i - 1]).TotalDays, ratings[i], W);
            card = scheduler.ReviewCard(card, ratings[i], times[i]).UpdatedCard;
            card.Stability.Should().BeApproximately(state.Stability, 1e-12, $"review {i}");
            card.StabilityFast.Should().BeApproximately(state.StabilityFast, 1e-12, $"review {i}");
            card.Difficulty.Should().BeApproximately(state.Difficulty, 1e-12, $"review {i}");
        }
    }

    [Fact]
    public void ReviewInterval_IsTheFractionalSolveOfTheFullState()
    {
        var scheduler = Scheduler();
        var card = new FsrsCard("u", 1, 0, state: FsrsState.Review, stability: 12, difficulty: 7, lastReview: Start)
                   {
                       StabilityFast = 3
                   };

        var (updated, _) = scheduler.ReviewCard(card, FsrsRating.Good, Start.AddDays(10));
        var solve = FsrsHelperV7.NextInterval(FsrsHelperV7.FromCard(updated), scheduler.DesiredRetention, W);

        (updated.Due - Start.AddDays(10)).TotalDays.Should().BeApproximately(solve, 1e-6);
        solve.Should().NotBe(Math.Round(solve));
    }

    [Fact]
    public void ReviewInterval_CanFallUnderADay()
    {
        var scheduler = new FsrsScheduler(parameters: W, desiredRetention: 0.95, enableFuzzing: false, relearningSteps: []);
        var card = new FsrsCard("u", 1, 0, state: FsrsState.Review, stability: 2, difficulty: 8, lastReview: Start) { StabilityFast = 1 };

        var (updated, _) = scheduler.ReviewCard(card, FsrsRating.Again, Start.AddDays(2));

        (updated.Due - Start.AddDays(2)).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(1)).And.BeLessThan(TimeSpan.FromDays(1));
    }

    [Fact]
    public void ReviewInterval_NeverFallsUnderAMinute()
    {
        var scheduler = new FsrsScheduler(parameters: W, desiredRetention: 0.99, enableFuzzing: false, learningSteps: []);

        var (updated, _) = scheduler.ReviewCard(new FsrsCard("u", 1, 0), FsrsRating.Again, Start);

        (updated.Due - Start).Should().BeCloseTo(TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void PreviewIntervals_LeaveTheCardUntouched()
    {
        var card = new FsrsCard("u", 1, 0, state: FsrsState.Review, stability: 12, difficulty: 7, lastReview: Start) { StabilityFast = 3 };
        var before = card.Clone();

        var preview = Scheduler().PreviewIntervals(card, Start.AddDays(4));

        preview.Should().HaveCount(4);
        preview[FsrsRating.Easy].Should().BeGreaterThanOrEqualTo(preview[FsrsRating.Good]);
        card.Should().BeEquivalentTo(before, o => o.Excluding(c => c.ReviewLogs));
    }

    [Fact]
    public void Retrievability_AndStabilityDays_UseTheFullState()
    {
        var scheduler = Scheduler();
        var card = new FsrsCard("u", 1, 0, state: FsrsState.Review, stability: 20, difficulty: 8, lastReview: Start) { StabilityFast = 4 };
        var state = FsrsHelperV7.FromCard(card);

        scheduler.GetCardRetrievability(card, Start.AddDays(3)).Should().BeApproximately(FsrsHelperV7.Retrievability(3, state, W), 1e-12);
        scheduler.GetStabilityDays(card).Should().BeApproximately(FsrsHelperV7.S90(state, W), 1e-12);
        scheduler.GetStabilityDays(new FsrsCard("u", 2, 0)).Should().Be(0);
    }

    [Fact]
    public void LegacyCardWithoutFastTrace_StartsFromTheInitialRatio()
    {
        var card = new FsrsCard("u", 1, 0, state: FsrsState.Review, stability: 10, difficulty: 5, lastReview: Start);
        FsrsHelperV7.FromCard(card).StabilityFast.Should().Be(8);

        var (updated, _) = Scheduler().ReviewCard(card, FsrsRating.Good, Start.AddDays(7));
        updated.StabilityFast.Should().NotBeNull();
    }

    [Fact]
    public void StatesOnlyReplay_KeepsTheSchedule()
    {
        var logs = new List<FsrsReviewLog>
        {
            new(1, FsrsRating.Good, Start),
            new(1, FsrsRating.Good, Start.AddDays(2)),
            new(1, FsrsRating.Again, Start.AddDays(9)),
        };
        var due = Start.AddDays(30);
        var card = new FsrsCard("u", 1, 0, state: FsrsState.Relearning, step: 0, stability: 99, difficulty: 2, due: due, lastReview: Start.AddDays(9));

        FsrsReplay.Recompute(card, logs, Scheduler(), Scheduler(), preserveSchedule: true).Should().BeTrue();

        card.Due.Should().Be(due);
        card.State.Should().Be(FsrsState.Relearning);
        card.Step.Should().Be(0);
        card.Stability.Should().NotBe(99);
        card.StabilityFast.Should().NotBeNull();
        card.Lapses.Should().Be(1);
    }

    [Fact]
    public void AdoptMemoryModel_DropsTheFastTraceUnderFsrs6()
    {
        var card = new FsrsCard("u", 1, 0, state: FsrsState.Review, stability: 10, difficulty: 5) { StabilityFast = 3 };
        FsrsReplay.AdoptMemoryModel(card, [], new FsrsScheduler());
        card.StabilityFast.Should().BeNull();
        card.Stability.Should().Be(10);
    }

    [Fact]
    public void WorkloadSimulator_RunsUnderFsrs7InReasonableTime()
    {
        var rng = new Random(5);
        var seeds = Enumerable.Range(0, 2000).Select(i =>
        {
            var s = rng.NextDouble() * 60 + 0.5;
            var last = Start.AddDays(-rng.Next(0, 30));
            return new FsrsCard("u", i, 0, state: FsrsState.Review, stability: s, difficulty: rng.NextDouble() * 9 + 1,
                                due: last.AddDays(s), lastReview: last) { StabilityFast = s * 0.5 };
        }).ToList();

        var sw = Stopwatch.StartNew();
        var projection = FsrsWorkloadSimulator.Project(seeds, Scheduler(), 180, Start);
        sw.Stop();

        projection.ReviewsPerDay.Should().BeGreaterThan(0);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }
}
