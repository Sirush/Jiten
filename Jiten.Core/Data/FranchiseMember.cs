using System.Text.Json.Serialization;

namespace Jiten.Core.Data;

/// <summary>A deck saved on a franchise builder board; it stays in that franchise without needing a link or a shared series.</summary>
public class FranchiseMember
{
    public int DeckId { get; set; }
    public int FranchiseId { get; set; }

    [JsonIgnore]
    public Deck Deck { get; set; } = null!;

    [JsonIgnore]
    public Franchise Franchise { get; set; } = null!;
}
