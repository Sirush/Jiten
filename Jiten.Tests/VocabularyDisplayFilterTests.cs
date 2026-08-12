using FluentAssertions;
using Jiten.Api.Helpers;
using Jiten.Core.Data;
using Xunit;

namespace Jiten.Tests;

public class VocabularyDisplayFilterTests
{
    private static VocabularyDisplayFilter Filter(string? display = "all", string? suspended = null, string? redundant = null)
        => VocabularyDisplayFilter.Parse(display, suspended, redundant);

    [Fact]
    public void UnknownDoesNotMatchRedundantForm()
    {
        // A kana form covered by an unreviewed sibling carries [New, Redundant]; it is not a word the user does not know.
        var filter = Filter("unknown");

        filter.Matches([KnownState.New, KnownState.Redundant]).Should().BeFalse();
        filter.Matches([KnownState.New]).Should().BeTrue();
    }

    [Fact]
    public void RedundantWithoutInheritedTierCountsAsLearning()
    {
        Filter("learning").Matches([KnownState.New, KnownState.Redundant]).Should().BeTrue();
    }

    [Fact]
    public void TiersAreOred()
    {
        var filter = Filter("mastered,blacklisted");

        filter.Matches([KnownState.Mastered]).Should().BeTrue();
        filter.Matches([KnownState.Blacklisted]).Should().BeTrue();
        filter.Matches([KnownState.Young]).Should().BeFalse();
    }

    [Fact]
    public void AllTiersMatchEverythingWhenModifiersAreShown()
    {
        var filter = Filter("all");

        filter.IsActive.Should().BeFalse();
        filter.Matches([KnownState.Young, KnownState.Suspended]).Should().BeTrue();
        filter.Matches([KnownState.Mature, KnownState.Redundant]).Should().BeTrue();
    }

    [Fact]
    public void SuspendedModeGatesTheResult()
    {
        Filter(suspended: "hide").Matches([KnownState.Young, KnownState.Suspended]).Should().BeFalse();
        Filter(suspended: "hide").Matches([KnownState.Young]).Should().BeTrue();
        Filter(suspended: "only").Matches([KnownState.Young]).Should().BeFalse();
        Filter(suspended: "only").Matches([KnownState.Young, KnownState.Suspended]).Should().BeTrue();
    }

    [Fact]
    public void ModifierGatesApplyOnTopOfTiers()
    {
        var filter = Filter("mature", suspended: "only");

        filter.Matches([KnownState.Mature, KnownState.Suspended]).Should().BeTrue();
        filter.Matches([KnownState.Young, KnownState.Suspended]).Should().BeFalse();
        filter.Matches([KnownState.Mature]).Should().BeFalse();
    }

    [Fact]
    public void LegacyKnownCoversEveryTierButUnknown()
    {
        var filter = Filter("known");

        filter.Matches([KnownState.New]).Should().BeFalse();
        filter.Matches([KnownState.Due]).Should().BeTrue();
        filter.Matches([KnownState.Young]).Should().BeTrue();
        filter.Matches([KnownState.Blacklisted]).Should().BeTrue();
    }

    [Fact]
    public void UnreviewedCardIsLearningNotUnknown()
    {
        // GetKnownStatesFromCard returns [Due] alone for a card that has never been reviewed.
        VocabularyDisplayFilter.ResolveTier([KnownState.Due]).Should().Be(VocabularyTier.Learning);
        VocabularyDisplayFilter.ResolveTier([KnownState.Suspended]).Should().Be(VocabularyTier.Learning);
        VocabularyDisplayFilter.ResolveTier([KnownState.New]).Should().Be(VocabularyTier.Unknown);
    }

    [Fact]
    public void UnknownTokensAreIgnoredAndAllResetsTiers()
    {
        Filter("young,bogus").Tiers.Should().ContainSingle().Which.Should().Be(VocabularyTier.Young);
        Filter("young,all").Tiers.Should().BeEmpty();
    }
}
