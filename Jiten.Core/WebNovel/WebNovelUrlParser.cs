using System.Text.RegularExpressions;
using Jiten.Core.Data.WebNovel;

namespace Jiten.Core.WebNovel;

public static partial class WebNovelUrlParser
{
    [GeneratedRegex(@"^[nN]\d{4}[a-zA-Z]{1,2}$")]
    private static partial Regex NcodePattern();

    /// <summary>
    /// Accepts a novel URL or a bare ncode. Returns false when the input isn't a supported source.
    /// </summary>
    public static bool TryParse(string input, out WebNovelProvider provider, out string sourceId)
    {
        provider = default;
        sourceId = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        input = input.Trim();

        // Bare ncode (n9669bk)
        if (NcodePattern().IsMatch(input))
        {
            provider = WebNovelProvider.Syosetu;
            sourceId = input.ToLowerInvariant();
            return true;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        var firstSegment = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        if (string.IsNullOrEmpty(firstSegment) || !NcodePattern().IsMatch(firstSegment))
            return false;

        provider = host switch
        {
            "ncode.syosetu.com" => WebNovelProvider.Syosetu,
            "novel18.syosetu.com" => WebNovelProvider.SyosetuNovel18,
            _ => default
        };

        if (provider == default)
            return false;

        sourceId = firstSegment.ToLowerInvariant();
        return true;
    }

    public static string BuildWorkUrl(WebNovelProvider provider, string sourceId) => provider switch
    {
        WebNovelProvider.Syosetu => $"https://ncode.syosetu.com/{sourceId}/",
        WebNovelProvider.SyosetuNovel18 => $"https://novel18.syosetu.com/{sourceId}/",
        _ => throw new NotSupportedException($"No URL template for provider {provider}")
    };
}
