namespace Jiten.Core.Data.CommunityLists;

/// <summary>A user-curated, publicly shareable ordered list of top-level decks.</summary>
public class CommunityList
{
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 1000;
    public const int MaxSlugLength = 120;

    public int ListId { get; set; }
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Cosmetic URL segment; lists always load by id, so it carries no uniqueness.</summary>
    public string Slug { get; set; } = "";

    public CommunityListStatus Status { get; set; }
    public int FavouriteCount { get; set; }

    /// <summary>Highest favourite milestone already announced to the owner; never lowered, so unfavouriting can't trigger a repeat.</summary>
    public int FavouriteMilestoneNotified { get; set; }

    public int ItemCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>First Draft to Public transition; "newest" sorts on it so a long-drafted list still surfaces when published.</summary>
    public DateTime? PublishedAt { get; set; }

    public List<CommunityListItem> Items { get; set; } = [];
    public List<CommunityListGenre> Genres { get; set; } = [];
    public List<CommunityListTag> Tags { get; set; } = [];
}

public enum CommunityListStatus
{
    Draft = 0,
    Public = 1,

    /// <summary>Taken down by moderation; distinct from Draft so the owner can't republish it.</summary>
    Unlisted = 2
}
