namespace Jiten.Core.Data.FSRS;

/// <summary>A card that was removed from the collection, with its schedule and packed review history.</summary>
public class FsrsCardArchive
{
    public long ArchiveId { get; set; }
    public string UserId { get; set; } = default!;
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }

    public DateTime ArchivedAt { get; set; }
    public CardArchiveReason Reason { get; set; }
    /// <summary>Sibling form that absorbed this one; set only for redundancy reasons.</summary>
    public byte? CoveringReadingIndex { get; set; }

    public FsrsState State { get; set; }
    public int? Step { get; set; }
    public double? Stability { get; set; }
    public double? Difficulty { get; set; }
    public DateTime Due { get; set; }
    public DateTime? LastReview { get; set; }
    public int Lapses { get; set; }
    public DateTime CardCreatedAt { get; set; }

    public int ReviewCount { get; set; }
    /// <summary>Base for every delta in <see cref="Logs"/>; null whenever <see cref="Logs"/> is.</summary>
    public DateTime? FirstReview { get; set; }

    /// <summary>
    /// Mirrors the truncated flag inside <see cref="Logs"/>, so listing removed cards does not have to read a
    /// blob per row to show it.
    /// </summary>
    public bool HistoryTruncated { get; set; }

    /// <summary>
    /// The history spans more than one card lifetime, so the schedule columns above — taken from the most
    /// recent removal — do not describe all of it. Restore replays such a row instead of trusting them.
    /// </summary>
    public bool HistoryMerged { get; set; }

    public byte[]? Logs { get; set; }
}
