using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Xunit;

namespace Jiten.Parser.Tests;

public class StudySettingsCardImageTests
{
    [Fact]
    public void Defaults_SerializeToExpectedWireValues()
    {
        var json = JsonSerializer.Serialize(new StudySettingsDto());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("cardImageLayout").GetString().Should().Be("beside");
        root.GetProperty("cardImagePosition").GetString().Should().Be("Back");
        root.GetProperty("blurCardImage").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void NonDefaults_SerializeToExactStrings()
    {
        var dto = new StudySettingsDto
        {
            CardImageLayout = CardImageLayout.Below,
            CardImagePosition = CardImagePosition.Front,
            BlurCardImage = false
        };

        var json = JsonSerializer.Serialize(dto);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("cardImageLayout").GetString().Should().Be("below");
        root.GetProperty("cardImagePosition").GetString().Should().Be("Front");
        root.GetProperty("blurCardImage").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Deserialize_ReadsWireStringsBothDirections()
    {
        var json = """{"cardImageLayout":"below","cardImagePosition":"Front","blurCardImage":false}""";
        var dto = JsonSerializer.Deserialize<StudySettingsDto>(json)!;

        dto.CardImageLayout.Should().Be(CardImageLayout.Below);
        dto.CardImagePosition.Should().Be(CardImagePosition.Front);
        dto.BlurCardImage.Should().BeFalse();
    }

    [Fact]
    public void Deserialize_MissingFields_FallBackToDefaults()
    {
        // A settings blob stored before these fields existed must deserialize to beside/Back/true.
        var dto = JsonSerializer.Deserialize<StudySettingsDto>("{}")!;

        dto.CardImageLayout.Should().Be(CardImageLayout.Beside);
        dto.CardImagePosition.Should().Be(CardImagePosition.Back);
        dto.BlurCardImage.Should().BeTrue();
    }

    [Fact]
    public void RoundTrip_IsStable()
    {
        var original = new StudySettingsDto
        {
            CardImageLayout = CardImageLayout.Below,
            CardImagePosition = CardImagePosition.Front,
            BlurCardImage = false
        };

        var reloaded = JsonSerializer.Deserialize<StudySettingsDto>(JsonSerializer.Serialize(original))!;

        reloaded.CardImageLayout.Should().Be(original.CardImageLayout);
        reloaded.CardImagePosition.Should().Be(original.CardImagePosition);
        reloaded.BlurCardImage.Should().Be(original.BlurCardImage);
    }
}
