namespace Jiten.Core.Data;

public class MediaRequest
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public MediaRequestKind Kind { get; set; } = MediaRequestKind.New;
    public MediaType MediaType { get; set; }
    public string? ExternalUrl { get; set; }
    public LinkType? ExternalLinkType { get; set; }
    public string? Description { get; set; }
    public MediaRequestStatus Status { get; set; } = MediaRequestStatus.Open;
    public string? AdminNote { get; set; }

    /// <summary>Deck an Update request is filed against. Null on New requests, or once the deck is deleted.</summary>
    public int? TargetDeckId { get; set; }

    public int? FulfilledDeckId { get; set; }
    public required string RequesterId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public int UpvoteCount { get; set; }
    public int BoostCount { get; set; }

    /// <summary>Titles, date, genres and tags read from <see cref="ExternalUrl"/>; <see cref="Title"/> stays as the requester wrote it.</summary>
    public string? OriginalTitle { get; set; }
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }
    public DateOnly? ReleaseDate { get; set; }
    public MediaRequestMetadataStatus MetadataStatus { get; set; }
    public DateTime? MetadataFetchedAt { get; set; }

    /// <summary>An older active request whose titles or links match this one's; set by the post-lookup check, never merged automatically.</summary>
    public int? PossibleDuplicateOfRequestId { get; set; }
    /// <summary>A deck whose titles or links match this request's.</summary>
    public int? PossibleDuplicateDeckId { get; set; }
    /// <summary>An admin ruled the flags false; later lookups of the same link leave them unset.</summary>
    public bool DuplicateFlagDismissed { get; set; }

    public Deck? TargetDeck { get; set; }
    public Deck? FulfilledDeck { get; set; }
    public ICollection<MediaRequestUpvote> Upvotes { get; set; } = new List<MediaRequestUpvote>();
    public ICollection<MediaRequestBoost> Boosts { get; set; } = new List<MediaRequestBoost>();
    public ICollection<MediaRequestSubscription> Subscriptions { get; set; } = new List<MediaRequestSubscription>();
    public ICollection<MediaRequestComment> Comments { get; set; } = new List<MediaRequestComment>();
    public ICollection<MediaRequestUpload> Uploads { get; set; } = new List<MediaRequestUpload>();
    public ICollection<MediaRequestGenre> Genres { get; set; } = new List<MediaRequestGenre>();
    public ICollection<MediaRequestTag> Tags { get; set; } = new List<MediaRequestTag>();
    public ICollection<MediaRequestTitle> Titles { get; set; } = new List<MediaRequestTitle>();
    public ICollection<MediaRequestLink> Links { get; set; } = new List<MediaRequestLink>();
    public MediaRequest? PossibleDuplicateOfRequest { get; set; }
    public Deck? PossibleDuplicateDeck { get; set; }
}


