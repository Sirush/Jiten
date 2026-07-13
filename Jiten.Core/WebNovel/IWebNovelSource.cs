using Jiten.Core.Data.WebNovel;

namespace Jiten.Core.WebNovel;

/// <summary>
/// A webnovel site. Implementations are responsible for their own politeness throttling — callers may
/// fetch in a tight loop, so the rate limit must hold inside the source, not at the call site.
/// </summary>
public interface IWebNovelSource
{
    WebNovelProvider Provider { get; }

    Task<WebNovelInfo> GetInfoAsync(string sourceId, CancellationToken ct = default);

    /// <summary>
    /// Full table of contents, ordered by episode number. One-shots return a single entry.
    /// </summary>
    Task<List<WebNovelEpisodeRef>> GetTocAsync(string sourceId, CancellationToken ct = default);

    /// <summary>
    /// Episode body, ruby inlined as {base'reading}.
    /// </summary>
    Task<string> GetEpisodeTextAsync(string sourceId, WebNovelEpisodeRef episode, CancellationToken ct = default);
}

/// <summary>
/// A source whose API can report the update state of many works at once. The sweeper uses this to poll
/// every tracked novel in a couple of requests instead of one table-of-contents fetch each.
/// </summary>
public interface IBatchPollableSource
{
    Task<Dictionary<string, WebNovelInfo>> BatchPollAsync(IEnumerable<string> sourceIds, CancellationToken ct = default);
}
