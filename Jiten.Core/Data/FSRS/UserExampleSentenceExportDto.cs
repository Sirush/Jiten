using System.Text.Json.Serialization;

namespace Jiten.Core.Data.FSRS;

public class UserExampleSentenceExportDto
{
    [JsonPropertyName("w")]
    public required int WordId { get; set; }

    [JsonPropertyName("r")]
    public required byte ReadingIndex { get; set; }

    /// <summary>Sentence text, with the target word wrapped in ** markers.</summary>
    [JsonPropertyName("s")]
    public required string Text { get; set; }

    [JsonPropertyName("src")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    [JsonPropertyName("o")]
    public byte SortOrder { get; set; }

    [JsonPropertyName("ca")]
    public long CreatedAt { get; set; }

    /// <summary>Surface of the form the sentence is attached to, written only when the backup carries word text. Ignored on import.</summary>
    [JsonPropertyName("t")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WordText { get; set; }

    /// <summary>Kana reading of <see cref="WordText"/>. Ignored on import.</summary>
    [JsonPropertyName("k")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WordReading { get; set; }
}
