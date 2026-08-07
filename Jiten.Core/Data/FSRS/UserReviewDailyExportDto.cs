using System.Text.Json.Serialization;

namespace Jiten.Core.Data.FSRS;

public class UserReviewDailyExportDto
{
    /// <summary>Local date in the study timezone the counters were built under, as yyyy-MM-dd.</summary>
    [JsonPropertyName("d")]
    public required string LocalDate { get; set; }

    [JsonPropertyName("r")]
    public int ReviewCount { get; set; }

    [JsonPropertyName("c")]
    public int CorrectCount { get; set; }

    [JsonPropertyName("n")]
    public int NewCardCount { get; set; }

    [JsonPropertyName("ms")]
    public long TotalDurationMs { get; set; }
}
