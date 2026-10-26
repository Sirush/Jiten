using System.Text.Json.Serialization;

namespace Jiten.Core.Data.CommunityLists;

public class CommunityListItem
{
    public const int MaxNoteLength = 300;

    public int ListId { get; set; }
    public int DeckId { get; set; }
    public int Position { get; set; }
    public string? Note { get; set; }

    [JsonIgnore]
    public CommunityList List { get; set; } = null!;

    [JsonIgnore]
    public Deck Deck { get; set; } = null!;
}
