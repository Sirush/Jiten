using System.ComponentModel.DataAnnotations;

namespace Jiten.Core.Data.YouTube;

/// <summary>
/// A source the admin asked for on the dashboard but that the server cannot list itself (bot-checked
/// egress). The home CLI resolves it with its own yt-dlp and completes it through the ingest API.
/// </summary>
public class YouTubeRegistration
{
    public int Id { get; set; }

    [MaxLength(500)]
    public string Url { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? OriginalTitle { get; set; }

    [MaxLength(500)]
    public string? RomajiTitle { get; set; }

    [MaxLength(500)]
    public string? EnglishTitle { get; set; }

    public DateOnly? ReleaseDate { get; set; }

    /// <summary>Cover uploaded on the dashboard, kept on disk until completion; null = channel avatar</summary>
    [MaxLength(1000)]
    public string? CoverPath { get; set; }

    [MaxLength(500)]
    public string? TitleFilterInclude { get; set; }

    [MaxLength(500)]
    public string? TitleFilterExclude { get; set; }

    public int? MinRuntimeSeconds { get; set; }

    public int? MaxRuntimeSeconds { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Parent deck once completed</summary>
    public int? DeckId { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }
}
