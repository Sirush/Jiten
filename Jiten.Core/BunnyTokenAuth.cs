using System.Security.Cryptography;
using System.Text;

namespace Jiten.Core;

public static class BunnyTokenAuth
{
    public static string Sign(string securityKey, string urlPath, long expiresUnixSeconds)
    {
        var raw = securityKey + urlPath + expiresUnixSeconds.ToString();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToBase64String(hash)
                      .Replace('+', '-')
                      .Replace('/', '_')
                      .TrimEnd('=');
    }

    /// <summary>Builds a full token-authenticated URL: {baseUrl}{urlPath}?token=...&amp;expires=...</summary>
    public static string BuildSignedUrl(string secureBaseUrl, string securityKey, string urlPath, long expiresUnixSeconds)
    {
        var token = Sign(securityKey, urlPath, expiresUnixSeconds);
        return $"{secureBaseUrl.TrimEnd('/')}{urlPath}?token={token}&expires={expiresUnixSeconds}";
    }
}
