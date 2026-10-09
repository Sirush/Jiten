using System.Text.Json.Serialization;

namespace Jiten.Core.Data;

/// <summary>Decks connected by story links, a shared series or a saved builder board; membership lives in <see cref="Deck.FranchiseId"/>.</summary>
public class Franchise
{
    public int FranchiseId { get; set; }
    public string OriginalTitle { get; set; } = "";
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }

    /// <summary>Automatic titles are replaced by every sync; manual ones are kept.</summary>
    public bool NameIsManual { get; set; }

    public GroupTitles Titles() => new(OriginalTitle, RomajiTitle, EnglishTitle);

    public void SetTitles(GroupTitles titles) => (OriginalTitle, RomajiTitle, EnglishTitle) = (titles.OriginalTitle, titles.RomajiTitle, titles.EnglishTitle);

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public ICollection<Deck> Decks { get; set; } = new List<Deck>();
}
