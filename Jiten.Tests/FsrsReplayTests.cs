using FluentAssertions;
using Jiten.Core.Data.FSRS;

namespace Jiten.Tests;

public class FsrsReplayTests
{
    private static readonly DateTime Base = new(2024, 1, 1, 9, 0, 0, DateTimeKind.Utc);

    private static List<FsrsReviewLog> Logs(params (int DayOffset, FsrsRating Rating)[] entries)
        => entries.Select((e, i) => new FsrsReviewLog
                                    {
                                        ReviewLogId = i + 1,
                                        CardId = 1,
                                        Rating = e.Rating,
                                        ReviewDateTime = Base.AddDays(e.DayOffset)
                                    }).ToList();

    private static FsrsScheduler NoFuzz() => new(enableFuzzing: false);

    /// <summary>The pre-extraction loop from SrsRecomputeJob, kept verbatim as the reference behaviour.</summary>
    private static (FsrsState State, int? Step, double? Stability, double? Difficulty, DateTime Due, DateTime? LastReview, int Lapses)
        LegacyRecompute(FsrsCard card, List<FsrsReviewLog> logs, FsrsScheduler scheduler, FsrsScheduler replayScheduler,
                        bool preserveTerminal)
    {
        var overrideState = preserveTerminal && card.State is FsrsState.Mastered or FsrsState.Blacklisted or FsrsState.Suspended
            ? card.State
            : (FsrsState?)null;

        var tempCard = new FsrsCard(card.UserId, card.WordId, card.ReadingIndex);
        var lapses = 0;
        for (var i = 0; i < logs.Count; i++)
        {
            var log = logs[i];
            var isSurvivingPlacement = i == logs.Count - 1 && overrideState == null;
            var activeScheduler = isSurvivingPlacement ? scheduler : replayScheduler;

            var prevState = tempCard.State;
            var review = activeScheduler.ReviewCard(tempCard, log.Rating, log.ReviewDateTime, log.ReviewDuration);
            if (prevState == FsrsState.Review && log.Rating == FsrsRating.Again)
                lapses++;
            tempCard = review.UpdatedCard;
        }

        return (overrideState ?? tempCard.State, tempCard.Step, tempCard.Stability, tempCard.Difficulty,
                tempCard.Due, tempCard.LastReview, lapses);
    }

    [Theory]
    [InlineData(FsrsState.Review, true)]
    [InlineData(FsrsState.Mastered, true)]
    [InlineData(FsrsState.Blacklisted, true)]
    [InlineData(FsrsState.Suspended, true)]
    [InlineData(FsrsState.Mastered, false)]
    public void Recompute_MatchesTheLoopItReplaced(FsrsState startingState, bool preserveTerminal)
    {
        var logs = Logs((0, FsrsRating.Good), (1, FsrsRating.Again), (2, FsrsRating.Good),
                        (10, FsrsRating.Again), (11, FsrsRating.Easy), (40, FsrsRating.Hard));

        var card = new FsrsCard("u", 1, 0) { CardId = 1, State = startingState };
        var expected = LegacyRecompute(card, logs, NoFuzz(), NoFuzz(), preserveTerminal);

        var actual = new FsrsCard("u", 1, 0) { CardId = 1, State = startingState };
        FsrsReplay.Recompute(actual, logs, NoFuzz(), NoFuzz(), preserveTerminal).Should().BeTrue();

        actual.State.Should().Be(expected.State);
        actual.Step.Should().Be(expected.Step);
        actual.Stability.Should().Be(expected.Stability);
        actual.Difficulty.Should().Be(expected.Difficulty);
        actual.Due.Should().Be(expected.Due);
        actual.LastReview.Should().Be(expected.LastReview);
        actual.Lapses.Should().Be(expected.Lapses);
    }

    [Fact]
    public void Recompute_NoLogs_LeavesTheCardAlone()
    {
        var card = new FsrsCard("u", 1, 0) { CardId = 1, State = FsrsState.Mastered, Lapses = 3 };

        FsrsReplay.Recompute(card, [], NoFuzz(), NoFuzz()).Should().BeFalse();

        card.State.Should().Be(FsrsState.Mastered);
        card.Lapses.Should().Be(3);
    }

    [Fact]
    public void Recompute_UnorderedLogs_ReplaysChronologically()
    {
        var ordered = Logs((0, FsrsRating.Good), (3, FsrsRating.Again), (5, FsrsRating.Good));
        var shuffled = new List<FsrsReviewLog> { ordered[2], ordered[0], ordered[1] };

        var a = new FsrsCard("u", 1, 0) { CardId = 1 };
        var b = new FsrsCard("u", 1, 0) { CardId = 1 };

        FsrsReplay.Recompute(a, ordered, NoFuzz(), NoFuzz());
        FsrsReplay.Recompute(b, shuffled, NoFuzz(), NoFuzz());

        b.Due.Should().Be(a.Due);
        b.Stability.Should().Be(a.Stability);
        b.Lapses.Should().Be(a.Lapses);
    }

    [Fact]
    public void Recompute_AcceptsUnspecifiedKindTimestamps()
    {
        var logs = Logs((0, FsrsRating.Good), (2, FsrsRating.Good));
        foreach (var log in logs)
            log.ReviewDateTime = DateTime.SpecifyKind(log.ReviewDateTime, DateTimeKind.Unspecified);

        var card = new FsrsCard("u", 1, 0) { CardId = 1 };

        var act = () => FsrsReplay.Recompute(card, logs, NoFuzz(), NoFuzz());

        act.Should().NotThrow();
        card.LastReview.Should().Be(Base.AddDays(2));
    }
}
