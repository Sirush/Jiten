using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiten.Core.Data;

/// <summary>
/// Detailed difficulty metrics for a deck
/// </summary>
public class DeckDifficulty
{
    [Key]
    [ForeignKey("Deck")]
    public int DeckId { get; set; }

    /// <summary>
    /// Overall difficulty (2 decimal precision, e.g. 3.52)
    /// </summary>
    public decimal Difficulty { get; set; }

    /// <summary>
    /// Peak difficulty (2 decimal precision, e.g. 3.92)
    /// </summary>
    public decimal Peak { get; set; }

    /// <summary>
    /// JSONB storage for deciles: {"10": 1.2, "20": 1.5, ...}
    /// </summary>
    public string DecilesJson { get; set; } = "{}";

    /// <summary>
    /// JSONB storage for progression segments
    /// </summary>
    public string ProgressionJson { get; set; } = "[]";

    public int EasierVoteCount { get; set; }
    public int HarderVoteCount { get; set; }
    public int DistinctVoterCount { get; set; }
    public decimal UserAdjustment { get; set; }
    public decimal NEffective { get; set; }

    /// <summary>Zero while the votes fail the confidence gate, i.e. they are recorded but not yet applied.</summary>
    public decimal AdjustmentConfidence { get; set; }

    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public Deck Deck { get; set; } = null!;

    [NotMapped]
    public Dictionary<string, decimal> Deciles
    {
        get => JsonSerializer.Deserialize<Dictionary<string, decimal>>(DecilesJson) ?? new();
        set => DecilesJson = JsonSerializer.Serialize(value);
    }

    [NotMapped]
    public List<ProgressionSegment> Progression
    {
        get => JsonSerializer.Deserialize<List<ProgressionSegment>>(ProgressionJson) ?? [];
        set => ProgressionJson = JsonSerializer.Serialize(value);
    }
}

public class ProgressionSegment
{
    [JsonPropertyName("segment")]
    public int Segment { get; set; }

    [JsonPropertyName("difficulty")]
    public decimal Difficulty { get; set; }

    [JsonPropertyName("peak")]
    public decimal Peak { get; set; }

    [JsonPropertyName("childStartOrder")]
    public int? ChildStartOrder { get; set; }

    [JsonPropertyName("childEndOrder")]
    public int? ChildEndOrder { get; set; }
}
