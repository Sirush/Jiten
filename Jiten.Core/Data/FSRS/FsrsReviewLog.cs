namespace Jiten.Core.Data.FSRS;

/// <summary>
/// Represents a record of a card review session
/// </summary>
public class FsrsReviewLog
{
    public long ReviewLogId { get; set; }
    
    /// <summary>
    /// ID of the card that was reviewed
    /// </summary>
    public long CardId { get; set; }

    /// <summary>
    /// Navigation property to the reviewed card
    /// </summary>
    public FsrsCard Card { get; set; } = null!;

    /// <summary>
    /// Rating given during the review
    /// </summary>
    public FsrsRating Rating { get; set; }

    /// <summary>
    /// When the review took place
    /// </summary>
    public DateTime ReviewDateTime { get; set; }

    /// <summary>
    /// How long the review took in milliseconds (optional)
    /// </summary>
    public int? ReviewDuration { get; set; }

    /// <summary>The card's state when it was answered; null on logs written before it was recorded.</summary>
    public FsrsState? State { get; set; }

    public FsrsReviewLog()
    {
    }
    
    /// <summary>
    /// Creates a new review log entry
    /// </summary>
    public FsrsReviewLog(
        long cardId,
        FsrsRating rating,
        DateTime reviewDateTime,
        int? reviewDuration = null)
    {
        CardId = cardId;
        Rating = rating;
        ReviewDateTime = reviewDateTime;
        ReviewDuration = reviewDuration;
    }
}