using System.Text.Json.Serialization;

namespace Jiten.Core.Data.CommunityLists;

public class CommunityListGenre
{
    public int ListId { get; set; }
    public Genre Genre { get; set; }

    [JsonIgnore]
    public CommunityList List { get; set; } = null!;
}
