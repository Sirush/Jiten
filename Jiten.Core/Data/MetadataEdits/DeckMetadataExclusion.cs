using System.ComponentModel.DataAnnotations;

namespace Jiten.Core.Data.MetadataEdits;

/// <summary>A value a person removed from a deck; provider imports never add it back.</summary>
public class DeckMetadataExclusion
{
    public long Id { get; set; }
    public int DeckId { get; set; }
    public DeckMetadataExclusionKind Kind { get; set; }

    /// <summary>Genre as its int value, tag as its TagId, alias and character name as the literal surface.</summary>
    [MaxLength(300)]
    public required string Value { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(450)]
    public string? CreatedByUserId { get; set; }
}

public enum DeckMetadataExclusionKind
{
    Genre = 1,
    Tag = 2,
    Alias = 3,
    CharacterName = 4
}
