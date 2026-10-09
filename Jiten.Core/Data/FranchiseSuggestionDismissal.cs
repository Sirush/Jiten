using System.Text.Json.Serialization;

namespace Jiten.Core.Data;

/// <summary>An admin ruled that this deck does not belong with the others sharing <see cref="RootKey"/>; a deck joining that root later brings the group back.</summary>
public class FranchiseSuggestionDismissal
{
    public int DeckId { get; set; }

    /// <summary>Normalised (NFKC, lower-case) title root, as produced by <see cref="Services.FranchiseSuggestions"/>.</summary>
    public string RootKey { get; set; } = "";

    public DateTime DismissedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public Deck Deck { get; set; } = null!;
}
