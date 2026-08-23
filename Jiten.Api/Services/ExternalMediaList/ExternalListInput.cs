using System.Text.RegularExpressions;

namespace Jiten.Api.Services.ExternalMediaList;

/// <summary>Turns whatever a user pastes (profile URL, uXX id, bare username) into the identifier the provider API expects.</summary>
public static partial class ExternalListInput
{
    public static string Normalize(ExternalListProvider provider, string input)
    {
        var value = input.Trim();
        if (value.Length == 0)
            return value;

        var match = provider switch
                    {
                        ExternalListProvider.Anilist => AnilistUserUrlRegex().Match(value),
                        ExternalListProvider.Vndb => VndbUserUrlRegex().Match(value),
                        _ => Match.Empty,
                    };

        if (match.Success)
            value = Uri.UnescapeDataString(match.Groups[1].Value);

        value = value.Trim().Trim('/');

        // VNDB resolves usernames case-insensitively but uXX ids only in lower case.
        if (provider == ExternalListProvider.Vndb && VndbIdRegex().IsMatch(value))
            value = value.ToLowerInvariant();

        return value;
    }

    [GeneratedRegex(@"anilist\.co/user/([^/?#\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AnilistUserUrlRegex();

    [GeneratedRegex(@"vndb\.org/(u\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex VndbUserUrlRegex();

    [GeneratedRegex(@"^u\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex VndbIdRegex();
}
