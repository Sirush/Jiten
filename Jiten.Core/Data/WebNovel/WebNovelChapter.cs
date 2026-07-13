using System.ComponentModel.DataAnnotations;

namespace Jiten.Core.Data.WebNovel;

/// <summary>
/// Per-episode ledger. Subdeck ranges, revision detection and child identity all derive from this,
/// never from deck titles — subdeck titles change as the open subdeck grows.
/// </summary>
public class WebNovelChapter
{
    /// <summary>
    /// Parent deck (the tracked novel)
    /// </summary>
    public int DeckId { get; set; }

    /// <summary>
    /// 1-based episode index at the source
    /// </summary>
    public int EpisodeNumber { get; set; }

    /// <summary>
    /// Subdeck this episode's text lives in
    /// </summary>
    public int ChildDeckId { get; set; }

    [MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Latest publish/revision (改稿) timestamp from the table of contents
    /// </summary>
    public DateTimeOffset? SourceUpdatedAt { get; set; }

    public int CharCount { get; set; }

    public WebNovelSource Source { get; set; } = null!;
}
