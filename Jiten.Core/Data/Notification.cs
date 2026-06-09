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
}

public enum NotificationType
{
    RequestStatusChanged = 1,
    RequestCompleted = 2,
    RequestFileUploaded = 3,
    RequestAdminComment = 4,
    General = 10
}
