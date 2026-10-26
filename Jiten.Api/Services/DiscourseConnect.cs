using System.Security.Cryptography;
using System.Text;
using Jiten.Core.Data;
using Microsoft.AspNetCore.WebUtilities;

namespace Jiten.Api.Services;

/// <summary>Bound from the <c>Discourse</c> config section. Keys live in sharedsettings.example.json.</summary>
public class DiscourseOptions
{
    public const string SectionName = "Discourse";

    /// <summary>Forum root, e.g. https://community.jiten.moe.</summary>
    public string Url { get; set; } = "";

    /// <summary>Must equal the forum's <c>discourse_connect_secret</c> setting.</summary>
    public string SsoSecret { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(SsoSecret);

    /// <summary>Read-only global key for topic lookup by embed URL, which no granular scope covers; post reads stay anonymous.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Granular key with only topics:write, used to create discussion topics; falls back to <see cref="ApiKey"/> when empty.</summary>
    public string TopicWriteApiKey { get; set; } = "";

    /// <summary>Secret of the forum webhook that keeps stored reply counts current; the webhook endpoint answers 404 while it is empty.</summary>
    public string WebhookSecret { get; set; } = "";

    /// <summary>Forum category id per discussion subject (config keys "deck", "list", bound case-insensitively); a subject without one posts to Uncategorized.</summary>
    public Dictionary<ForumSubject, int> CategoryIds { get; set; } = [];

    /// <summary>Must be staff: Discourse throttles topic creation for everyone else to one per minute site-wide.</summary>
    public string ApiUsername { get; set; } = "system";

    /// <summary>Hosts besides the forum's own that post images may load from, such as its upload CDN; any other image becomes a link.</summary>
    public string[] MediaHosts { get; set; } = ["jiten-community.b-cdn.net"];

    /// <summary>
    /// Keyed calls per minute per API instance. Discourse counts every API-key request against one forum-wide limit
    /// (DISCOURSE_MAX_ADMIN_API_REQS_PER_MINUTE, 60 by default), and two instances overlap during a deploy.
    /// </summary>
    public int LookupsPerMinute { get; set; } = 15;

    /// <inheritdoc cref="LookupsPerMinute"/>
    public int CreationsPerMinute { get; set; } = 5;

    /// <summary>All anonymous reads come from this server's IP; Discourse blocks an IP past 50 per 10 seconds unless it is in DISCOURSE_MAX_REQS_PER_IP_EXCEPTIONS.</summary>
    public int AnonymousReadsPer10Seconds { get; set; } = 30;

    /// <summary>Https only: the key travels in request headers.</summary>
    public bool IsDiscussionConfigured => Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>DiscourseConnect payload signing, with Jiten as the identity provider (https://meta.discourse.org/t/13045).</summary>
public static class DiscourseConnect
{
    public readonly record struct Request(string Nonce, string ReturnUrl);

    /// <summary>Verifies and decodes the payload the forum sends, or returns null if the signature or return URL is wrong.</summary>
    public static Request? ReadRequest(string sso, string sig, DiscourseOptions options, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(sso) || string.IsNullOrEmpty(sig)) return null;

        byte[] given;
        try
        {
            given = Convert.FromHexString(sig);
        }
        catch (FormatException)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(given, ComputeHmac(sso, options.SsoSecret)))
        {
            logger?.LogWarning("Rejected DiscourseConnect payload: signature mismatch, Discourse:SsoSecret differs from the forum's discourse_connect_secret");
            return null;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(sso));
        }
        catch (FormatException)
        {
            return null;
        }

        var query = QueryHelpers.ParseQuery(decoded);
        var nonce = query.TryGetValue("nonce", out var n) ? n.ToString() : "";
        var returnUrl = query.TryGetValue("return_sso_url", out var r) ? r.ToString() : "";

        // The signature already proves the forum sent this; the host check stops a leaked secret from turning the endpoint into an open redirect.
        if (nonce.Length == 0 || !returnUrl.StartsWith(options.Url.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogWarning("Rejected DiscourseConnect payload: return URL {ReturnUrl} is not under Discourse:Url {DiscourseUrl}", returnUrl, options.Url);
            return null;
        }

        return new Request(nonce, returnUrl);
    }

    /// <summary>Signs the user fields and returns the forum URL to send the browser to.</summary>
    public static string BuildReturnUrl(Request request, IEnumerable<KeyValuePair<string, string>> fields, DiscourseOptions options)
    {
        var pairs = fields.Prepend(new KeyValuePair<string, string>("nonce", request.Nonce));
        var payload = string.Join("&", pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        var sso = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        var sig = Sign(sso, options.SsoSecret);

        return QueryHelpers.AddQueryString(request.ReturnUrl, new Dictionary<string, string?> { ["sso"] = sso, ["sig"] = sig });
    }

    public static string Sign(string sso, string secret) => Convert.ToHexStringLower(ComputeHmac(sso, secret));

    private static byte[] ComputeHmac(string sso, string secret) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(sso));
}
