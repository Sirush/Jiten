using FluentAssertions;
using Jiten.Api.Helpers;
using Jiten.Core.Data.FSRS;

namespace Jiten.Tests;

public class WaniKaniCardBuilderTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CleanClimb_IsTheLessonPlusOnePassPerStage()
    {
        var started = Now.AddDays(-300);
        var nextReview = Now.AddDays(20);
        var reviews = WaniKaniCardBuilder.ReconstructReviews(new WaniKaniProgress(8, started, started.AddDays(10), null, nextReview, 7, 0), Now);

        reviews.Should().HaveCount(8).And.OnlyContain(r => r.Rating == FsrsRating.Good);
        reviews[0].At.Should().Be(started);
        reviews[4].At.Should().Be(started.AddDays(10), "the Guru pass is a real date");
        reviews[^1].At.Should().Be(nextReview.AddDays(-120));
        reviews.Select(r => r.At).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void SurplusReviews_BecomeFailuresCappedByWrongAnswers()
    {
        var progress = new WaniKaniProgress(4, Now.AddDays(-30), null, null, Now.AddDays(1), Reviews: 11, Incorrect: 2);
        var reviews = WaniKaniCardBuilder.ReconstructReviews(progress, Now);

        reviews.Count(r => r.Rating == FsrsRating.Again).Should().Be(2);
        reviews.Should().HaveCount(1 + 3 + 2 * 2);
        reviews[^1].Rating.Should().Be(FsrsRating.Good);
    }

    [Fact]
    public void MissingOrFutureDates_StillYieldPastOrderedReviews()
    {
        var reviews = WaniKaniCardBuilder.ReconstructReviews(new WaniKaniProgress(9, null, null, null, null, 0, 0), Now);

        reviews.Should().HaveCount(9);
        reviews[^1].At.Should().Be(Now);
        reviews.Select(r => r.At).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();

        var future = WaniKaniCardBuilder.ReconstructReviews(new WaniKaniProgress(1, Now.AddDays(5), null, null, null, 0, 0), Now);
        future.Should().ContainSingle().Which.At.Should().Be(Now);
    }

    [Fact]
    public void BuiltCard_MatchesAReplayOfItsOwnLogs()
    {
        var scheduler = new FsrsScheduler(enableFuzzing: false);
        var progress = new WaniKaniProgress(7, Now.AddDays(-200), Now.AddDays(-180), null, Now.AddDays(12), 12, 5);

        var card = WaniKaniCardBuilder.Build("user", (1414170, 0), progress, Now, scheduler);
        var replayed = new FsrsCard("user", 1414170, 0);
        FsrsReplay.Recompute(replayed, card.ReviewLogs.ToList(), scheduler, scheduler).Should().BeTrue();

        card.State.Should().Be(FsrsState.Review);
        replayed.Stability.Should().Be(card.Stability);
        replayed.Due.Should().Be(card.Due);
        replayed.Lapses.Should().Be(card.Lapses);
    }
}
