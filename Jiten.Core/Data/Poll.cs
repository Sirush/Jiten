namespace Jiten.Core.Data;

public class Poll
{
    public int Id { get; set; }
    public required string Question { get; set; }
    public string? DescriptionMarkdown { get; set; }

    public int MaxSelections { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    /// <summary>Null means draft. Stamped once, on the first publish.</summary>
    public DateTime? PublishedAt { get; set; }

    public DateTime? ClosesAt { get; set; }

    /// <summary>Manual close stamp; cleared again by an admin reopen.</summary>
    public DateTime? ClosedAt { get; set; }

    public List<PollOption> Options { get; set; } = [];

    public bool IsClosed(DateTime now) => ClosedAt != null || (ClosesAt != null && ClosesAt <= now);
}
