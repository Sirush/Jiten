using System.Text.RegularExpressions;

namespace Jiten.Api.Services.MetadataEdits;

/// <summary>Strips tracking and affiliate data from contributed URLs; links are shown to every signed-in user and feed metadata refetches.</summary>
public static partial class LinkUrlCleaner
{
    private static readonly string[] TrackingPrefixes = ["utm_", "pk_", "mtm_", "_ga", "_gl", "ga_", "hsa_", "oly_"];

    private static readonly HashSet<string> TrackingParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "gclsrc", "gbraid", "wbraid", "dclid", "msclkid", "yclid", "twclid", "ttclid", "li_fat_id", "igshid", "igsh",
        "mc_cid", "mc_eid", "_hsenc", "_hsmi", "mkt_tok", "ref_src", "ref_url", "spm", "scm", "srsltid", "vero_id", "rb_clickid", "s_cid",
        "cmpid", "wickedid", "epik", "affiliate", "aff", "aff_id", "affid", "clickid", "irclickid", "zanpid"
    };

    /// <summary>YouTube share links carry a per-share id; v, t and list are the only parameters that change what plays.</summary>
    private static readonly HashSet<string> YouTubeTracking = new(StringComparer.OrdinalIgnoreCase) { "si", "feature", "pp", "ab_channel" };

    [GeneratedRegex(@"/(?:dp|gp/product|gp/aw/d|exec/obidos/ASIN|o/ASIN)/([A-Z0-9]{10})", RegexOptions.IgnoreCase)]
    private static partial Regex AmazonAsin();

    [GeneratedRegex(@"/ref=[^/?#]*", RegexOptions.IgnoreCase)]
    private static partial Regex AmazonRefSegment();

    /// <summary>Returns the cleaned absolute URL, or the input unchanged when it isn't an absolute http(s) URL.</summary>
    public static string Clean(string url) => Clean(url, unwrapDepth: 2);

    private static string Clean(string url, int unwrapDepth)
    {
        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return trimmed;

        var host = uri.Host.ToLowerInvariant();
        if (unwrapDepth > 0 && IsGoogleRedirect(host, uri.AbsolutePath) && RedirectTarget(uri.Query) is { } target)
            return Clean(target, unwrapDepth - 1);

        if (IsAmazon(host))
            return CleanAmazon(uri, host);

        var youTube = host is "youtu.be" || host == "youtube.com" || host.EndsWith(".youtube.com");
        var (head, rawQuery, rawFragment) = SplitRaw(trimmed);
        var query = FilterQuery(rawQuery, key => IsTracking(key) || (youTube && YouTubeTracking.Contains(key)));
        var fragment = rawFragment.StartsWith(":~:", StringComparison.Ordinal) ? "" : rawFragment;
        return head + (query.Length > 0 ? "?" + query : "") + (fragment.Length > 0 ? "#" + fragment : "");
    }

    /// <summary>Works on the original string so the path keeps the contributor's encoding; Uri would percent-encode Japanese paths.</summary>
    private static (string Head, string Query, string Fragment) SplitRaw(string url)
    {
        var hash = url.IndexOf('#');
        var fragment = hash < 0 ? "" : url[(hash + 1)..];
        var rest = hash < 0 ? url : url[..hash];
        var question = rest.IndexOf('?');
        return question < 0 ? (rest, "", fragment) : (rest[..question], rest[(question + 1)..], fragment);
    }

    private static bool IsTracking(string key) =>
        TrackingParams.Contains(key) || TrackingPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static bool IsGoogleRedirect(string host, string path) =>
        path is "/url" or "/imgres" && (host.StartsWith("google.") || host.StartsWith("www.google."));

    private static string? RedirectTarget(string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = pair[..eq];
            if (key is not ("q" or "url" or "imgrefurl"))
                continue;
            var value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            if (Uri.TryCreate(value, UriKind.Absolute, out var target) && (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps))
                return value;
        }

        return null;
    }

    private static bool IsAmazon(string host)
    {
        var bare = host.StartsWith("www.") ? host[4..] : host;
        return bare.StartsWith("amazon.") || bare is "amzn.to" or "amzn.asia";
    }

    /// <summary>Product pages reduce to /dp/ASIN; every query parameter on Amazon is a ref, tag or session id.</summary>
    private static string CleanAmazon(Uri uri, string host)
    {
        var match = AmazonAsin().Match(uri.AbsolutePath);
        if (match.Success)
            return $"https://{host}/dp/{match.Groups[1].Value.ToUpperInvariant()}";

        var (head, _, _) = SplitRaw(uri.OriginalString.Trim());
        var path = AmazonRefSegment().Replace(head[(head.IndexOf("//", StringComparison.Ordinal) + 2)..], "");
        var slash = path.IndexOf('/');
        return $"{uri.Scheme}://{(slash < 0 ? path + "/" : path)}";
    }

    private static string FilterQuery(string query, Func<string, bool> drop)
    {
        if (query.Length == 0)
            return "";
        var kept = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                        .Where(pair =>
                        {
                            var eq = pair.IndexOf('=');
                            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
                            return !drop(key);
                        });
        return string.Join("&", kept);
    }
}
