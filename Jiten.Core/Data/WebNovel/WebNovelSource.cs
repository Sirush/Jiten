using System.ComponentModel.DataAnnotations;

namespace Jiten.Core.Data.WebNovel;

/// <summary>
/// One tracked webnovel. Keyed on the parent deck; chapter-range subdecks hang off that deck.
/// </summary>
public class WebNovelSource
{
    /// <summary>
    /// Parent deck holding the chapter-range subdecks
    /// </summary>
    public int DeckId { get; set; }

    public Deck Deck { get; set; } = null!;

    public WebNovelProvider Provider { get; set; }

    /// <summary>
    /// Provider-specific work id (Syosetu ncode, lowercase)
    /// </summary>
    [MaxLength(64)]
    public string SourceId { get; set; } = string.Empty;

    /// <summary>
    /// Number of episodes ingested so far
    /// </summary>
    public int LastEpisodeCount { get; set; }

    /// <summary>
    /// Latest episode timestamp reported by the source at the last sync (Narou general_lastup)
    /// </summary>
    public DateTimeOffset? LastSourceUpdate { get; set; }

    public DateTimeOffset? LastSyncedAt { get; set; }

    /// <summary>
    /// Staggered next poll; also used to back off after failures
    /// </summary>
    public DateTimeOffset NextCheckAt { get; set; }

    public bool SyncEnabled { get; set; } = true;

    /// <summary>
    /// Finished at the source (Narou end=0) — polled monthly instead of weekly
    /// </summary>
    public bool CompletedAtSource { get; set; }

    /// <summary>
    /// Serialisation stopped at the source (Narou isstop)
    /// </summary>
    public bool OnHiatusAtSource { get; set; }

    public int ConsecutiveFailures { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }

    /// <summary>
    /// Per-novel override of the subdeck character budget; null uses the global default
    /// </summary>
    public int? ChunkCharBudget { get; set; }

    /// <summary>
    /// Episodes revised at the source inside already-closed subdecks, awaiting a manual refresh
    /// </summary>
    public int PendingRevisionCount { get; set; }

    public List<WebNovelChapter> Chapters { get; set; } = new();
}
