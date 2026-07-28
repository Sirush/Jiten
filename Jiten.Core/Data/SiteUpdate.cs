namespace Jiten.Core.Data;

public class SiteUpdate
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public required string BodyMarkdown { get; set; }

    /// <summary>Body of the notification sent on publish; falls back to a generic line when empty.</summary>
    public string? NotificationTeaser { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    /// <summary>Null means draft. Stamped once, on the first publish.</summary>
    public DateTime? PublishedAt { get; set; }

    /// <summary>Tracked separately from <see cref="PublishedAt"/> so no retry or republish can notify everyone twice.</summary>
    public DateTime? NotifiedAt { get; set; }
}
