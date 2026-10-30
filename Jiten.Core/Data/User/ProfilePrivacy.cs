using System.Text.Json;

namespace Jiten.Core.Data;

public enum ProfileVisibility
{
    Public = 0,
    Friends = 1,
    Private = 2
}

public enum AudienceLevel
{
    Everyone = 0,
    Friends = 1,
    OnlyMe = 2
}

public enum ProfileSection
{
    Library,
    Scores,
    Pinned,
    Vocabulary,
    StudyActivity,
    Stats,
    Activity,
    Social,
    FavouriteLists,

    /// <summary>Each badge also needs its source section or field to be visible.</summary>
    Milestones
}

public enum ProfileField
{
    WordsKnown,
    KanjiKnown,
    Characters,
    MemberSince,
    Links,
    CurrentlyReading,
    TitlesFinished
}

public enum ProfileViewer
{
    Everyone,
    Friend,
    Owner
}

/// <summary>Parsing never throws: unknown or missing keys fall back to defaults, so new settings need no data migration.</summary>
public sealed class ProfilePrivacy
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ProfileVisibility Profile { get; set; }
    public Dictionary<ProfileSection, AudienceLevel> Sections { get; set; } = new();
    public HashSet<ProfileField> HiddenFields { get; set; } = new();
    public bool HideAdultTitles { get; set; } = true;

    /// <summary>Legacy flags seed the defaults only while nothing has been saved.</summary>
    public static ProfilePrivacy Parse(string? json, bool legacyIsPublic, bool legacyMediaListPublic)
    {
        var defaults = Defaults(legacyIsPublic, legacyMediaListPublic);
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
            return defaults;

        try
        {
            return JsonSerializer.Deserialize<ProfilePrivacyNames>(json, ReadOptions) is { } names ? names.ApplyTo(defaults) : defaults;
        }
        catch (JsonException)
        {
            return defaults;
        }
    }

    /// <summary>Stats and scores break the library down, so they start at the library's level rather than opening what a private media list kept hidden.</summary>
    public static ProfilePrivacy Defaults(bool legacyIsPublic, bool legacyMediaListPublic)
    {
        var privacy = new ProfilePrivacy { Profile = legacyIsPublic ? ProfileVisibility.Public : ProfileVisibility.Private };
        foreach (var section in Enum.GetValues<ProfileSection>())
            privacy.Sections[section] = AudienceLevel.Everyone;
        var libraryLevel = legacyMediaListPublic ? AudienceLevel.Everyone : AudienceLevel.OnlyMe;
        privacy.Sections[ProfileSection.Library] = libraryLevel;
        privacy.Sections[ProfileSection.Stats] = libraryLevel;
        privacy.Sections[ProfileSection.Scores] = libraryLevel;
        privacy.Sections[ProfileSection.FavouriteLists] = AudienceLevel.OnlyMe;
        return privacy;
    }

    public string Serialize() => JsonSerializer.Serialize(ToNames(), JsonOptions);

    public ProfilePrivacyNames ToNames() => new(
        Name(Profile),
        Enum.GetValues<ProfileSection>().ToDictionary(Name, section => Name(Sections.GetValueOrDefault(section, AudienceLevel.Everyone))),
        HiddenFields.OrderBy(f => f).Select(Name).ToList(),
        HideAdultTitles);

    public bool IsVisibleTo(ProfileViewer viewer) => viewer switch
    {
        ProfileViewer.Owner => true,
        ProfileViewer.Friend => Profile != ProfileVisibility.Private,
        _ => Profile == ProfileVisibility.Public
    };

    /// <summary>A section is never more open than the profile itself.</summary>
    public AudienceLevel EffectiveLevel(ProfileSection section)
    {
        var sectionLevel = Sections.GetValueOrDefault(section, AudienceLevel.Everyone);
        var profileLevel = Profile switch
        {
            ProfileVisibility.Public => AudienceLevel.Everyone,
            ProfileVisibility.Friends => AudienceLevel.Friends,
            _ => AudienceLevel.OnlyMe
        };
        return (AudienceLevel)Math.Max((int)sectionLevel, (int)profileLevel);
    }

    public bool CanSee(ProfileViewer viewer, ProfileSection section) => viewer switch
    {
        ProfileViewer.Owner => true,
        ProfileViewer.Friend => EffectiveLevel(section) <= AudienceLevel.Friends,
        _ => EffectiveLevel(section) == AudienceLevel.Everyone
    };

    /// <summary>A field inside a section is shown only where that section is visible too.</summary>
    public bool ShowsField(ProfileViewer viewer, ProfileField field) =>
        viewer == ProfileViewer.Owner
        || (!HiddenFields.Contains(field) && (SectionOf(field) is not { } section || CanSee(viewer, section)));

    private static ProfileSection? SectionOf(ProfileField field) => field switch
    {
        ProfileField.WordsKnown or ProfileField.KanjiKnown => ProfileSection.Vocabulary,
        ProfileField.CurrentlyReading => ProfileSection.Library,
        _ => null
    };

    public static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    /// <summary>Defined names only, case-insensitive; numeric strings are rejected.</summary>
    public static bool TryParseEnum<T>(string? raw, out T value) where T : struct, Enum =>
        Enum.TryParse(raw, ignoreCase: true, out value) && Enum.IsDefined(value) && !int.TryParse(raw, out _);

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
}

/// <summary>Privacy settings as names, the shape of the stored JSON and of the API; on update, a key left out keeps its stored value.</summary>
public sealed record ProfilePrivacyNames(
    string? Profile,
    Dictionary<string, string>? Sections,
    List<string>? HiddenFields,
    bool? HideAdultTitles)
{
    /// <summary>Unknown names are ignored; anything left out keeps its value in <paramref name="current"/>, and hidden fields are replaced as a whole.</summary>
    public ProfilePrivacy ApplyTo(ProfilePrivacy current)
    {
        var result = new ProfilePrivacy
                     {
                         Profile = ProfilePrivacy.TryParseEnum<ProfileVisibility>(Profile, out var profile) ? profile : current.Profile,
                         Sections = new Dictionary<ProfileSection, AudienceLevel>(current.Sections),
                         HiddenFields = HiddenFields == null ? [..current.HiddenFields] : [],
                         HideAdultTitles = HideAdultTitles ?? current.HideAdultTitles
                     };

        foreach (var (key, value) in Sections ?? [])
        {
            if (ProfilePrivacy.TryParseEnum<ProfileSection>(key, out var section) && ProfilePrivacy.TryParseEnum<AudienceLevel>(value, out var level))
                result.Sections[section] = level;
        }

        foreach (var name in HiddenFields ?? [])
        {
            if (ProfilePrivacy.TryParseEnum<ProfileField>(name, out var field))
                result.HiddenFields.Add(field);
        }

        return result;
    }
}
