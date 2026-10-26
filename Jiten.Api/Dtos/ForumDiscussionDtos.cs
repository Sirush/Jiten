namespace Jiten.Api.Dtos;

public enum ForumDiscussionStatus
{
    Ready = 0,
    Unavailable = 2,

    /// <summary>No topic yet; one is only created when a signed-in reader starts the discussion.</summary>
    NotStarted = 3
}

public record ForumPostDto
{
    public int PostId { get; init; }
    public string Username { get; init; } = "";
    public string AvatarUrl { get; init; } = "";
    public DateTime CreatedAt { get; init; }

    /// <summary>Sanitised by <see cref="Services.Forum.ForumPostSanitizer"/>, with every link absolute.</summary>
    public string Html { get; init; } = "";

    public string? ReplyToUsername { get; init; }
    public string Url { get; init; } = "";
}

public record ForumThreadDto
{
    public ForumPostDto Post { get; init; } = new();

    /// <summary>Every reply below <see cref="Post"/>, however deep, in posting order.</summary>
    public IReadOnlyList<ForumPostDto> Replies { get; init; } = [];
}

public record ForumDiscussionDto
{
    public static readonly ForumDiscussionDto Unavailable = new() { Status = ForumDiscussionStatus.Unavailable };
    public static readonly ForumDiscussionDto NotStarted = new() { Status = ForumDiscussionStatus.NotStarted };

    public ForumDiscussionStatus Status { get; init; }
    public int? TopicId { get; init; }
    public string? TopicUrl { get; init; }
    public int ReplyCount { get; init; }

    /// <summary>Latest activity first.</summary>
    public IReadOnlyList<ForumThreadDto> Threads { get; init; } = [];

    /// <summary>Cursor for the next page of threads; null once the last one is loaded.</summary>
    public int? NextBefore { get; init; }
}
