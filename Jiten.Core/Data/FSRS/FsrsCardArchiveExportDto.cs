using System.Text.Json.Serialization;

namespace Jiten.Core.Data.FSRS;

public class FsrsCardArchiveExportDto
{
    [JsonPropertyName("w")]
    public required int WordId { get; set; }

    [JsonPropertyName("r")]
    public required byte ReadingIndex { get; set; }

    [JsonPropertyName("aa")]
    public long ArchivedAt { get; set; }

    [JsonPropertyName("rn")]
    public CardArchiveReason Reason { get; set; }

    [JsonPropertyName("ci")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte? CoveringReadingIndex { get; set; }

    [JsonPropertyName("s")]
    public FsrsState State { get; set; }

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
    public long Due { get; set; }

    [JsonPropertyName("lr")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? LastReview { get; set; }

    [JsonPropertyName("lp")]
    public int Lapses { get; set; }

    [JsonPropertyName("ca")]
    public long CardCreatedAt { get; set; }

    /// <summary>True total, which exceeds the exported logs when the stored history was truncated.</summary>
    [JsonPropertyName("rc")]
    public int ReviewCount { get; set; }

    /// <summary>History spans more than one card lifetime, so the schedule above does not describe all of it.</summary>
    [JsonPropertyName("hm")]
    public bool HistoryMerged { get; set; }

    [JsonPropertyName("l")]
    public List<FsrsReviewLogExportDto> ReviewLogs { get; set; } = [];
}
