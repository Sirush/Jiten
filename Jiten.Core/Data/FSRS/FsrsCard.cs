namespace Jiten.Core.Data.FSRS;

/// <summary>
/// Represents a spaced repetition card with scheduling information
/// </summary>
public class FsrsCard
{
    /// <summary>
    /// Unique identifier for the card
    /// </summary>
    public long CardId { get; set; }

    public string UserId { get; set; } = default!;
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }

    /// <summary>
    /// Current learning state of the card
    /// </summary>
    public FsrsState State { get; set; }

    /// <summary>
    /// Current step in learning/relearning sequence (null for review state)
    /// </summary>
    public int? Step { get; set; }

    /// <summary>
    /// Memory stability in days - how long the card can be remembered
    /// </summary>
    public double? Stability { get; set; }

    /// <summary>
    /// Card difficulty on a scale of 1-10
    /// </summary>
    public double? Difficulty { get; set; }

    /// <summary>
    /// When the card is next due for review
    /// </summary>
    public DateTime Due { get; set; }

    /// <summary>
    /// When the card was last reviewed (null for new cards)
    /// </summary>
    public DateTime? LastReview { get; set; }

    public int Lapses { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Review history for this card
    /// </summary>
    public ICollection<FsrsReviewLog> ReviewLogs { get; set; } = new List<FsrsReviewLog>();

    public FsrsCard()
    {
    }

    /// <summary>
    /// Creates a new card with default or specified values
    /// </summary>
    public FsrsCard(
        string userId,
        int wordId,
        byte readingIndex,
        long? cardId = null,
        FsrsState state = FsrsState.Learning,
        int? step = null,
        double? stability = null,
        double? difficulty = null,
        DateTime? due = null,
        DateTime? lastReview = null)
    {
        CardId = cardId ?? 0;
        UserId = userId;
        WordId = wordId;
        ReadingIndex = readingIndex;
        State = state;
        Step = state == FsrsState.Learning && step == null ? 0 : step;
        Stability = stability;
        Difficulty = difficulty;
        Due = due ?? DateTime.UtcNow;
        LastReview = lastReview;
        CreatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates a deep copy of the card
    /// </summary>
    public FsrsCard Clone()
    {
        return new FsrsCard(UserId, WordId, ReadingIndex, CardId, State, Step, Stability, Difficulty, Due, LastReview)
        {
            Lapses = Lapses,
            CreatedAt = CreatedAt
        };
    }
}