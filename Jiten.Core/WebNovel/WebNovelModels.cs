using Jiten.Core.Data.WebNovel;

namespace Jiten.Core.WebNovel;

/// <summary>
/// Work-level metadata, from the provider's API where one exists.
/// </summary>
public class WebNovelInfo
{
    public WebNovelProvider Provider { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string? Synopsis { get; set; }

    /// <summary>
    /// Provider genre label (e.g. ハイファンタジー〔ファンタジー〕), mapped via ExternalGenreMappings
    /// </summary>
    public string? Genre { get; set; }

    public List<string> Keywords { get; set; } = new();

    public DateTimeOffset? FirstPublishedAt { get; set; }
    public DateTimeOffset? LastUpdatedAt { get; set; }

    public int EpisodeCount { get; set; }
    public long TotalCharacters { get; set; }

    /// <summary>
    /// 短編 — the work is a single page and has no table of contents
    /// </summary>
    public bool IsOneShot { get; set; }

    public bool IsCompleted { get; set; }
    public bool IsOnHiatus { get; set; }
    public bool IsAdultOnly { get; set; }
    public bool IsR15 { get; set; }
}

/// <summary>
/// Titles chosen by the admin at add time. A source only reports the Japanese title, so romaji and English
/// are typed in (or auto-romanised) on the add page and carried into the import.
/// </summary>
public class WebNovelTitles
{
    public string? OriginalTitle { get; set; }
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }
}

/// <summary>
/// One entry in the table of contents.
/// </summary>
public class WebNovelEpisodeRef
{
    /// <summary>
    /// 1-based index at the source
    /// </summary>
    public int Number { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Latest publish or revision (改稿) timestamp shown in the table of contents
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// Enclosing 章 heading, when the work uses them
    /// </summary>
    public string? SectionTitle { get; set; }

    /// <summary>
    /// The episode is a 短編's single page: its body lives on the work page itself, /{n}/ does not exist
    /// </summary>
    public bool IsOneShot { get; set; }
}
