using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

/// <summary>The decks of one franchise with the story links between them, its series and the settings its decks share.</summary>
public class FranchiseDto
{
    /// <summary>Null for a deck that belongs to no franchise; the DTO then holds that deck alone.</summary>
    public int? FranchiseId { get; set; }

    /// <summary>Null with <see cref="FranchiseId"/>.</summary>
    public string? OriginalTitle { get; set; }

    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }
    public bool NameIsManual { get; set; }
    public List<FranchiseNodeDto> Nodes { get; set; } = new();
    public List<FranchiseEdgeDto> Edges { get; set; } = new();

    /// <summary>Every story-link component of the franchise, single decks included, earliest anchor first.</summary>
    public List<FranchiseLineDto> Lines { get; set; } = new();

    /// <summary>Every series with a member in the franchise.</summary>
    public List<FranchiseSeriesDto> Series { get; set; } = new();

    public List<FranchiseSettingDto> Settings { get; set; } = new();

    /// <summary>"series" when one series holds 2+ entries (story lines or standalone members), else "timeline".</summary>
    public string PreferredView { get; set; } = FranchiseViews.Timeline;

    /// <summary>Franchise builder only: decks put on the board that are not in the franchise yet.</summary>
    public List<int> BoardOnlyDeckIds { get; set; } = new();

    public void SetTitles(GroupTitles titles) => (OriginalTitle, RomajiTitle, EnglishTitle) = (titles.OriginalTitle, titles.RomajiTitle, titles.EnglishTitle);

    /// <summary>Copy with its own node instances (setting outsiders included) for per-viewer data; every other list is shared.</summary>
    public FranchiseDto CloneNodes()
    {
        var copy = (FranchiseDto)MemberwiseClone();
        copy.Nodes = Nodes.Select(n => n.Clone()).ToList();
        copy.Settings = Settings.Select(s => s.CloneOutside()).ToList();
        return copy;
    }
}

public static class FranchiseViews
{
    public const string Timeline = "timeline";
    public const string Series = "series";
}

public class DeckFranchiseDto
{
    /// <summary>Null when the deck belongs to no franchise.</summary>
    public int? FranchiseId { get; set; }
}

public class FranchiseLineDto
{
    /// <summary>Earliest-released deck of the line (lowest DeckId on ties); identifies and names it.</summary>
    public int AnchorDeckId { get; set; }

    public List<int> DeckIds { get; set; } = new();
}

public class FranchiseSummaryDto : MediaGroupTitlesDto
{
    public int FranchiseId { get; set; }
    public bool NameIsManual { get; set; }
    public int DeckCount { get; set; }
    public int SeriesCount { get; set; }

    /// <summary>Any member deck, to open the franchise builder on.</summary>
    public int? FirstDeckId { get; set; }
}

public enum FranchiseListSort
{
    Name,
    Decks,
    Series
}

public class FranchiseSeriesDto : MediaGroupTitlesDto
{
    public int SeriesId { get; set; }
    public List<int> MemberDeckIds { get; set; } = new();
}

public class FranchiseSettingDto : MediaGroupTitlesDto
{
    public int SeriesId { get; set; }
    public List<int> MemberDeckIds { get; set; } = new();
    public List<FranchiseNodeDto> Outside { get; set; } = new();
    public int OutsideCount { get; set; }

    public FranchiseSettingDto CloneOutside()
    {
        var copy = (FranchiseSettingDto)MemberwiseClone();
        copy.Outside = Outside.Select(n => n.Clone()).ToList();
        return copy;
    }
}

/// <summary>
/// A single deck in the franchise graph. Deliberately lighter than <see cref="DeckDto"/>:
/// just enough to render a cover card with learner stats.
/// </summary>
public class FranchiseNodeDto
{
    public int DeckId { get; set; }
    public string OriginalTitle { get; set; } = "Unknown";
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }
    public string CoverName { get; set; } = "nocover.jpg";
    public MediaType MediaType { get; set; }
    public DateTime ReleaseDate { get; set; }

    /// <summary>0-5 difficulty band, same mapping as <see cref="DeckDto.Difficulty"/>.</summary>
    public int Difficulty { get; set; }

    /// <summary>Adjusted raw difficulty (0-5 float), same value as <see cref="DeckDto.DifficultyRaw"/>;
    /// needed so the client can honour the user's value/percentage difficulty display style.</summary>
    public float DifficultyRaw { get; set; }

    /// <summary>Viewer's mature word coverage (%), populated only for authenticated requests; 0 otherwise.</summary>
    public float Coverage { get; set; }

    /// <summary>Viewer's mature unique-word coverage (%), populated only for authenticated requests; 0 otherwise.</summary>
    public float UniqueCoverage { get; set; }

    public FranchiseNodeDto Clone() => (FranchiseNodeDto)MemberwiseClone();
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
