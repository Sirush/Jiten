namespace Jiten.Api.Dtos.Requests;

public class BulkSetVocabularyStateRequest
{
    public required List<BulkVocabularyStateItem> Items { get; set; }
    public required string State { get; set; }
}

public class BulkVocabularyStateItem
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
}
