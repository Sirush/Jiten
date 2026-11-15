using System.ComponentModel.DataAnnotations;

namespace Jiten.Core.Data.Jimaku;

/// <summary>
/// Per-file ledger under a source. A file is decided once, so a held or ignored file is never downloaded again.
/// </summary>
public class JimakuTrackedFile
{
    public const int MaxFileNameLength = 500;

    public int SourceDeckId { get; set; }

    [MaxLength(MaxFileNameLength)]
    public string FileName { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string Url { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTimeOffset? SourceModifiedAt { get; set; }

    /// <summary>Number read from the file name; orders the subdecks, never shown as the episode title</summary>
    public double? EpisodeNumber { get; set; }

    public JimakuFileStatus Status { get; set; }

    /// <summary>Machine-prefixed detail (duplicate: ..., gap: ..., over: ..., unnumbered: ..., archive: ..., error: ...)</summary>
    [MaxLength(500)]
    public string? HoldReason { get; set; }

    /// <summary>Null for an episode that was already in the deck when tracking started and could not be matched by name</summary>
    public int? ChildDeckId { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public JimakuSource Source { get; set; } = null!;
}
