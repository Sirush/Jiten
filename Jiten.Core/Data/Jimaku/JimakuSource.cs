using System.ComponentModel.DataAnnotations;

namespace Jiten.Core.Data.Jimaku;

/// <summary>
/// An anime or drama deck that follows one Jimaku entry and ingests each matching subtitle file as an episode subdeck.
/// </summary>
public class JimakuSource
{
    public int DeckId { get; set; }

    public Deck Deck { get; set; } = null!;

    public int JimakuEntryId { get; set; }

    /// <summary>Case-insensitive whole-name match where each run of * stands for any text</summary>
    [MaxLength(500)]
    public string FilePattern { get; set; } = string.Empty;

    public int? AnilistId { get; set; }

    /// <summary>Jimaku encoding, e.g. tv:1234</summary>
    [MaxLength(32)]
    public string? TmdbId { get; set; }

    /// <summary>TMDB counts episodes per season; null takes the only season, or the whole show when there are several</summary>
    public int? TmdbSeason { get; set; }

    /// <summary>Total episodes reported by AniList or TMDB at the last check</summary>
    public int? ExpectedEpisodes { get; set; }

    /// <summary>Admin correction that wins over <see cref="ExpectedEpisodes"/></summary>
    public int? ExpectedEpisodesOverride { get; set; }

    public int? AiredEpisodes { get; set; }

    public bool FinishedAiring { get; set; }

    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>Last check that ingested at least one episode</summary>
    public DateTimeOffset? LastSyncedAt { get; set; }

    public DateTimeOffset NextCheckAt { get; set; }

    public bool SyncEnabled { get; set; } = true;

    /// <summary>Set when tracking stopped by itself because every expected episode was ingested</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    public int ConsecutiveFailures { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }

    public List<JimakuTrackedFile> Files { get; set; } = [];

    public int? EffectiveExpectedEpisodes => ExpectedEpisodesOverride ?? ExpectedEpisodes;
}
