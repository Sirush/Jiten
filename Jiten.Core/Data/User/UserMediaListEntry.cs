namespace Jiten.Core.Data;

public enum MediaListEntryState
{
    InProgress = 0,
    Completed = 1,
    Dropped = 2
}

/// <summary>One pass through a title on the user's media list; reading it again adds another entry.</summary>
public class UserMediaListEntry
{
    public long Id { get; set; }
    public string UserId { get; set; } = null!;
    public int DeckId { get; set; }
    public MediaListEntryState State { get; set; }

    /// <summary>Calendar dates as the user sees them; null means unknown.</summary>
    public DateOnly? StartedOn { get; set; }

    /// <summary>Completion date for a completed entry, the date it was abandoned for a dropped one.</summary>
    public DateOnly? FinishedOn { get; set; }

    /// <summary>User-entered count. Null on a completed entry means the whole deck, and means unknown on an unfinished one.</summary>
    public int? CharactersRead { get; set; }

    /// <summary>On a volume or episode, the pass through its series this entry belongs to; null for a first read or one outside a tracked series pass.</summary>
    public long? SeriesEntryId { get; set; }

    public UserMediaListEntry? SeriesEntry { get; set; }

    /// <summary>Total (mature plus young) coverage in percent at completion; only captured when the entry is completed on the day.</summary>
    public float? CoverageAtFinish { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Last write to the entry; an in-progress entry left untouched for a month is offered for an update on the home page.</summary>
    public DateTime UpdatedAt { get; set; }
}
