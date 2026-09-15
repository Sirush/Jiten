using FluentAssertions;
using Jiten.Core.Data;
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
    public void RestTargetPercentage_DefaultsTo90_AndAllowsZero()
    {
        SmartDeckSettings.Parse(null).RestTargetPercentage.Should().Be(90);
        SmartDeckSettings.Parse("{\"enabled\":true,\"targetPercentage\":80}").RestTargetPercentage.Should().Be(90, "documents saved before the field existed keep the default");
        new SmartDeckSettings { RestTargetPercentage = -5 }.Normalized().RestTargetPercentage.Should().Be(0);
        new SmartDeckSettings { RestTargetPercentage = 0 }.Normalized().RestTargetPercentage.Should().Be(0);
        new SmartDeckSettings { RestTargetPercentage = 140 }.Normalized().RestTargetPercentage.Should().Be(100);
    }

    [Fact]
    public void LookaheadByMediaType_ClampsDropsNonSequentialTypes_AndRoundTrips()
    {
        var settings = new SmartDeckSettings
        {
            LookaheadUnits = 2,
            LookaheadByMediaType = new() { [MediaType.Anime] = 9, [MediaType.Manga] = 1, [MediaType.VisualNovel] = 3 },
        }.Normalized();

        settings.LookaheadByMediaType.Should().Equal(new Dictionary<MediaType, int> { [MediaType.Anime] = 3, [MediaType.Manga] = 1 });
        settings.LookaheadFor(MediaType.Anime).Should().Be(3);
        settings.LookaheadFor(MediaType.Manga).Should().Be(1);
        settings.LookaheadFor(MediaType.Drama).Should().Be(2, "a type without an override follows the general pick-ahead");
        settings.LookaheadFor(MediaType.VisualNovel).Should().Be(2);

        var parsed = SmartDeckSettings.Parse(settings.Serialize());
        parsed.LookaheadByMediaType.Should().Equal(settings.LookaheadByMediaType);
        SmartDeckSettings.Parse("{\"lookaheadByMediaType\":{\"1\":3,\"Manga\":2}}").LookaheadByMediaType
                         .Should().Equal(new Dictionary<MediaType, int> { [MediaType.Anime] = 3, [MediaType.Manga] = 2 }, "numeric and named keys both parse");
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
