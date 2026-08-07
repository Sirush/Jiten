using System.Text.Json.Serialization;

namespace Jiten.Core.Data.FSRS;

public class FsrsCardExportDto
{
    [JsonPropertyName("w")]
    public required int WordId { get; set; }

    [JsonPropertyName("r")]
    public required byte ReadingIndex { get; set; }

    [JsonPropertyName("s")]
    public required FsrsState State { get; set; }

    [JsonPropertyName("sp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Step { get; set; }

    [JsonPropertyName("st")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Stability { get; set; }

    [JsonPropertyName("d")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Difficulty { get; set; }

    [JsonPropertyName("du")]
    public required long Due { get; set; }

    [JsonPropertyName("lr")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? LastReview { get; set; }

    [JsonPropertyName("ca")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("l")]
    public List<FsrsReviewLogExportDto> ReviewLogs { get; set; } = [];

    /// <summary>Surface of the exported form, written only when the backup was asked to carry word text. Ignored on import.</summary>
    [JsonPropertyName("t")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    /// <summary>Kana reading of <see cref="Text"/>, absent when no reading could be resolved. Ignored on import.</summary>
    [JsonPropertyName("k")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reading { get; set; }
}