using Jiten.Core.Data;

namespace Jiten.Api.Dtos.Requests;

public class GroupTitlesRequest
{
    public string? OriginalTitle { get; set; }
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }

    public GroupTitles ToTitles() => GroupTitles.Of(OriginalTitle, RomajiTitle, EnglishTitle);

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(OriginalTitle))
            return "The original title cannot be empty.";
        if (new[] { OriginalTitle, RomajiTitle, EnglishTitle }.Any(t => t != null && t.Trim().Length > GroupTitles.MaxLength))
            return $"Titles must be at most {GroupTitles.MaxLength} characters.";
        return null;
    }
}

public class CreateSeriesRequest : GroupTitlesRequest
{
    public SeriesKind Kind { get; set; }
}

/// <summary>A null original title leaves all three titles unchanged; otherwise all three are replaced.</summary>
public class UpdateSeriesRequest : GroupTitlesRequest;

public class SeriesMembersRequest
{
    public List<int> DeckIds { get; set; } = new();
}

public class FranchiseBuilderMember
{
    public int SeriesId { get; set; }
    public int DeckId { get; set; }
}

public class FranchiseBuilderSaveRequest
{
    public int AnchorDeckId { get; set; }
    public List<FranchiseEdgeDto> AddEdges { get; set; } = new();
    public List<FranchiseEdgeDto> RemoveEdges { get; set; } = new();
    public List<FranchiseBuilderMember> AddMembers { get; set; } = new();
    public List<FranchiseBuilderMember> RemoveMembers { get; set; } = new();
    public List<int> BoardDeckIds { get; set; } = new();
}

/// <summary>A blank original title returns the franchise to automatic titles.</summary>
public class UpdateFranchiseRequest : GroupTitlesRequest;

public class FranchiseSuggestionDismissRequest
{
    public string RootKey { get; set; } = "";
    public List<int> DeckIds { get; set; } = new();
}
