using FluentAssertions;
using Jiten.Api.Helpers;
using Jiten.Api.Jobs;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.User;

namespace Jiten.Tests;

public class FsrsAutoOptimiseTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly FsrsAutoOptimiseOptions Options = new();

    private static bool IsDue(int total, int baseline = 0, DateTime? attemptedAt = null,
                              FsrsParametersSource source = FsrsParametersSource.Default, bool autoOptimise = true)
        => FsrsAutoOptimiseJob.IsDue(Options, source, autoOptimise, attemptedAt, baseline, total, Now);

    [Fact]
    public void FirstFit_NeedsTheMinimumHistory()
    {
        IsDue(999).Should().BeFalse();
        IsDue(1000).Should().BeTrue();
    }

    [Fact]
    public void EditedParametersAndOptOut_AreNeverDue()
    {
        IsDue(50_000, source: FsrsParametersSource.Edited).Should().BeFalse();
        IsDue(50_000, autoOptimise: false).Should().BeFalse();
        IsDue(50_000, source: FsrsParametersSource.Legacy).Should().BeTrue();
    }

    [Fact]
    public void Attempts_AreSpacedBySevenDays()
    {
        IsDue(10_000, baseline: 1000, attemptedAt: Now.AddDays(-6.9)).Should().BeFalse();
        IsDue(10_000, baseline: 1000, attemptedAt: Now.AddDays(-7)).Should().BeTrue();
    }

    [Theory]
    [InlineData(1200, 1699, false)]
    [InlineData(1200, 1700, true)]
    [InlineData(4000, 4999, false)]
    [InlineData(4000, 5000, true)]
    [InlineData(500_000, 504_999, false)]
    [InlineData(500_000, 505_000, true)]
    public void Growth_IsAQuarterBetweenTheFloorAndTheCap(int baseline, int total, bool due)
        => IsDue(total, baseline, Now.AddDays(-30)).Should().Be(due);

    [Fact]
    public void Refits_StillNeedTheMinimumHistory()
        => IsDue(900, baseline: 300, attemptedAt: Now.AddDays(-30)).Should().BeFalse();

    [Fact]
    public void V7EvaluateLoss_MatchesTheLossTheOptimiserReports()
    {
        var items = SyntheticItems(300);
        var result = FsrsOptimizerV7.Optimize(items);

        FsrsOptimizerV7.EvaluateLoss(items, result.Parameters).Should().BeApproximately(result.Loss, 1e-9);
    }

    [Fact]
    public void V6EvaluateLoss_ScoresAFitBelowTheDefaults()
    {
        var items = SyntheticItems(300);
        var result = FsrsOptimizer.Optimize(items);

        FsrsOptimizer.EvaluateLoss(items, result.Parameters)
                     .Should().BeLessThan(FsrsOptimizer.EvaluateLoss(items, FsrsConstants.DefaultParameters));
    }

    [Fact]
    public void Undo_ReturnsToTheNewestLiveRowBeforeTheCurrentOne()
    {
        var rows = HistoryRows(4);
        FsrsParameterProvenance.UndoTarget(rows).Should().BeSameAs(rows[2]);

        FsrsParameterProvenance.MarkRestored(rows, rows[2], Now);

        FsrsParameterProvenance.Current(rows).Should().BeSameAs(rows[2]);
        rows[3].RevertedAtUtc.Should().Be(Now);
        FsrsParameterProvenance.UndoTarget(rows).Should().BeSameAs(rows[1]);
    }

    [Fact]
    public void RestoringAnUndoneRow_MakesItCurrentAgain()
    {
        var rows = HistoryRows(3);
        FsrsParameterProvenance.MarkRestored(rows, rows[0], Now);
        FsrsParameterProvenance.MarkRestored(rows, rows[2], Now.AddMinutes(1));

        FsrsParameterProvenance.Current(rows).Should().BeSameAs(rows[2]);
        rows[1].RevertedAtUtc.Should().Be(Now);
        FsrsParameterProvenance.UndoTarget(rows).Should().BeSameAs(rows[0]);
    }

    private static List<FsrsParameterHistory> HistoryRows(int count)
        => Enumerable.Range(0, count)
                     .Select(i => new FsrsParameterHistory { Id = i + 1, CreatedAtUtc = Now.AddDays(i - count) })
                     .ToList();

    /// <summary>Cards a learner keeps recalling at gaps far longer than the default parameters expect.</summary>
    private static List<FsrsTrainingItem> SyntheticItems(int cards)
    {
        var rng = new Random(7);
        var items = new List<FsrsTrainingItem>(cards);
        for (var c = 0; c < cards; c++)
        {
            var reviews = new List<FsrsTrainingReview> { new(3, 0) };
            foreach (var gap in new[] { 8.0, 25, 70, 160 })
                reviews.Add(new FsrsTrainingReview(rng.NextDouble() < 0.06 ? 1 : 3, gap + rng.Next(0, 4)));
            items.Add(new FsrsTrainingItem(reviews.ToArray(), c));
        }
        return items;
    }
}
