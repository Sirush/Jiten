using System.Text.Json.Serialization;

namespace Jiten.Core.Data.FSRS;

public class CardBatchExportDto
{
    [JsonPropertyName("id")]
    public required Guid BatchId { get; set; }

    [JsonPropertyName("ca")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("n")]
    public int CardCount { get; set; }

    [JsonPropertyName("d")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CardBatchDetails? Details { get; set; }

    [JsonPropertyName("s")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FsrsState? CardState { get; set; }

    [JsonPropertyName("u")]
    public bool Undone { get; set; }
}
