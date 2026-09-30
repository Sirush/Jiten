using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core.Data.FSRS;

namespace Jiten.Tests;

public class WordReplacementCardPickTests
{
    private static FsrsCard Card(DateTime? lastReview) => new() { LastReview = lastReview };

    [Fact]
    public void MoreReviewsWins()
    {
        var older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = older.AddDays(30);

        WordReplacementService.KeepsOldCard(Card(older), 5, Card(newer), 2).Should().BeTrue();
        WordReplacementService.KeepsOldCard(Card(newer), 2, Card(older), 5).Should().BeFalse();
    }

    [Fact]
    public void EqualReviews_MoreRecentReviewWins()
    {
        var older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = older.AddDays(1);

        WordReplacementService.KeepsOldCard(Card(newer), 3, Card(older), 3).Should().BeTrue();
        WordReplacementService.KeepsOldCard(Card(older), 3, Card(newer), 3).Should().BeFalse();
        WordReplacementService.KeepsOldCard(Card(newer), 0, Card(null), 0).Should().BeTrue();
    }

    [Fact]
    public void FullTie_KeepsNewCard()
    {
        var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        WordReplacementService.KeepsOldCard(Card(at), 3, Card(at), 3).Should().BeFalse();
        WordReplacementService.KeepsOldCard(Card(null), 0, Card(null), 0).Should().BeFalse();
    }
}
