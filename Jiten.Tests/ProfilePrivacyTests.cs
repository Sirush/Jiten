using FluentAssertions;
using Jiten.Core.Data;
using Xunit;

namespace Jiten.Parser.Tests;

public class ProfilePrivacyTests
{
    [Fact]
    public void EmptyJsonFallsBackToTheLegacyFlags()
    {
        var privacy = ProfilePrivacy.Parse("{}", legacyIsPublic: true, legacyMediaListPublic: false);

        privacy.Profile.Should().Be(ProfileVisibility.Public);
        privacy.CanSee(ProfileViewer.Everyone, ProfileSection.Library).Should().BeFalse();
        privacy.CanSee(ProfileViewer.Everyone, ProfileSection.Stats).Should().BeFalse();
        privacy.CanSee(ProfileViewer.Everyone, ProfileSection.Scores).Should().BeFalse();
        privacy.CanSee(ProfileViewer.Everyone, ProfileSection.StudyActivity).Should().BeTrue();
        privacy.HideAdultTitles.Should().BeTrue();
    }

    [Fact]
    public void PublicLegacyMediaListOpensStatsAndScores()
    {
        var privacy = ProfilePrivacy.Parse("{}", legacyIsPublic: true, legacyMediaListPublic: true);

        privacy.CanSee(ProfileViewer.Everyone, ProfileSection.Library).Should().BeTrue();
        privacy.CanSee(ProfileViewer.Everyone, ProfileSection.Stats).Should().BeTrue();
        privacy.CanSee(ProfileViewer.Everyone, ProfileSection.Scores).Should().BeTrue();
    }

    [Fact]
    public void SectionIsNeverMoreOpenThanTheProfile()
    {
        var privacy = ProfilePrivacy.Parse("""{"profile":"friends","sections":{"library":"everyone"}}""", false, false);

        privacy.EffectiveLevel(ProfileSection.Library).Should().Be(AudienceLevel.Friends);
        privacy.CanSee(ProfileViewer.Everyone, ProfileSection.Library).Should().BeFalse();
        privacy.CanSee(ProfileViewer.Friend, ProfileSection.Library).Should().BeTrue();
        privacy.IsVisibleTo(ProfileViewer.Everyone).Should().BeFalse();
    }

    [Fact]
    public void OwnerSeesEverything()
    {
        var privacy = ProfilePrivacy.Parse("""{"profile":"private","sections":{"stats":"onlyMe"},"hiddenFields":["links"]}""", true, true);

        privacy.IsVisibleTo(ProfileViewer.Owner).Should().BeTrue();
        privacy.CanSee(ProfileViewer.Owner, ProfileSection.Stats).Should().BeTrue();
        privacy.ShowsField(ProfileViewer.Owner, ProfileField.Links).Should().BeTrue();
        privacy.ShowsField(ProfileViewer.Everyone, ProfileField.Links).Should().BeFalse();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"profile":"3","sections":{"library":"nobody","bogus":"everyone"},"hiddenFields":[1,"nope"]}""")]
    [InlineData("""[]""")]
    public void MalformedInputKeepsDefaults(string json)
    {
        var privacy = ProfilePrivacy.Parse(json, legacyIsPublic: true, legacyMediaListPublic: true);

        privacy.Profile.Should().Be(ProfileVisibility.Public);
        privacy.EffectiveLevel(ProfileSection.Library).Should().Be(AudienceLevel.Everyone);
        privacy.HiddenFields.Should().BeEmpty();
    }

    [Fact]
    public void SerialiseRoundTrips()
    {
        var original = ProfilePrivacy.Parse("""{"profile":"public","sections":{"scores":"onlyMe"},"hiddenFields":["links"],"hideAdultTitles":false}""",
                                            false, false);

        var copy = ProfilePrivacy.Parse(original.Serialize(), false, false);

        copy.Profile.Should().Be(ProfileVisibility.Public);
        copy.Sections[ProfileSection.Scores].Should().Be(AudienceLevel.OnlyMe);
        copy.HiddenFields.Should().BeEquivalentTo([ProfileField.Links]);
        copy.HideAdultTitles.Should().BeFalse();
    }
}
