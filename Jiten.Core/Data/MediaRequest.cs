namespace Jiten.Core.Data;

public class MediaRequest
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public MediaType MediaType { get; set; }
    public string? ExternalUrl { get; set; }
    public LinkType? ExternalLinkType { get; set; }
    public string? Description { get; set; }
    public MediaRequestStatus Status { get; set; } = MediaRequestStatus.Open;
    public string? AdminNote { get; set; }
    public int? FulfilledDeckId { get; set; }
    public required string RequesterId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public int UpvoteCount { get; set; }
    public int BoostCount { get; set; }

    public Deck? FulfilledDeck { get; set; }
    public ICollection<MediaRequestUpvote> Upvotes { get; set; } = new List<MediaRequestUpvote>();
    public ICollection<MediaRequestBoost> Boosts { get; set; } = new List<MediaRequestBoost>();
    public ICollection<MediaRequestSubscription> Subscriptions { get; set; } = new List<MediaRequestSubscription>();
    public ICollection<MediaRequestComment> Comments { get; set; } = new List<MediaRequestComment>();
    public ICollection<MediaRequestUpload> Uploads { get; set; } = new List<MediaRequestUpload>();
}


