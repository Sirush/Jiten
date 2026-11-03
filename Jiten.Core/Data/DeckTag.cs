using System.Text.Json.Serialization;

namespace Jiten.Core.Data;

public class DeckTag
{
    public int DeckId { get; set; }
    public int TagId { get; set; }
    public byte Percentage { get; set; }

    /// <summary>Percentage set by a person; provider mappings leave the row alone.</summary>
    public bool IsManual { get; set; }

    [JsonIgnore]
    public Deck Deck { get; set; } = null!;
    public Tag Tag { get; set; } = null!;
}
