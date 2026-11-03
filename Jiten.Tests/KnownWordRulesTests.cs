using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;

namespace Jiten.Tests;

public class KnownWordRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(FsrsState.Mastered, null, 0, KnownState.Mastered)]
    [InlineData(FsrsState.Blacklisted, null, 0, KnownState.Blacklisted)]
    [InlineData(FsrsState.Learning, null, 0, KnownState.New)]
    [InlineData(FsrsState.Review, -1, 5, KnownState.Young)]
    [InlineData(FsrsState.Review, -1, 20, KnownState.Mature)]
    [InlineData(FsrsState.Relearning, -1, 2, KnownState.Young)]
    [InlineData(FsrsState.Suspended, -1, 30, KnownState.Mature)]
    [InlineData(FsrsState.Suspended, null, 30, KnownState.New)]
    [InlineData(FsrsState.New, -1, 0, KnownState.New)]
    [InlineData(FsrsState.New, -30, 0, KnownState.New)]
    public void CardTier(FsrsState state, int? reviewedDaysAgo, int dueInDays, KnownState expected)
    {
        DateTime? lastReview = reviewedDaysAgo is { } d ? Now.AddDays(d) : null;

        KnownWordRules.CardTier(state, Now.AddDays(dueInDays), lastReview).Should().Be(expected);
    }

    [Fact]
    public void WordTier_LearnedFormOutranksBlacklisted() =>
        KnownWordRules.WordTier([KnownState.Blacklisted, KnownState.Young]).Should().Be(KnownState.Young);

    [Fact]
    public void WordTier_BlacklistedOutranksUnknownForms() =>
        KnownWordRules.WordTier([KnownState.New, KnownState.Blacklisted]).Should().Be(KnownState.Blacklisted);

    [Fact]
    public void WordTier_PicksHighestLearnedTier() =>
        KnownWordRules.WordTier([KnownState.Young, KnownState.Mastered, KnownState.Mature]).Should().Be(KnownState.Mastered);

    [Fact]
    public void WordTier_NoKnownFormsIsNew() =>
        KnownWordRules.WordTier([KnownState.New]).Should().Be(KnownState.New);

    [Fact]
    public void FormRank_MasteredOutranksBlacklistedOutranksMature() =>
        new[] { KnownState.Young, KnownState.Mature, KnownState.Blacklisted, KnownState.Mastered }
            .Select(KnownWordRules.FormRank).Should().BeInAscendingOrder();
}
