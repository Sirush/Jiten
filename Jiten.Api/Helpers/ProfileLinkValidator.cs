namespace Jiten.Api.Helpers;

public static class ProfileLinkValidator
{
    public const int MaxLinks = 3;
    public const int MaxLength = 200;

    /// <summary>Matched anywhere in the host and path.</summary>
    private static readonly string[] BlockedTerms = ["porn", "hentai"];

    /// <summary>Shorteners hide the destination behind a host visitors would trust.</summary>
    private static readonly HashSet<string> Shorteners =
    [
        "bit.ly", "bitly.com", "tinyurl.com", "t.co", "goo.gl", "ow.ly", "is.gd", "v.gd", "buff.ly", "rebrand.ly", "cutt.ly", "shorturl.at",
        "rb.gy", "tiny.cc", "t.ly", "s.id", "lnkd.in", "bl.ink", "short.io", "shorturl.asia", "x.gd", "urlr.me", "tinu.be"
    ];

    /// <summary>Null when the link may be saved; otherwise the message to show.</summary>
    public static string? Validate(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp
            ? "Links must start with https://."
            : Check(link, requireHttps: true);

    /// <summary>Links saved before https was required still show; every other rule applies to them as it does on save.</summary>
    public static bool IsDisplayable(string link) => Check(link, requireHttps: false) == null;

    private static string? Check(string link, bool requireHttps)
    {
        if (link.Length > MaxLength)
            return $"Links can be up to {MaxLength} characters.";

        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && (requireHttps || uri.Scheme != Uri.UriSchemeHttp))
            || !string.IsNullOrEmpty(uri.UserInfo))
            return "Links must be full https addresses.";

        if (uri.HostNameType != UriHostNameType.Dns || !uri.Host.Contains('.'))
            return "Links must point to a website name, not an IP address.";

        var host = uri.IdnHost.ToLowerInvariant();
        if (host.StartsWith("www."))
            host = host[4..];
        if (Shorteners.Contains(host))
            return "Link shorteners aren't allowed. Use the full address.";

        var hostAndPath = $"{uri.Host}{Uri.UnescapeDataString(uri.AbsolutePath)}".ToLowerInvariant();
        if (BlockedTerms.Any(hostAndPath.Contains) || DisplayNameValidator.ContainsOffensiveWord(hostAndPath))
            return "That link isn't allowed.";

        return null;
    }
}
