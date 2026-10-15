using System.Text.Json.Serialization;

namespace Jiten.Core.Data;

public class MediaRequestTag
{
    public int MediaRequestId { get; set; }
    public int TagId { get; set; }
    public byte Percentage { get; set; }

    [JsonIgnore]
    public MediaRequest MediaRequest { get; set; } = null!;
    public Tag Tag { get; set; } = null!;
}
