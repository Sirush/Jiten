using Jiten.Core.Data.YouTube;

namespace Jiten.Core.YouTube;

/// <summary>
/// A channel or playlist resolved to its canonical id.
/// </summary>
public class YouTubeSourceInfo
{
    public YouTubeSourceKind Kind { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ChannelName { get; set; } = string.Empty;
    public string? ChannelId { get; set; }
    public string? Description { get; set; }
    public string? CoverUrl { get; set; }

    /// <summary>Upload date of the oldest listed video; stands in for the channel's creation date</summary>
    public DateTimeOffset? OldestUploadAt { get; set; }

    public List<YouTubeVideoListing> Videos { get; set; } = new();
}

/// <summary>
/// Admin overrides at add time. The source only reports one title, so romaji and English are typed in.
/// </summary>
public class YouTubeSourceTitles
{
    public string? OriginalTitle { get; set; }
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }

    /// <summary>Parent release date; null falls back to the oldest listed upload, then the oldest imported video</summary>
    public DateOnly? ReleaseDate { get; set; }
}

/// <summary>
/// One entry from a flat listing. Listings carry no upload date, so <see cref="Position"/> (0 = newest for
/// channels, playlist order for playlists) is the only ordering until the video is fetched.
/// </summary>
public record YouTubeVideoListing(string VideoId, string Title, int? DurationSeconds, int Position);

public class YouTubeVideoInfo
{
    public string VideoId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? DurationSeconds { get; set; }
    public DateTimeOffset? UploadedAt { get; set; }
    public bool PlayableInEmbed { get; set; } = true;
    public string? Availability { get; set; }
    public string? LiveStatus { get; set; }
    public bool IsLive { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? ChannelId { get; set; }
    public string? ChannelName { get; set; }
    public List<string> ManualSubtitleLanguages { get; set; } = new();
    public List<string> AutomaticCaptionLanguages { get; set; } = new();

    /// <summary>Downloaded Japanese track, null when yt-dlp wrote none</summary>
    public string? SubtitlePath { get; set; }
}

/// <summary>
/// Outcome of one video fetch: either info, or a skip reason in the ledger's machine-prefixed form.
/// </summary>
public class YouTubeFetchResult
{
    public YouTubeVideoInfo? Info { get; set; }
    public string? SkipReason { get; set; }
    public YouTubeVideoStatus Status { get; set; }

    /// <summary>yt-dlp failed on this video without a classifiable cause; the video stays Pending</summary>
    public string? Error { get; set; }

    public bool Succeeded => Info != null && SkipReason == null && Error == null;

    public static YouTubeFetchResult Skip(YouTubeVideoStatus status, string reason, YouTubeVideoInfo? info = null) =>
        new() { Status = status, SkipReason = reason, Info = info };

    public static YouTubeFetchResult Failed(string error) =>
        new() { Status = YouTubeVideoStatus.Pending, Error = error };
}

public class YouTubeBatchFetchResult
{
    public Dictionary<string, YouTubeFetchResult> Results { get; } = new();

    /// <summary>Set when the egress IP was bot-checked partway; results before it are still valid</summary>
    public string? BlockedMessage { get; set; }
}
