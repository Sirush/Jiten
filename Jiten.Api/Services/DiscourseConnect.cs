using System.Security.Cryptography;
using System.Text;
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
}

/// <summary>DiscourseConnect payload signing, with Jiten as the identity provider (https://meta.discourse.org/t/13045).</summary>
public static class DiscourseConnect
{
    public readonly record struct Request(string Nonce, string ReturnUrl);

    /// <summary>Verifies and decodes the payload the forum sends, or returns null if the signature or return URL is wrong.</summary>
    public static Request? ReadRequest(string sso, string sig, DiscourseOptions options)
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
            return null;

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
            return null;

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
