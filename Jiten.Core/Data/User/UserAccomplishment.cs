namespace Jiten.Core.Data;

public class UserAccomplishment
{
    public int AccomplishmentId { get; set; }

    public string UserId { get; set; } = string.Empty;
    public MediaType? MediaType { get; set; }  // null = global (all types)

    // Aggregated statistics
    public int CompletedDeckCount { get; set; }

    /// <summary>Leaf units behind the completed decks: a completed parent contributes its child count, a childless deck contributes 1.</summary>
    public int CompletedUnitCount { get; set; }

    public long TotalCharacterCount { get; set; }
    public long TotalWordCount { get; set; }
    public int UniqueWordCount { get; set; }
    public int UniqueWordUsedOnceCount { get; set; }
    public int UniqueKanjiCount { get; set; }

    public DateTimeOffset LastComputedAt { get; set; }
}
