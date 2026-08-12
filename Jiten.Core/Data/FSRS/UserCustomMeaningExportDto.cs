using System.Text.Json.Serialization;

namespace Jiten.Core.Data.FSRS;

public class UserCustomMeaningExportDto
{
    [JsonPropertyName("w")]
    public required int WordId { get; set; }

    /// <summary>The user's own note for the word.</summary>
    [JsonPropertyName("m")]
    public required string Text { get; set; }

    [JsonPropertyName("ca")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("ua")]
    public long UpdatedAt { get; set; }

    /// <summary>Surface of the word, written only when the backup carries word text. Ignored on import.</summary>
    [JsonPropertyName("t")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WordText { get; set; }

    /// <summary>Kana reading of <see cref="WordText"/>. Ignored on import.</summary>
    [JsonPropertyName("k")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WordReading { get; set; }
}
