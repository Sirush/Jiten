using System.Linq.Expressions;
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

    /// <summary>SameSeries / SameSetting rows predate Series membership; nothing stores them any more.</summary>
    public static bool IsLegacyGroupType(DeckRelationshipType type) =>
        type is DeckRelationshipType.SameSeries or DeckRelationshipType.SameSetting;

    /// <summary>Story links (Sequel to Alternative) are the only storable types; they join lines and franchises.</summary>
    public static bool IsStoryLinkType(DeckRelationshipType type) =>
        type is >= DeckRelationshipType.Sequel and <= DeckRelationshipType.Alternative;

    public static readonly Expression<Func<DeckRelationship, bool>> IsStoryLink =
        r => r.RelationshipType >= DeckRelationshipType.Sequel && r.RelationshipType <= DeckRelationshipType.Alternative;

    /// <summary>Null when the edge can be stored; otherwise the reason it cannot.</summary>
    public static string? ValidateEdge(int sourceDeckId, int targetDeckId, DeckRelationshipType type)
    {
        if (IsLegacyGroupType(type))
            return "Use series membership for same series and same setting links.";
        if (!IsStoryLinkType(type))
            return $"Relationship type {(int)type} cannot be stored.";
        if (sourceDeckId == targetDeckId)
            return "A deck cannot be related to itself.";
        return null;
    }

    /// <summary>Matches the edge as given or stored from the other end with the inverse type.</summary>
    public static Expression<Func<DeckRelationship, bool>> SameEdge(int sourceDeckId, int targetDeckId, DeckRelationshipType type)
    {
        var inverse = GetInverse(type);
        return r => (r.SourceDeckId == sourceDeckId && r.TargetDeckId == targetDeckId && r.RelationshipType == type) ||
                    (r.SourceDeckId == targetDeckId && r.TargetDeckId == sourceDeckId && r.RelationshipType == inverse);
    }

    /// <summary>(earlier, later) of a stored edge: the later work is the source except for Adaptation; null when undirected.</summary>
    public static (int Earlier, int Later)? StoryFlow(DeckRelationshipType type, int sourceDeckId, int targetDeckId) => type switch
    {
        DeckRelationshipType.Sequel or DeckRelationshipType.Fandisc or DeckRelationshipType.Spinoff or DeckRelationshipType.SideStory
            => (targetDeckId, sourceDeckId),
        DeckRelationshipType.Adaptation => (sourceDeckId, targetDeckId),
        _ => null
    };
}
