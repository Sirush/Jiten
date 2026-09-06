using System.ComponentModel.DataAnnotations;

namespace Jiten.Core.Data.YouTube;

/// <summary>
/// Per-video ledger under a source. Most videos are rejected, so every row records why to keep sweeps
/// from re-fetching known-bad videos.
/// </summary>
public class YouTubeVideo
{
    public int SourceDeckId { get; set; }

    [MaxLength(16)]
    public string VideoId { get; set; } = string.Empty;

    /// <summary>Set once imported</summary>
    public int? ChildDeckId { get; set; }

    public YouTubeVideoStatus Status { get; set; } = YouTubeVideoStatus.Pending;

    [MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    public DateTimeOffset? UploadedAt { get; set; }

    public int? RuntimeSeconds { get; set; }

    /// <summary>yt-dlp playable_in_embed; false degrades watch mode to a ?t= link</summary>
    public bool PlayableInEmbed { get; set; } = true;

    /// <summary>Machine-prefixed rejection detail (asr-only, no-ja-track, density: ..., title-filter: ...)</summary>
    [MaxLength(500)]
    public string? SkipReason { get; set; }

    public DateTimeOffset LastCheckedAt { get; set; }

    public YouTubeSource Source { get; set; } = null!;
}
