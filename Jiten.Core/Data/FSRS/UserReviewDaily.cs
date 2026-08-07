namespace Jiten.Core.Data.FSRS;

/// <summary>
/// Per-user, per-local-day review counts serving the heatmap, streaks and activity totals. Derived from live
/// logs plus archived blobs and fully rebuildable.
/// </summary>
public class UserReviewDaily
{
    public string UserId { get; set; } = default!;

    /// <summary>Date in the user's configured study timezone, so day boundaries match burying.</summary>
    public DateOnly LocalDate { get; set; }

    public int ReviewCount { get; set; }
    public int CorrectCount { get; set; }
    /// <summary>First review of a card, counted once per card lifetime, so a re-added card counts again.</summary>
    public int NewCardCount { get; set; }
    public long TotalDurationMs { get; set; }
}
