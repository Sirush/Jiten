using System.Text.Json.Serialization;

namespace Jiten.Core.Data;

public class DeckRelationship
{
    public int SourceDeckId { get; set; }
    public int TargetDeckId { get; set; }
    public DeckRelationshipType RelationshipType { get; set; }

    [JsonIgnore]
    public Deck SourceDeck { get; set; } = null!;
    [JsonIgnore]
    public Deck TargetDeck { get; set; } = null!;

    public DeckRelationshipType InverseRelationshipType => GetInverse(RelationshipType);

    public static DeckRelationshipType GetInverse(DeckRelationshipType type) => type switch
    {
        DeckRelationshipType.Sequel => DeckRelationshipType.Prequel,
        DeckRelationshipType.Prequel => DeckRelationshipType.Sequel,
        DeckRelationshipType.Fandisc => DeckRelationshipType.HasFandisc,
        DeckRelationshipType.Spinoff => DeckRelationshipType.HasSpinoff,
        DeckRelationshipType.SideStory => DeckRelationshipType.HasSideStory,
        DeckRelationshipType.Adaptation => DeckRelationshipType.SourceMaterial,
        DeckRelationshipType.Alternative => DeckRelationshipType.Alternative,
        DeckRelationshipType.SameSeries => DeckRelationshipType.SameSeries,
        DeckRelationshipType.SameSetting => DeckRelationshipType.SameSetting,
        DeckRelationshipType.HasFandisc => DeckRelationshipType.Fandisc,
        DeckRelationshipType.HasSpinoff => DeckRelationshipType.Spinoff,
        DeckRelationshipType.HasSideStory => DeckRelationshipType.SideStory,
        DeckRelationshipType.SourceMaterial => DeckRelationshipType.Adaptation,
        _ => type
    };

    public static bool IsPrimaryRelationship(DeckRelationshipType type) =>
        (int)type < 100;
}
