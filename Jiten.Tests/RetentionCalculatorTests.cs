using FluentAssertions;
using Jiten.Core.Data.FSRS;
using Xunit;

namespace Jiten.Tests;

public class RetentionCalculatorTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static RetentionCalculator.ReviewEntry Log(long cardId, DateTime when, bool isAgain) => new(cardId, when, isAgain);

    [Fact]
    public void FirstReviewOfCard_IsExcluded()
    {
        // A single review has no prior review → it is a new-card first exposure, not a retention test.
        var logs = new[] { Log(1, Now.AddDays(-5), isAgain: false) };

        var result = RetentionCalculator.Compute(logs, offsetHours: 0, nowUtc: Now);

        result.AllTime.Overall.Total.Should().Be(0);
        result.AllTime.Overall.Retention.Should().BeNull();
    }

    [Fact]
    public void SameDayLearningSteps_AreExcluded()
    {
        // Two reviews within the same day (< 1 day elapsed) → learning steps, not counted.
        var logs = new[]
        {
            Log(1, Now.AddDays(-2), isAgain: false),
            Log(1, Now.AddDays(-2).AddMinutes(10), isAgain: false),
        };

        var result = RetentionCalculator.Compute(logs, offsetHours: 0, nowUtc: Now);

        result.AllTime.Overall.Total.Should().Be(0);
    }

    [Fact]
    public void PassFail_CountedByNonAgainRating()
    {
        // card 1: first exposure (excluded), then a pass and a fail on later days.
        var logs = new[]
        {
            Log(1, Now.AddDays(-10), isAgain: false),
            Log(1, Now.AddDays(-8), isAgain: false), // pass, elapsed 2d → young
            Log(1, Now.AddDays(-5), isAgain: true),  // fail, elapsed 3d → young
        };

        var result = RetentionCalculator.Compute(logs, offsetHours: 0, nowUtc: Now);

        result.AllTime.Overall.Total.Should().Be(2);
        result.AllTime.Overall.Passed.Should().Be(1);
        result.AllTime.Overall.Retention.Should().BeApproximately(0.5, 1e-9);
        result.AllTime.Young.Total.Should().Be(2);
        result.AllTime.Mature.Total.Should().Be(0);
    }

    [Fact]
    public void YoungVsMature_SplitByElapsedInterval()
    {
        var logs = new[]
        {
            Log(1, Now.AddDays(-60), isAgain: false), // first exposure
            Log(1, Now.AddDays(-50), isAgain: false), // elapsed 10d → young, pass
            Log(2, Now.AddDays(-60), isAgain: false), // first exposure
            Log(2, Now.AddDays(-20), isAgain: false), // elapsed 40d → mature, pass
            Log(2, Now.AddDays(-2), isAgain: true),   // elapsed 18d → young, fail
        };

        var result = RetentionCalculator.Compute(logs, offsetHours: 0, nowUtc: Now);

        result.AllTime.Young.Total.Should().Be(2);
        result.AllTime.Young.Passed.Should().Be(1);
        result.AllTime.Mature.Total.Should().Be(1);
        result.AllTime.Mature.Passed.Should().Be(1);
        result.AllTime.Mature.Retention.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Windows_RespectTrailingCutoffs()
    {
        var logs = new[]
        {
            Log(1, Now.AddDays(-200), isAgain: false), // first exposure
            Log(1, Now.AddDays(-150), isAgain: false), // mature pass, all-time only
            Log(1, Now.AddDays(-60), isAgain: true),   // mature fail, in last90 + all
            Log(1, Now.AddDays(-10), isAgain: false),  // mature pass, in all windows
        };

        var result = RetentionCalculator.Compute(logs, offsetHours: 0, nowUtc: Now);

        result.AllTime.Overall.Total.Should().Be(3);
        result.Last90.Overall.Total.Should().Be(2);
        result.Last30.Overall.Total.Should().Be(1);
        result.Last30.Overall.Passed.Should().Be(1);
    }

    [Fact]
    public void FirstReviewPerCardPerDay_Deduplicated()
    {
        // Card reviewed twice on the same later day (e.g. Again then re-shown and passed):
        // only the first qualifying review of that day counts.
        var logs = new[]
        {
            Log(1, Now.AddDays(-10), isAgain: false),               // first exposure
            Log(1, Now.AddDays(-3).AddHours(1), isAgain: true),     // first of the day → counts (fail)
            Log(1, Now.AddDays(-3).AddHours(2), isAgain: false),    // same day, < 1d elapsed → excluded anyway
        };

        var result = RetentionCalculator.Compute(logs, offsetHours: 0, nowUtc: Now);

        result.AllTime.Overall.Total.Should().Be(1);
        result.AllTime.Overall.Passed.Should().Be(0);
    }

    [Fact]
    public void Monthly_SeriesGroupsByLocalMonth()
    {
        var logs = new[]
        {
            Log(1, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), false), // first exposure
            Log(1, new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc), false), // Jan pass
            Log(1, new DateTime(2026, 2, 15, 12, 0, 0, DateTimeKind.Utc), true),  // Feb fail
        };

        var result = RetentionCalculator.Compute(logs, offsetHours: 0, nowUtc: Now);

        result.Monthly.Should().HaveCount(2);
        result.Monthly[0].Period.Should().Be("2026-01");
        result.Monthly[0].Overall.Passed.Should().Be(1);
        result.Monthly[1].Period.Should().Be("2026-02");
        result.Monthly[1].Overall.Passed.Should().Be(0);
    }

    [Fact]
    public void Weekly_SeriesGroupsByMondayOfLocalWeek()
    {
        var logs = new[]
        {
            // 2026-04-06 is a Monday. First exposure, then a pass same week and a fail the next week.
            Log(1, new DateTime(2026, 4, 6, 9, 0, 0, DateTimeKind.Utc), false),  // first exposure
            Log(1, new DateTime(2026, 4, 8, 9, 0, 0, DateTimeKind.Utc), false),  // Wed same week → pass
            Log(1, new DateTime(2026, 4, 14, 9, 0, 0, DateTimeKind.Utc), true),  // following Tue → fail
        };

        var result = RetentionCalculator.Compute(logs, offsetHours: 0, nowUtc: Now);

        result.Weekly.Should().HaveCount(2);
        result.Weekly[0].Period.Should().Be("2026-04-06");
        result.Weekly[0].Overall.Passed.Should().Be(1);
        result.Weekly[1].Period.Should().Be("2026-04-13");
        result.Weekly[1].Overall.Passed.Should().Be(0);
    }
}
