using Jiten.Core.Data.WebNovel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jiten.Core.WebNovel;

/// <summary>
/// カクヨム. No public API: metadata and the full table of contents come from one work page, episode text from
/// the episode pages.
/// </summary>
public class KakuyomuSource : IWebNovelSource, IWebNovelLinkResolver
{
    /// <summary>
    /// A sync reads the info and then the table of contents, both from the same work page; caching the parse
    /// halves its requests
    /// </summary>
    private static readonly TimeSpan WorkPageCacheLifetime = TimeSpan.FromMinutes(5);

    private readonly WebNovelHttp _http;
    private readonly MemoryCache _workPages = new(new MemoryCacheOptions { SizeLimit = 64 });

    public WebNovelProvider Provider => WebNovelProvider.Kakuyomu;

    public KakuyomuSource(IHttpClientFactory httpClientFactory, ILogger<KakuyomuSource> logger)
        : this(httpClientFactory, logger, WebNovelThrottle.For(WebNovelProviders.Kakuyomu))
    {
    }

    internal KakuyomuSource(IHttpClientFactory httpClientFactory, ILogger<KakuyomuSource> logger, WebNovelThrottle throttle)
    {
        _http = new WebNovelHttp(httpClientFactory, WebNovelProviders.Kakuyomu.HttpClientName, throttle, logger);
    }

    public async Task<WebNovelInfo> GetInfoAsync(string sourceId, CancellationToken ct = default) =>
        (await GetWorkPageAsync(sourceId, ct)).Info;

    /// <exception cref="InvalidOperationException">
    /// The page lists fewer episodes than the work reports: ingesting it would take a partial work and later append
    /// the missing episodes out of order.
    /// </exception>
    public async Task<List<WebNovelEpisodeRef>> GetTocAsync(string sourceId, CancellationToken ct = default)
    {
        var page = await GetWorkPageAsync(sourceId, ct);

        if (page.Toc.Count < page.Info.EpisodeCount)
        {
            throw new InvalidOperationException(
                $"Kakuyomu work {sourceId} lists {page.Toc.Count} episodes but reports {page.Info.EpisodeCount} (markup may have changed).");
        }

        return page.Toc;
    }

    public async Task<string> GetEpisodeTextAsync(string sourceId, WebNovelEpisodeRef episode, CancellationToken ct = default)
    {
        var url = $"{WebNovelUrlParser.BuildWorkUrl(Provider, sourceId)}/episodes/{episode.SourceEpisodeId}";
        var html = await _http.GetStringAsync(url, ct);
        return KakuyomuPageParser.ParseEpisodeText(html, url);
    }

    public async Task<string> ResolveWorkIdAsync(Uri url, CancellationToken ct = default)
    {
        var html = await _http.GetStringAsync(url.GetLeftPart(UriPartial.Path), ct);
        var workIds = KakuyomuPageParser.ParseLinkedWorkIds(html);

        return workIds.Count switch
        {
            1 => workIds[0],
            0 => throw new InvalidOperationException("This Kakuyomu book page doesn't link to a web version."),
            _ => throw new InvalidOperationException(
                $"This Kakuyomu book page links to {workIds.Count} works. Paste the URL of the work itself.")
        };
    }

    private async Task<KakuyomuWorkPage> GetWorkPageAsync(string workId, CancellationToken ct) =>
        (await _workPages.GetOrCreateAsync(workId, async entry =>
        {
            entry.Size = 1;
            entry.AbsoluteExpirationRelativeToNow = WorkPageCacheLifetime;

            var html = await _http.GetStringAsync(WebNovelUrlParser.BuildWorkUrl(Provider, workId), ct);
            return KakuyomuPageParser.ParseWorkPage(html, workId);
        }))!;
}
