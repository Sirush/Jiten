namespace Jiten.Core.Data;

public class RequestActivityLog
{
    public long Id { get; set; }
    public int? MediaRequestId { get; set; }
    public required string UserId { get; set; }
    public RequestAction Action { get; set; }
    public string? TargetUserId { get; set; }
    public string? Detail { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum RequestAction
{
    RequestCreated = 1,
    RequestDeleted = 2,

    Upvoted = 10,
    UpvoteRemoved = 11,

    Subscribed = 12,
    Unsubscribed = 13,

    CommentAdded = 20,
    CommentEdited = 21,
    AdminCommentAdded = 22,

    FileUploaded = 30,
    FileDeletedByAdmin = 31,

    StatusChangedToInProgress = 40,
    StatusChangedToCompleted = 41,
    StatusChangedToRejected = 42,
    StatusChangedToOpen = 43,

    RequestEditedByAdmin = 50,
    RequestEditedByRequester = 51,

    ContributionValidated = 60,
    ContributionRevoked = 61,
}
