using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class MediaGroupTitlesDto
{
    public string OriginalTitle { get; set; } = "";
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }
}

public class MediaGroupMembersDto
{
    public MediaGroupKind Kind { get; set; }
    public int Id { get; set; }

    /// <summary>Franchise or series name; a line's anchor OriginalTitle.</summary>
    public string Name { get; set; } = "";

    public int? FranchiseId { get; set; }

    /// <summary>Every deck of the group, filters not applied, in release order.</summary>
    public List<FranchiseNodeDto> Members { get; set; } = new();
}
