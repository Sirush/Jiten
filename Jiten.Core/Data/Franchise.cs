using System.Text.Json.Serialization;

namespace Jiten.Core.Data;

/// <summary>Decks connected by story links or a shared series; membership lives in <see cref="Deck.FranchiseId"/>.</summary>
public class Franchise
{
    public int FranchiseId { get; set; }
    public string Name { get; set; } = "";

    /// <summary>An automatic name is replaced by every sync; a manual one is kept.</summary>
    public bool NameIsManual { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public ICollection<Deck> Decks { get; set; } = new List<Deck>();
}
