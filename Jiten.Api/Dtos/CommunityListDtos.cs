using Jiten.Core.Data;
using Jiten.Core.Data.CommunityLists;

namespace Jiten.Api.Dtos;

public class CommunityListSummaryDto
{
    public int ListId { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? OwnerDisplayName { get; set; }

    /// <summary>True when the owner's profile is public, or the viewer is the owner; friends-only profiles stay unlinked.</summary>
    public bool CanOpenOwnerProfile { get; set; }

    public List<Genre> Genres { get; set; } = [];
    public List<TagDto> Tags { get; set; } = [];
    public int FavouriteCount { get; set; }
    public int ItemCount { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public List<string> PreviewCoverNames { get; set; } = [];
    public bool IsFavourited { get; set; }
    public bool IsOwner { get; set; }

    /// <summary>Only sent to the owner and admins; everyone else only ever sees public lists.</summary>
    public CommunityListStatus? Status { get; set; }
}

/// <summary>Viewer's mature coverage of every item, the figure the item cards show.</summary>
public class CommunityListCoverageSummaryDto
{
    /// <summary>One percentage per item, highest first.</summary>
    public List<float> Values { get; set; } = [];

    /// <summary>Null when the viewer has no coverage on any item.</summary>
    public CommunityListEasiestDto? Easiest { get; set; }
}

public class CommunityListEasiestDto
{
    public int DeckId { get; set; }
    public required string OriginalTitle { get; set; }
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }
    public float Coverage { get; set; }
}

public class CommunityListStatusCountDto
{
    public DeckStatus Status { get; set; }
    public int Count { get; set; }
}

public class CommunityListItemDto
{
    /// <summary>1-based rank in curator order, whatever order the page is sorted in.</summary>
    public int Position { get; set; }

    public string? Note { get; set; }
    public required DeckDto Deck { get; set; }
}

public class CommunityListDetailDto : CommunityListSummaryDto
{
    public bool CanEdit { get; set; }

    /// <summary>Null for anonymous viewers.</summary>
    public CommunityListCoverageSummaryDto? Coverage { get; set; }

    /// <summary>How many items the viewer already tracks, per status with a non-zero count; null for anonymous viewers.</summary>
    public List<CommunityListStatusCountDto>? StatusCounts { get; set; }

    public PaginatedResponse<List<CommunityListItemDto>> Items { get; set; } = null!;
}

public class CommunityListQuotaDto
{
    public int MaxLists { get; set; }
    public int MaxItems { get; set; }

    /// <summary>The Jiten+ Full caps, for upsell copy.</summary>
    public int FullMaxLists { get; set; }

    public int FullMaxItems { get; set; }
    public int MaxGenres { get; set; }
    public int MaxTags { get; set; }
    public int ListCount { get; set; }
    public bool CanCreate { get; set; }
}

public class CommunityListProfileDto
{
    public List<CommunityListSummaryDto> Written { get; set; } = [];

    /// <summary>Null when the owner keeps favourited lists from this viewer.</summary>
    public List<CommunityListSummaryDto>? Favourited { get; set; }
}

public class CommunityListMineDto
{
    public List<CommunityListSummaryDto> Lists { get; set; } = [];
    public required CommunityListQuotaDto Quota { get; set; }
}

public class CommunityListMembershipDto
{
    public int ListId { get; set; }
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public CommunityListStatus Status { get; set; }
    public int ItemCount { get; set; }

    /// <summary>How many of the asked titles the list already holds.</summary>
    public int Contained { get; set; }
}

public class CommunityListMembershipResponse
{
    public List<CommunityListMembershipDto> Lists { get; set; } = [];
    public required CommunityListQuotaDto Quota { get; set; }
}

public class CommunityListMembershipRequest
{
    public List<int> DeckIds { get; set; } = [];
}

public class AddCommunityListItemsRequest
{
    public List<CommunityListItemInput> Items { get; set; } = [];

    /// <summary>Zero-based slot for the first new item; null or past the end appends.</summary>
    public int? Position { get; set; }
}

public class CommunityListEditItemDto
{
    public int DeckId { get; set; }
    public string? Note { get; set; }
    public required string OriginalTitle { get; set; }
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }
    public MediaType MediaType { get; set; }
    public required string CoverName { get; set; }
    public List<Genre> Genres { get; set; } = [];
    public List<TagWithPercentageDto> Tags { get; set; } = [];
}

public class CommunityListEditDto
{
    public int ListId { get; set; }
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public CommunityListStatus Status { get; set; }
    public int FavouriteCount { get; set; }
    public List<Genre> Genres { get; set; } = [];
    public List<TagDto> Tags { get; set; } = [];
    public List<CommunityListEditItemDto> Items { get; set; } = [];
    public required CommunityListQuotaDto Quota { get; set; }
}

public class CommunityListBrowseResponse(List<CommunityListSummaryDto> data, int totalItems, int pageSize, int currentOffset, int? seed)
    : PaginatedResponse<List<CommunityListSummaryDto>>(data, totalItems, pageSize, currentOffset)
{
    /// <summary>Echoed for the random sort so the next page keeps the same order.</summary>
    public int? Seed { get; set; } = seed;
}

public class CreateCommunityListRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
}

public class UpdateCommunityListRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public CommunityListStatus Status { get; set; }
    public List<Genre> Genres { get; set; } = [];
    public List<int> TagIds { get; set; } = [];
}

public class CommunityListItemInput
{
    public int DeckId { get; set; }
    public string? Note { get; set; }
}

public class ReplaceCommunityListItemsRequest
{
    public List<CommunityListItemInput> Items { get; set; } = [];
}

public class ReportCommunityListRequest
{
    public string Reason { get; set; } = "";
}

public class AdminCommunityListReportDto
{
    public int ReportId { get; set; }
    public int ListId { get; set; }
    public required string ListName { get; set; }
    public required string ListSlug { get; set; }
    public CommunityListStatus ListStatus { get; set; }
    public string? ReporterUserName { get; set; }
    public required string Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

public class AdminCommunityListDto
{
    public int ListId { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public required string OwnerId { get; set; }
    public string? OwnerUserName { get; set; }
    public string? OwnerDisplayName { get; set; }
    public CommunityListStatus Status { get; set; }
    public int ItemCount { get; set; }
    public int FavouriteCount { get; set; }
    public int Views7Days { get; set; }
    public int Views30Days { get; set; }
    public int ViewsTotal { get; set; }
    public int OpenReports { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
