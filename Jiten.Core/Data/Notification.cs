namespace Jiten.Core.Data;

public class Notification
{
    public int Id { get; set; }
    public required string UserId { get; set; }
    public NotificationType Type { get; set; }
    public required string Title { get; set; }
    public required string Message { get; set; }
    public string? LinkUrl { get; set; }
    public bool IsRead { get; set; }
    public DateTime? ReadAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Unread notifications sharing a key fold into one row; null never folds.</summary>
    public string? GroupKey { get; set; }

    public int GroupCount { get; set; } = 1;
}

public enum NotificationType
{
    RequestStatusChanged = 1,
    RequestCompleted = 2,
    RequestFileUploaded = 3,
    RequestAdminComment = 4,
    General = 10,
    SiteUpdate = 11,
    CommunityListMilestone = 12,
    MetadataEditApproved = 30,
    MetadataEditRejected = 31,
    MetadataEditReverted = 32
}

/// <summary>Mirrors NotificationGroup in Jiten.Web/app/types/enums.ts.</summary>
public enum NotificationGroup
{
    FromJiten = 0,
    MediaRequests = 1,
    MetadataEdits = 2,
    CommunityLists = 3
}

public static class NotificationGroups
{
    /// <summary>Sent on purpose by a person at Jiten, so pinned first in the bell and never muted; mirrors FROM_JITEN_TYPES in notificationType.ts.</summary>
    public static readonly NotificationType[] FromJitenTypes =
        [NotificationType.General, NotificationType.SiteUpdate, NotificationType.RequestAdminComment];

    public static readonly NotificationGroup[] Mutable =
        [NotificationGroup.MediaRequests, NotificationGroup.MetadataEdits, NotificationGroup.CommunityLists];

    public static NotificationGroup Of(NotificationType type) => type switch
    {
        NotificationType.RequestStatusChanged or NotificationType.RequestCompleted or NotificationType.RequestFileUploaded
            => NotificationGroup.MediaRequests,
        NotificationType.MetadataEditApproved or NotificationType.MetadataEditRejected or NotificationType.MetadataEditReverted
            => NotificationGroup.MetadataEdits,
        NotificationType.CommunityListMilestone => NotificationGroup.CommunityLists,
        NotificationType.General or NotificationType.SiteUpdate or NotificationType.RequestAdminComment => NotificationGroup.FromJiten,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Every notification type needs a group.")
    };

    public static bool IsMutable(NotificationType type) => Of(type) != NotificationGroup.FromJiten;
}
