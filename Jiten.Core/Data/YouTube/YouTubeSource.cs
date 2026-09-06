using System.ComponentModel.DataAnnotations;

namespace Jiten.Core.Data.YouTube;

/// <summary>
/// One tracked channel or playlist. Keyed on the parent deck; every imported video is a child subdeck.
/// </summary>
public class YouTubeSource
{
    public int DeckId { get; set; }

    public Deck Deck { get; set; } = null!;

    public YouTubeSourceKind SourceKind { get; set; }

    /// <summary>Canonical id (UC... or PL...), never a handle</summary>
    [MaxLength(64)]
    public string SourceId { get; set; } = string.Empty;

    [MaxLength(200)]
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>Owning channel, also for playlists; drives the RSS feed and the channel link</summary>
    [MaxLength(64)]
    public string? ChannelId { get; set; }

    /// <summary>Regex; when set only matching video titles are ingested</summary>
    [MaxLength(500)]
    public string? TitleFilterInclude { get; set; }

    /// <summary>Regex; matching video titles are skipped</summary>
    [MaxLength(500)]
    public string? TitleFilterExclude { get; set; }

    /// <summary>Videos shorter than this are skipped (mixed short and long-form channels)</summary>
    public int? MinRuntimeSeconds { get; set; }

    public int? MaxRuntimeSeconds { get; set; }

    /// <summary>Newest video upload seen at the last check</summary>
    public DateTimeOffset? LastSourceUpdate { get; set; }

    /// <summary>Last fetch drain completed, not the last feed check</summary>
    public DateTimeOffset? LastSyncedAt { get; set; }

    /// <summary>Staggered next feed poll; also used to back off after failures</summary>
    public DateTimeOffset NextCheckAt { get; set; }

    public bool SyncEnabled { get; set; } = true;

    /// <summary>Days between feed checks; null = weekly, monthly once the source has been quiet for 90 days</summary>
    public int? CheckIntervalDays { get; set; }

    public int ConsecutiveFailures { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }

    public List<YouTubeVideo> Videos { get; set; } = new();
}
