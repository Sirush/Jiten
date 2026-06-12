using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

/// <summary>
/// The full connected component of related media around a deck: lightweight nodes plus the
/// primary-typed edges that connect them. <see cref="Truncated"/> is true when the node cap
/// stopped the traversal before the component was fully expanded.
/// </summary>
public class FranchiseDto
{
    public List<FranchiseNodeDto> Nodes { get; set; } = new();
    public List<FranchiseEdgeDto> Edges { get; set; } = new();
    public bool Truncated { get; set; }
}

/// <summary>
/// A single deck in the franchise graph. Deliberately lighter than <see cref="DeckDto"/>:
/// just enough to render a cover card with learner stats.
/// </summary>
public class FranchiseNodeDto
{
    public int DeckId { get; set; }
    public string OriginalTitle { get; set; } = "Unknown";
    public string RomajiTitle { get; set; } = "";
    public string EnglishTitle { get; set; } = "";
    public string CoverName { get; set; } = "nocover.jpg";
    public MediaType MediaType { get; set; }
    public DateTime ReleaseDate { get; set; }

    /// <summary>0-5 difficulty band, same mapping as <see cref="DeckDto.Difficulty"/>.</summary>
    public int Difficulty { get; set; }

    /// <summary>Adjusted raw difficulty (0-5 float), same value as <see cref="DeckDto.DifficultyRaw"/>;
    /// needed so the client can honour the user's value/percentage difficulty display style.</summary>
    public float DifficultyRaw { get; set; }
    public int CharacterCount { get; set; }
    public int WordCount { get; set; }
    public int ChildrenDeckCount { get; set; }

    /// <summary>Viewer's mature word coverage (%), populated only for authenticated requests; 0 otherwise.</summary>
    public float Coverage { get; set; }

    /// <summary>Viewer's mature unique-word coverage (%), populated only for authenticated requests; 0 otherwise.</summary>
    public float UniqueCoverage { get; set; }
}

/// <summary>
/// A directed edge as stored in the database (primary relationship types only, never inverses).
/// </summary>
public class FranchiseEdgeDto
{
    public int SourceDeckId { get; set; }
    public int TargetDeckId { get; set; }
    public DeckRelationshipType RelationshipType { get; set; }
}
