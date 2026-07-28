namespace Jiten.Api.Dtos.Requests;

public class SetVocabularyStateRequest
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public required string State { get; set; }

    /// <summary>Due date to put back when undoing a bury. Ignored unless it precedes the card's current due date.</summary>
    public DateTime? RestoreDue { get; set; }
}
