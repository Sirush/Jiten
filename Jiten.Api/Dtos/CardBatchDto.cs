using Jiten.Core.Data.FSRS;

namespace Jiten.Api.Dtos;

public class CardBatchDto
{
    public Guid BatchId { get; set; }
    public CardSource Source { get; set; }
    public CardBatchDetails Details { get; set; } = new();

    /// <summary>Titles of <see cref="CardBatchDetails.DeckId"/>; null once the deck is gone.</summary>
    public CardBatchDeckDto? Deck { get; set; }
    public DateTime CreatedAt { get; set; }
    public int CardCount { get; set; }

    /// <summary>Distinct words among the batch's current cards, one word having a card per spelling; once undone, the cards it had inserted.</summary>
    public int WordCount { get; set; }

    /// <summary>Distinct words an undo would remove right now; studied or changed cards are excluded, and an undone batch reports 0.</summary>
    public int RemovableCount { get; set; }

    public bool Undone { get; set; }
}

public sealed record CardBatchDeckDto(int DeckId, string OriginalTitle, string? RomajiTitle, string? EnglishTitle);

public class CardBatchUndoResult
{
    public int Removed { get; set; }

    /// <summary>Distinct words the batch inserted that survive because the user studied or changed them since.</summary>
    public int Kept { get; set; }

    public bool AlreadyUndone { get; set; }
}
