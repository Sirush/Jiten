namespace Jiten.Api.Dtos;

/// <summary>Slim row for the SSR vocabulary preview on the deck detail page</summary>
public class DeckVocabularyPreviewWordDto
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public string Reading { get; set; } = "";
    public string ReadingFurigana { get; set; } = "";
    public string? MainDefinition { get; set; }
    public int? FrequencyRank { get; set; }
    public int Occurrences { get; set; }
}
