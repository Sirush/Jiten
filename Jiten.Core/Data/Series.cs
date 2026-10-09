using System.Text.Json.Serialization;

namespace Jiten.Core.Data;

/// <summary>A named group of decks: a series, whose members share a franchise, or a setting, which never merges franchises.</summary>
public class Series
{
    public int SeriesId { get; set; }
    public string Name { get; set; } = "";
    public SeriesKind Kind { get; set; }
    [JsonIgnore]
    public ICollection<SeriesMember> Members { get; set; } = new List<SeriesMember>();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class SeriesMember
{
    public int SeriesId { get; set; }
    public int DeckId { get; set; }

    [JsonIgnore]
    public Series Series { get; set; } = null!;

    [JsonIgnore]
    public Deck Deck { get; set; } = null!;
}
