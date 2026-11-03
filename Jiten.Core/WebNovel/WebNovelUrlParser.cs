using Jiten.Core.Data;
using Jiten.Core.Data.WebNovel;

namespace Jiten.Core.WebNovel;

public static class WebNovelUrlParser
{
    /// <summary>
    /// Accepts a work or episode URL, or a bare work id where the provider has one (a Syosetu ncode).
    /// Returns false when the input isn't a supported source.
    /// </summary>
    public static bool TryParse(string input, out WebNovelProvider provider, out string sourceId)
    {
        provider = default;
        sourceId = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        input = input.Trim();

        foreach (var descriptor in WebNovelProviders.All)
        {
            if (descriptor.ParseBareId?.Invoke(input) is { } bareId)
            {
                provider = descriptor.Provider;
                sourceId = bareId;
                return true;
            }
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || !WebNovelProviders.TryMatchHost(uri, out var match))
            return false;

        if (match.ParseWorkUrl(uri) is not { } id)
            return false;

        provider = match.Provider;
        sourceId = id;
        return true;
    }

    /// <summary>
    /// A URL that names a work only after the provider looks it up, such as a Kakuyomu book page.
    /// </summary>
    public static bool TryMatchIndirect(string input, out WebNovelProvider provider)
    {
        provider = default;

        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri) ||
            !WebNovelProviders.TryMatchHost(uri, out var match) ||
            match.IsIndirectWorkUrl?.Invoke(uri) != true)
            return false;

        provider = match.Provider;
        return true;
    }

    /// <summary>
    /// Full URLs only, direct or indirect: a bare ncode is an id, not a link.
    /// </summary>
    public static bool IsWebNovelLink(string? url, out WebNovelProvider provider)
    {
        provider = default;

        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out _))
            return false;

        return TryParse(url!, out provider, out _) || TryMatchIndirect(url!, out provider);
    }

    /// <summary>
    /// The first of a deck's links that names a webnovel work directly
    /// </summary>
    public static WebNovelWorkRef? FirstWorkLink(IEnumerable<Link> links)
    {
        foreach (var link in links.Where(l => WebNovelProviders.IsWebNovelLinkType(l.LinkType)))
        {
            if (TryParse(link.Url, out var provider, out var sourceId))
                return new WebNovelWorkRef(provider, sourceId);
        }

        return null;
    }

    public static string BuildWorkUrl(WebNovelProvider provider, string sourceId) =>
        WebNovelProviders.Get(provider).BuildWorkUrl(sourceId);
}
