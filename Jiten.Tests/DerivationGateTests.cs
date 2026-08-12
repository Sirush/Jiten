using FluentAssertions;
using Jiten.Core.Data.JMDict;
using Xunit;

namespace Jiten.Tests;

public class DerivationGateTests
{
    [Fact]
    public void ExcludeVerdict_DropsThePair_EvenInAFormerlyUnguardedCategory()
    {
        var (dropped, _, _) = DerivationBuilder.ResolveGate(DerivationVerdict.Exclude,
                                                            DerivationCategory.SaNominal, over: null);

        dropped.Should().BeTrue();
    }

    [Fact]
    public void OneWayVerdict_ShipsBaseToDerivedOnly_EvenInAFormerlyUnguardedCategory()
    {
        var (dropped, direction, category) = DerivationBuilder.ResolveGate(DerivationVerdict.OneWayOnly,
                                                                           DerivationCategory.KuAdverb, over: null);

        dropped.Should().BeFalse();
        direction.Should().Be(DerivationDirection.BaseToDerivedOnly);
        category.Should().Be(DerivationCategory.KuAdverb);
    }

    [Fact]
    public void BidirectionalVerdict_ConductsBothWays()
    {
        var (dropped, direction, _) = DerivationBuilder.ResolveGate(DerivationVerdict.Bidirectional,
                                                                     DerivationCategory.SaNominal, over: null);

        dropped.Should().BeFalse();
        direction.Should().Be(DerivationDirection.Bidirectional);
    }

    [Fact]
    public void Override_RescuesAPairTheAutomaticVerdictExcludes()
    {
        var over = new DerivationOverride(DerivationVerdict.ForceInclude, null);

        var (dropped, direction, _) = DerivationBuilder.ResolveGate(DerivationVerdict.Exclude,
                                                                     DerivationCategory.SaNominal, over);

        dropped.Should().BeFalse();
        direction.Should().Be(DerivationDirection.Bidirectional);
    }

    [Fact]
    public void OverrideOneWayOnly_RescuesAnExcludedPairInOneDirection()
    {
        var over = new DerivationOverride(DerivationVerdict.OneWayOnly, null);

        var (dropped, direction, _) = DerivationBuilder.ResolveGate(DerivationVerdict.Exclude,
                                                                     DerivationCategory.MasuStemNoun, over);

        dropped.Should().BeFalse();
        direction.Should().Be(DerivationDirection.BaseToDerivedOnly);
    }

    [Fact]
    public void OverrideExclude_DropsAPairTheAutomaticVerdictWouldKeep()
    {
        var over = new DerivationOverride(DerivationVerdict.Exclude, null);

        var (dropped, _, _) = DerivationBuilder.ResolveGate(DerivationVerdict.Bidirectional,
                                                             DerivationCategory.SaNominal, over);

        dropped.Should().BeTrue();
    }

    [Fact]
    public void RecategorizeOverride_MovesTheCategoryAndKeepsItsDirection()
    {
        var over = new DerivationOverride(DerivationVerdict.Recategorize, DerivationCategory.Potential,
                                          DerivationDirection.BaseToDerivedOnly);

        var (dropped, direction, category) = DerivationBuilder.ResolveGate(DerivationVerdict.Exclude,
                                                                            DerivationCategory.CausativeDoublet, over);

        dropped.Should().BeFalse();
        category.Should().Be(DerivationCategory.Potential);
        direction.Should().Be(DerivationDirection.BaseToDerivedOnly);
    }

    [Fact]
    public void RecategorizeOverrideWithoutDirection_ConductsBothWays()
    {
        var over = new DerivationOverride(DerivationVerdict.Recategorize, DerivationCategory.TransitivityPair);

        var (_, direction, category) = DerivationBuilder.ResolveGate(DerivationVerdict.OneWayOnly,
                                                                      DerivationCategory.Potential, over);

        category.Should().Be(DerivationCategory.TransitivityPair);
        direction.Should().Be(DerivationDirection.Bidirectional);
    }
}
