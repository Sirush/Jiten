using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Xunit;

namespace Jiten.Parser.Tests;

public class StudySettingsCustomAudioTests
{
    [Fact]
    public void PreMigrationBlob_GetsCurrentCustomAudioDefaults()
    {
        var json = """{"autoPlayCustomAudio":false,"autoPlayCustomAudioPosition":"Front","autoPlayCustomAudioInstead":false}""";
        var dto = StudySettingsMigrator.Apply(JsonSerializer.Deserialize<StudySettingsDto>(json)!);

        dto.AutoPlayCustomAudio.Should().BeTrue();
        dto.AutoPlayCustomAudioPosition.Should().Be(CardAudioAutoPlayPosition.Back);
        dto.CustomAudioReplacesHeadword.Should().BeTrue();
        dto.CustomAudioReplacesSentence.Should().BeTrue();
        dto.AudioDefaultsVersion.Should().Be(StudySettingsMigrator.CurrentAudioDefaultsVersion);
    }

    [Fact]
    public void MigratedBlob_KeepsTheUsersChoices()
    {
        var json = $$"""
                     {"autoPlayCustomAudio":false,"autoPlayCustomAudioPosition":"Front","customAudioReplacesHeadword":false,
                      "customAudioReplacesSentence":false,"audioDefaultsVersion":{{StudySettingsMigrator.CurrentAudioDefaultsVersion}}}
                     """;
        var dto = StudySettingsMigrator.Apply(JsonSerializer.Deserialize<StudySettingsDto>(json)!);

        dto.AutoPlayCustomAudio.Should().BeFalse();
        dto.AutoPlayCustomAudioPosition.Should().Be(CardAudioAutoPlayPosition.Front);
        dto.CustomAudioReplacesHeadword.Should().BeFalse();
        dto.CustomAudioReplacesSentence.Should().BeFalse();
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        var dto = StudySettingsMigrator.Apply(JsonSerializer.Deserialize<StudySettingsDto>("{}")!);
        dto.AutoPlayCustomAudio = false;

        StudySettingsMigrator.Apply(dto).AutoPlayCustomAudio.Should().BeFalse();
    }

    [Fact]
    public void Defaults_SerializeToExpectedWireValues()
    {
        var json = JsonSerializer.Serialize(new StudySettingsDto());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("autoPlayCustomAudio").GetBoolean().Should().BeTrue();
        root.GetProperty("autoPlayCustomAudioPosition").GetString().Should().Be("Back");
        root.GetProperty("customAudioReplacesHeadword").GetBoolean().Should().BeTrue();
        root.GetProperty("customAudioReplacesSentence").GetBoolean().Should().BeTrue();
        root.GetProperty("audioDefaultsVersion").GetInt32().Should().Be(0);
    }
}
