using System.Security.Cryptography;
using System.Text;

namespace Jiten.Api.Services;

/// <summary>
/// Slug + anonymous-URL helpers for custom frequency lists. A list gets its PublicSlug the moment it
/// becomes permanent (saved) and keeps it for life — the slug URL is the anonymous way to reach the list
/// (share links and Yomitan's update index/download), so installed Yomitan dictionaries stay updatable.
/// There is deliberately no unshare: revoking the slug would silently break those installs.
/// </summary>
public static class FrequencyListLinks
{
    private const int SlugLength = 10;
    private const string SlugAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public static string GenerateSlug()
    {
        var bytes = RandomNumberGenerator.GetBytes(SlugLength);
        var sb = new StringBuilder(SlugLength);
        foreach (var b in bytes)
            sb.Append(SlugAlphabet[b % SlugAlphabet.Length]);
        return sb.ToString();
    }

    public static string GetApiBaseUrl(IConfiguration configuration) =>
        (configuration["ApiBaseUrl"] ?? "https://api.jiten.moe").TrimEnd('/');

    public static string IndexUrl(IConfiguration configuration, string slug) =>
        $"{GetApiBaseUrl(configuration)}/api/frequency-lists/shared/{slug}/index";

    public static string DownloadUrl(IConfiguration configuration, string slug, string format = "zip") =>
        $"{GetApiBaseUrl(configuration)}/api/frequency-lists/shared/{slug}?format={format}";
}
