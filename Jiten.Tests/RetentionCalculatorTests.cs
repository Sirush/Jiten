using FluentAssertions;
using Jiten.Core.Data.FSRS;
using Xunit;

namespace Jiten.Tests;

public class RetentionCalculatorTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static RetentionCalculator.ReviewEntry Log(long cardId, DateTime when, bool isAgain) => new(cardId, when, isAgain);

    private static RetentionCalculator.ReviewEntry Rated(long cardId, DateTime when, int rating, int? durationMs = null) =>
        new(cardId, when, rating == 1, rating, durationMs);

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

    [Fact]
    public void AnswerButtons_FirstReviewAndSameDay_AreLearning()
    {
        var logs = new[]
        {
            Rated(1, Now.AddDays(-2), rating: 3),                  // first-ever → learning, good
            Rated(1, Now.AddDays(-2).AddMinutes(10), rating: 1),  // <1d gap → learning, again
        };

        var result = RetentionCalculator.Compute(logs, offsetHours: 0, nowUtc: Now);

        result.AnswerButtons.All.Learning.Should().Equal(1, 0, 1, 0); // again=1, good=1
        result.AnswerButtons.All.Young.Should().Equal(0, 0, 0, 0);
        result.AnswerButtons.All.Mature.Should().Equal(0, 0, 0, 0);
    }

    [Fact]
    public void AnswerButtons_YoungVsMature_SplitAt21Days()
    {
        var logs = new[]
        {
            Rated(1, Now.AddDays(-60), rating: 3),  // first → learning
            Rated(1, Now.AddDays(-50), rating: 3),  // gap 10d → young, good
            Rated(1, Now.AddDays(-20), rating: 4),  // gap 30d → mature, easy
        };

        var result = RetentionCalculator.Compute(logs, offsetHours: 0, nowUtc: Now);

        result.AnswerButtons.All.Learning.Should().Equal(0, 0, 1, 0);
        result.AnswerButtons.All.Young.Should().Equal(0, 0, 1, 0);
        result.AnswerButtons.All.Mature.Should().Equal(0, 0, 0, 1);
    }

    [Fact]
    public void AnswerButtons_TotalCount_EqualsRatedLogCount()
    {
        var logs = new[]
        {
            Rated(1, Now.AddDays(-30), rating: 3),
            Rated(1, Now.AddDays(-25), rating: 2),
            Rated(2, Now.AddDays(-10), rating: 1),
            Rated(2, Now.AddDays(-1), rating: 4),
        };

        var result = RetentionCalculator.Compute(logs, offsetHours: 0, nowUtc: Now);

        var total = result.AnswerButtons.All.Learning.Sum()
                    + result.AnswerButtons.All.Young.Sum()
                    + result.AnswerButtons.All.Mature.Sum();
        total.Should().Be(4);
    }

    [Fact]
    public void Hourly_BucketsByLocalHour_WithOffset()
    {
        // 23:30 UTC + 1h offset → local hour 0.
        var logs = new[]
        {
            Rated(1, new DateTime(2026, 5, 1, 23, 30, 0, DateTimeKind.Utc), rating: 3),
            Rated(2, new DateTime(2026, 5, 1, 23, 45, 0, DateTimeKind.Utc), rating: 1),
        };

        var result = RetentionCalculator.Compute(logs, offsetHours: 1, nowUtc: Now);

        result.Hourly.All.Should().HaveCount(24);
        result.Hourly.All[0].Count.Should().Be(2);
        result.Hourly.All[0].PassRate.Should().BeApproximately(0.5, 1e-9);
        result.Hourly.All[12].Count.Should().Be(0);
        result.Hourly.All[12].PassRate.Should().BeNull();
    }

    [Fact]
    public void ReviewTime_BucketsAndCapsOutliers()
    {
        var logs = new[]
        {
            Rated(1, Now.AddDays(-3), rating: 3, durationMs: 500),       // 0.5s → bucket 0
            Rated(1, Now.AddDays(-2), rating: 3, durationMs: 4000),      // 4s → bucket 3 (3–5)
            Rated(2, Now.AddDays(-2), rating: 3, durationMs: 600000),    // 600s → 60+ bucket, capped to 120s for average
            Rated(3, Now.AddDays(-2), rating: 3, durationMs: null),      // no duration → ignored
        };

        var result = RetentionCalculator.Compute(logs, offsetHours: 0, nowUtc: Now);

        result.ReviewTime.All.Count.Should().Be(3);
        result.ReviewTime.All.Buckets[0].Should().Be(1);
        result.ReviewTime.All.Buckets[3].Should().Be(1);
        result.ReviewTime.All.Buckets[^1].Should().Be(1); // 60s+
        // Average of capped values: (0.5 + 4 + 120) / 3 = 41.5
        result.ReviewTime.All.AverageSeconds.Should().BeApproximately(41.5, 1e-9);
        // Total hours uses raw (uncapped): (0.5 + 4 + 600) / 3600
        result.ReviewTime.All.TotalHours.Should().BeApproximately(604.5 / 3600.0, 1e-9);
    }

    [Fact]
    public void Today_CountsLocalDayReviewsAndNewCards()
    {
        var now = new DateTime(2026, 6, 1, 2, 0, 0, DateTimeKind.Utc); // local 12:00 with +10 offset
        var offset = 10.0;
        var localTodayUtcMorning = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc); // local 10:00 same day

        var logs = new[]
        {
            // card 1: first-ever review today → new card today, plus a same-day extra review
            Rated(1, localTodayUtcMorning, rating: 3, durationMs: 30000),
            Rated(1, localTodayUtcMorning.AddHours(1), rating: 1, durationMs: 90000),
            // card 2: first review yesterday (local) → not new today; reviewed again today
            Rated(2, new DateTime(2026, 5, 30, 23, 0, 0, DateTimeKind.Utc), rating: 3), // local 2026-05-31 09:00
            Rated(2, localTodayUtcMorning.AddHours(2), rating: 3, durationMs: 60000),
        };

        var result = RetentionCalculator.Compute(logs, offset, now);

        result.Today.Reviews.Should().Be(3);           // 2 from card1 today + 1 from card2 today
        result.Today.NewCards.Should().Be(1);          // only card1 first-reviewed today
        result.Today.PassRate.Should().BeApproximately(2.0 / 3.0, 1e-9); // one Again of three
        result.Today.Minutes.Should().Be(3);           // (30000+90000+60000)ms = 180000ms = 3min
    }

    [Fact]
    public void NewBlocks_EmptyLogs_ProduceZeroedShape()
    {
        var result = RetentionCalculator.Compute(Array.Empty<RetentionCalculator.ReviewEntry>(), 0, Now);

        result.AnswerButtons.All.Learning.Should().Equal(0, 0, 0, 0);
        result.AnswerButtons.Last30.Learning.Should().Equal(0, 0, 0, 0);
        result.AnswerButtons.Last90.Mature.Should().Equal(0, 0, 0, 0);
        result.Hourly.All.Should().HaveCount(24);
        result.Hourly.All.Should().OnlyContain(h => h.Count == 0 && h.PassRate == null);
        result.Hourly.Last30.Should().HaveCount(24);
        result.Hourly.Last90.Should().HaveCount(24);
        result.ReviewTime.All.Count.Should().Be(0);
        result.ReviewTime.All.AverageSeconds.Should().BeNull();
        result.ReviewTime.All.TotalHours.Should().Be(0);
        result.ReviewTime.Last30.Count.Should().Be(0);
        result.ReviewTime.Last90.AverageSeconds.Should().BeNull();
        result.Today.Reviews.Should().Be(0);
        result.Today.PassRate.Should().BeNull();
        result.Today.NewCards.Should().Be(0);
    }

    [Fact]
    public void AnswerButtons_WindowBoundaries_SplitByReviewAge()
    {
        // One card, four rated reviews at descending ages. The first is a learning exposure;
        // the later ones are young/mature by elapsed gap. Windows filter by review timestamp.
        var logs = new[]
        {
            Rated(1, Now.AddDays(-120), rating: 3), // first → learning; all only
            Rated(1, Now.AddDays(-95), rating: 3),  // gap 25d → mature; all only (95d old)
            Rated(1, Now.AddDays(-31), rating: 2),  // gap 64d → mature; last90 + all (31d old)
            Rated(1, Now.AddDays(-5), rating: 1),   // gap 26d → mature; all windows (5d old)
        };

        var result = RetentionCalculator.Compute(logs, offsetHours: 0, nowUtc: Now);

        // All-time: 1 learning + 3 mature.
        result.AnswerButtons.All.Learning.Sum().Should().Be(1);
        result.AnswerButtons.All.Mature.Sum().Should().Be(3);

        // Last90 (>=90d cutoff): the -120 and -95 reviews drop out → 2 mature only.
        result.AnswerButtons.Last90.Learning.Sum().Should().Be(0);
        result.AnswerButtons.Last90.Mature.Sum().Should().Be(2);

        // Last30 (>=30d cutoff): only the -5 review remains → 1 mature.
        result.AnswerButtons.Last30.Mature.Sum().Should().Be(1);
        result.AnswerButtons.Last30.Learning.Sum().Should().Be(0);
    }

    [Fact]
    public void HourlyAndReviewTime_WindowBoundaries_FilterByReviewAge()
    {
        // A review 31 days old appears in last90 + all but not last30; a 5-day-old one is in all windows.
        var logs = new[]
        {
            Rated(1, Now.AddDays(-31).Date.AddHours(8), rating: 3, durationMs: 2000),  // last90 + all
            Rated(2, Now.AddDays(-5).Date.AddHours(8), rating: 1, durationMs: 9000),   // all windows
        };

        var result = RetentionCalculator.Compute(logs, offsetHours: 0, nowUtc: Now);

        // Hourly: hour 8 has both for all/last90, only the recent one for last30.
        result.Hourly.All[8].Count.Should().Be(2);
        result.Hourly.Last90[8].Count.Should().Be(2);
        result.Hourly.Last30[8].Count.Should().Be(1);

        // Review time: count of durations mirrors the hourly membership.
        result.ReviewTime.All.Count.Should().Be(2);
        result.ReviewTime.Last90.Count.Should().Be(2);
        result.ReviewTime.Last30.Count.Should().Be(1);
    }
}
