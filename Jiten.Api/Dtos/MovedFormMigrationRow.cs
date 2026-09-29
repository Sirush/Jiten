namespace Jiten.Api.Dtos;

public class MovedFormMigrationRow
{
    public string Text { get; set; } = "";
    public int OldWordId { get; set; }
    public short OldReadingIndex { get; set; }
    public int NewWordId { get; set; }
    public short NewReadingIndex { get; set; }
    public int OwnerCount { get; set; }

    /// <summary>"ambiguous" or "deleted" when the move is left to the reparse; null when it was remapped.</summary>
    public string? Skipped { get; set; }

    public WordReplacementResult? Result { get; set; }
}
