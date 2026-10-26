using System.Text.Json.Serialization;

namespace Jiten.Core.Data.CommunityLists;

public class CommunityListTag
{
    public int ListId { get; set; }
    public int TagId { get; set; }

    [JsonIgnore]
    public CommunityList List { get; set; } = null!;

    public Tag Tag { get; set; } = null!;
}
