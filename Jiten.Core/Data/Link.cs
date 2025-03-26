using System.Text.Json.Serialization;

namespace Jiten.Core.Data;

public class Link
{
    public int LinkId { get; set; }
    public LinkType LinkType { get; set; }
    public string Url { get; set; } = string.Empty;
    public int DeckId { get; set; }
    
    [JsonIgnore]
    public Deck Deck { get; set; } = new();
}