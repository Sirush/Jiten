using System.Text.Json.Serialization;

namespace Jiten.Core.Data.FSRS;

public class FsrsReviewLogExportDto
{
    [JsonPropertyName("r")]
    public required FsrsRating Rating { get; set; }

    [JsonPropertyName("dt")]
    public required long ReviewDateTime { get; set; }

    [JsonPropertyName("d")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReviewDuration { get; set; }

    [JsonPropertyName("s")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FsrsState? State { get; set; }
}