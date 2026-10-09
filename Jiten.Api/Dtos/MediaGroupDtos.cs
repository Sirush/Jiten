using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class MediaGroupTitlesDto
{
    public string OriginalTitle { get; set; } = "";
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }

    public static MediaGroupTitlesDto From(GroupTitles titles) =>
        new() { OriginalTitle = titles.OriginalTitle, RomajiTitle = titles.RomajiTitle, EnglishTitle = titles.EnglishTitle };
}

/// <summary>Titles are the franchise's or series', a line's anchor titles.</summary>
public class MediaGroupMembersDto : MediaGroupTitlesDto
{
    public MediaGroupKind Kind { get; set; }
    public int Id { get; set; }

    public int? FranchiseId { get; set; }

    /// <summary>Every deck of the group, filters not applied, in release order.</summary>
    public List<FranchiseNodeDto> Members { get; set; } = new();
}
