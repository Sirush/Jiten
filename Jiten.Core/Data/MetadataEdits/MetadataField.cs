namespace Jiten.Core.Data.MetadataEdits;

/// <summary>Community-editable deck metadata fields; also stored on <see cref="Deck.MetadataLocks"/>, where only scalar bits are meaningful.</summary>
[Flags]
public enum MetadataField
{
    None = 0,
    OriginalTitle = 1,
    RomajiTitle = 2,
    EnglishTitle = 4,
    Aliases = 8,
    ReleaseDate = 16,
    Description = 32,
    MediaType = 64,
    Genres = 128,
    Tags = 256,
    Links = 512,
    Relationships = 1024,
    CharacterNames = 2048
}

/// <summary>Risk tier that decides who may apply an edit without review.</summary>
public enum MetadataRiskTier
{
    Low = 0,
    Medium = 1,
    High = 2
}

public static class MetadataFields
{
    /// <summary>Fields rendered into search results and social embeds, so applying them pings IndexNow.</summary>
    public const MetadataField Indexed = MetadataField.OriginalTitle | MetadataField.RomajiTitle | MetadataField.EnglishTitle |
                                         MetadataField.Description | MetadataField.MediaType;

    public static readonly MetadataField[] All =
    [
        MetadataField.OriginalTitle, MetadataField.RomajiTitle, MetadataField.EnglishTitle, MetadataField.Aliases,
        MetadataField.ReleaseDate, MetadataField.Description, MetadataField.MediaType, MetadataField.Genres,
        MetadataField.Tags, MetadataField.Links, MetadataField.Relationships, MetadataField.CharacterNames
    ];

    public static MetadataRiskTier Tier(MetadataField field) => field switch
    {
        MetadataField.RomajiTitle or MetadataField.EnglishTitle or MetadataField.Aliases or
            MetadataField.ReleaseDate or MetadataField.Links => MetadataRiskTier.Low,
        MetadataField.Genres or MetadataField.Tags or MetadataField.Relationships or
            MetadataField.CharacterNames => MetadataRiskTier.Medium,
        _ => MetadataRiskTier.High
    };

    public static IEnumerable<MetadataField> Split(MetadataField fields) => All.Where(f => fields.HasFlag(f));

    public static MetadataField Of(IEnumerable<MetadataField> fields) => fields.Aggregate(MetadataField.None, (all, f) => all | f);

    public static MetadataField Of(IEnumerable<MetadataFieldChange> changes) => Of(changes.Select(c => c.Field));
}
