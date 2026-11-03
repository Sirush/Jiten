using System.Text.RegularExpressions;
using Jiten.Core.Data;
using Jiten.Core.Data.WebNovel;

namespace Jiten.Core.WebNovel;

/// <param name="PauseEvery">An extra <paramref name="Pause"/> after every N requests; 0 disables it</param>
public sealed record WebNovelPoliteness(TimeSpan RequestInterval, int PauseEvery, TimeSpan Pause)
{
    public double EstimateSeconds(int requests) =>
        requests * RequestInterval.TotalSeconds +
        (PauseEvery > 0 ? requests / PauseEvery * Pause.TotalSeconds : 0);
}

/// <summary>
/// A host serving one or more providers. Requests to a site are throttled and queued together, whichever provider
/// they come from.
/// </summary>
/// <param name="Key">Lowercase; names the site's Hangfire queues, throttle and HttpClient</param>
public sealed record WebNovelSite(string Key, WebNovelPoliteness Politeness)
{
    public string HttpClientName => $"webnovel-{Key}";
}

public sealed class WebNovelProviderDescriptor
{
    public required WebNovelProvider Provider { get; init; }
    public required string DisplayName { get; init; }
    public required WebNovelSite Site { get; init; }

    /// <summary>
    /// Link type of the work page; also keys the provider's genre and tag mappings
    /// </summary>
    public required LinkType LinkType { get; init; }

    public required string[] Hosts { get; init; }

    /// <summary>
    /// Episode ids survive insertions, deletions and reordering at the source. Positional ids (Syosetu's /{n}/)
    /// shift when an earlier episode is deleted, so a shrinking table of contents has to stop the sync.
    /// </summary>
    public required bool StableEpisodeIds { get; init; }

    public required Func<string, string> BuildWorkUrl { get; init; }

    /// <summary>
    /// Work id from a work or episode URL on one of <see cref="Hosts"/>, or null
    /// </summary>
    public required Func<Uri, string?> ParseWorkUrl { get; init; }

    /// <summary>
    /// Work id typed on its own, without a URL
    /// </summary>
    public Func<string, string?>? ParseBareId { get; init; }

    /// <summary>
    /// URLs that name a work only after a lookup (a book page linking to its web version); the provider's source
    /// must implement <see cref="IWebNovelLinkResolver"/> when this is set
    /// </summary>
    public Func<Uri, bool>? IsIndirectWorkUrl { get; init; }

    public bool MatchesHost(Uri uri) => Hosts.Contains(uri.Host.ToLowerInvariant());
}

public static partial class WebNovelProviders
{
    /// <summary>
    /// narou.rb's shipped defaults: 0.7s between requests and a 5s pause every 10
    /// </summary>
    public static readonly WebNovelSite Syosetu =
        new("syosetu", new WebNovelPoliteness(TimeSpan.FromMilliseconds(700), 10, TimeSpan.FromSeconds(5)));

    /// <summary>
    /// kakuyomu.jp robots.txt asks crawlers for one request per second
    /// </summary>
    public static readonly WebNovelSite Kakuyomu =
        new("kakuyomu", new WebNovelPoliteness(TimeSpan.FromSeconds(1), 0, TimeSpan.Zero));

    public static IReadOnlyList<WebNovelProviderDescriptor> All { get; } =
    [
        new()
        {
            Provider = WebNovelProvider.Syosetu,
            DisplayName = "Syosetu",
            Site = Syosetu,
            LinkType = LinkType.Syosetsu,
            Hosts = ["ncode.syosetu.com"],
            StableEpisodeIds = false,
            BuildWorkUrl = id => $"https://ncode.syosetu.com/{id}/",
            ParseWorkUrl = ParseNcodeUrl,
            ParseBareId = ParseNcode
        },
        new()
        {
            Provider = WebNovelProvider.SyosetuNovel18,
            DisplayName = "Syosetu (R18)",
            Site = Syosetu,
            LinkType = LinkType.Syosetsu,
            Hosts = ["novel18.syosetu.com"],
            StableEpisodeIds = false,
            BuildWorkUrl = id => $"https://novel18.syosetu.com/{id}/",
            ParseWorkUrl = ParseNcodeUrl
        },
        new()
        {
            Provider = WebNovelProvider.Kakuyomu,
            DisplayName = "Kakuyomu",
            Site = Kakuyomu,
            LinkType = LinkType.Kakuyomu,
            Hosts = ["kakuyomu.jp"],
            StableEpisodeIds = true,
            BuildWorkUrl = id => $"https://kakuyomu.jp/works/{id}",
            ParseWorkUrl = ParseKakuyomuWorkUrl,
            IsIndirectWorkUrl = IsKakuyomuPublicationUrl
        }
    ];

    public static IEnumerable<WebNovelSite> Sites => All.Select(d => d.Site).Distinct();

    public static WebNovelProviderDescriptor Get(WebNovelProvider provider) =>
        TryGet(provider, out var descriptor)
            ? descriptor
            : throw new NotSupportedException($"Webnovel provider {provider} has no descriptor.");

    public static bool TryGet(WebNovelProvider provider, out WebNovelProviderDescriptor descriptor)
    {
        descriptor = All.FirstOrDefault(d => d.Provider == provider)!;
        return descriptor != null;
    }

    public static bool TryMatchHost(Uri uri, out WebNovelProviderDescriptor descriptor)
    {
        descriptor = All.FirstOrDefault(d => d.MatchesHost(uri))!;
        return descriptor != null;
    }

    public static bool IsWebNovelLinkType(LinkType linkType) => All.Any(d => d.LinkType == linkType);

    [GeneratedRegex(@"^[nN]\d{4}[a-zA-Z]{1,2}$")]
    private static partial Regex NcodePattern();

    [GeneratedRegex(@"^\d{10,25}$")]
    private static partial Regex KakuyomuIdPattern();

    private static string[] Segments(Uri uri) => uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string? ParseNcode(string value) =>
        NcodePattern().IsMatch(value) ? value.ToLowerInvariant() : null;

    private static string? ParseNcodeUrl(Uri uri) =>
        Segments(uri).FirstOrDefault() is { } first ? ParseNcode(first) : null;

    private static string? ParseKakuyomuWorkUrl(Uri uri)
    {
        var segments = Segments(uri);
        return segments.Length >= 2 && segments[0] == "works" && KakuyomuIdPattern().IsMatch(segments[1])
            ? segments[1]
            : null;
    }

    private static bool IsKakuyomuPublicationUrl(Uri uri)
    {
        var segments = Segments(uri);
        return segments.Length >= 3 && segments[0] == "publication" && segments[1] == "entry";
    }
}
