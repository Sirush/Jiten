using Jiten.Core.Data.WebNovel;

namespace Jiten.Core.WebNovel;

public sealed record WebNovelWorkRef(WebNovelProvider Provider, string SourceId);

public interface IWebNovelSourceResolver
{
    IWebNovelSource Resolve(WebNovelProvider provider);
    bool IsSupported(WebNovelProvider provider);

    /// <summary>
    /// The work a URL or bare id names, looking indirect pages up at the source. Null when the input isn't a
    /// webnovel link at all.
    /// </summary>
    /// <exception cref="NotSupportedException">The URL's provider is not enabled.</exception>
    /// <exception cref="InvalidOperationException">An indirect page links to no work, or to several.</exception>
    Task<WebNovelWorkRef?> ResolveUrlAsync(string input, CancellationToken ct = default);
}

public class WebNovelSourceResolver(IEnumerable<IWebNovelSource> sources) : IWebNovelSourceResolver
{
    private readonly Dictionary<WebNovelProvider, IWebNovelSource> _sources =
        sources.ToDictionary(s => s.Provider);

    public IWebNovelSource Resolve(WebNovelProvider provider) =>
        _sources.TryGetValue(provider, out var source)
            ? source
            : throw new NotSupportedException($"Webnovel provider {provider} is not enabled.");

    public bool IsSupported(WebNovelProvider provider) => _sources.ContainsKey(provider);

    public async Task<WebNovelWorkRef?> ResolveUrlAsync(string input, CancellationToken ct = default)
    {
        if (WebNovelUrlParser.TryParse(input, out var provider, out var sourceId))
            return new WebNovelWorkRef(provider, sourceId);

        if (!WebNovelUrlParser.TryMatchIndirect(input, out provider))
            return null;

        if (Resolve(provider) is not IWebNovelLinkResolver linkResolver)
            throw new NotSupportedException($"{provider} cannot look up indirect links.");

        var workId = await linkResolver.ResolveWorkIdAsync(new Uri(input.Trim()), ct);
        return new WebNovelWorkRef(provider, workId);
    }
}
