using FluentAssertions;
using Jiten.Core.Services.SmartDeck;

namespace Jiten.Tests;

public class SmartDeckSettingsTests
{
    [Fact]
    public void TargetPercentage_DefaultsTo95_AndClamps()
    {
        SmartDeckSettings.Parse(null).TargetPercentage.Should().Be(95);
        SmartDeckSettings.Parse("{\"enabled\":true}").TargetPercentage.Should().Be(95, "documents saved before the field existed keep the default");
        new SmartDeckSettings { TargetPercentage = 10 }.Normalized().TargetPercentage.Should().Be(SmartDeckConstants.MinTargetPercentage);
        new SmartDeckSettings { TargetPercentage = 140 }.Normalized().TargetPercentage.Should().Be(100);
    }

    [Fact]
    public void Parse_EmptyOrInvalid_ReturnsDefaults()
    {
        foreach (var json in new[] { null, "", "{}", "not json", "[1,2]" })
        {
            var settings = SmartDeckSettings.Parse(json);
            settings.Enabled.Should().BeFalse();
            settings.LookaheadUnits.Should().Be(1);
            settings.RecencyHalfLifeDays.Should().Be(14);
            settings.PinnedDeckIds.Should().BeEmpty();
        }
    }

    [Fact]
    public void Normalized_ClampsAndResolvesListConflicts()
    {
        var settings = new SmartDeckSettings
        {
            LookaheadUnits = 9,
            RecencyHalfLifeDays = 11,
            PinnedDeckIds = [1, 1, 2, 3, 4, 5],
            IncludedDeckIds = [2, 6, 0, -1],
            ExcludedDeckIds = [2, 2, 7],
        }.Normalized();

        settings.LookaheadUnits.Should().Be(3);
        settings.RecencyHalfLifeDays.Should().Be(14);
        settings.PinnedDeckIds.Should().Equal(1, 3, 4);
        settings.IncludedDeckIds.Should().Equal(6);
        settings.ExcludedDeckIds.Should().Equal(2, 7);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(365)]
    [InlineData(SmartDeckConstants.NeverFadesHalfLife)]
    public void Normalized_KeepsAllowedHalfLives(int days)
    {
        new SmartDeckSettings { RecencyHalfLifeDays = days }.Normalized().RecencyHalfLifeDays.Should().Be(days);
    }

    [Fact]
    public void Serialize_RoundTripsCamelCase()
    {
        var json = new SmartDeckSettings { Enabled = true, PinnedDeckIds = [42], SequenceOverrides = { [7] = false, [0] = true } }.Serialize();
        json.Should().Contain("\"enabled\":true").And.Contain("\"pinnedDeckIds\":[42]").And.Contain("\"sequenceOverrides\":{\"7\":false}");
        SmartDeckSettings.Parse(json).Serialize().Should().Be(json);
        SmartDeckSettings.Parse(json).SequenceOverrides.Should().Equal(new Dictionary<int, bool> { [7] = false });
    }
}
