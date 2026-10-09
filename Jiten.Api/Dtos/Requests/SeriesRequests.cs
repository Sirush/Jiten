using Jiten.Core.Data;

namespace Jiten.Api.Dtos.Requests;

public class CreateSeriesRequest
{
    public string Name { get; set; } = "";
    public SeriesKind Kind { get; set; }
}

public class UpdateSeriesRequest
{
    public string? Name { get; set; }
}

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
}

public class UpdateFranchiseRequest
{
    public string? Name { get; set; }
}

public class FranchiseSuggestionDismissRequest
{
    public string RootKey { get; set; } = "";
    public List<int> DeckIds { get; set; } = new();
}
