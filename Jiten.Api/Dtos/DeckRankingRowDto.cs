namespace Jiten.Api.Dtos;

/// <summary>Minimal row for the public by-difficulty ranking pages</summary>
public class DeckRankingRowDto
{
    public int DeckId { get; set; }
    public string OriginalTitle { get; set; } = "";
    public string RomajiTitle { get; set; } = "";
    public string EnglishTitle { get; set; } = "";

    /// <summary>Adjusted raw difficulty (0-5), community vote adjustment included.</summary>
    public float Difficulty { get; set; }

    public int CharacterCount { get; set; }
    public int? ReleaseYear { get; set; }
}
