using System.Text.RegularExpressions;
using Jiten.Core.Data.YouTube;

namespace Jiten.Core.YouTube;

/// <summary>Per-source admission rules, applied before any subtitle is fetched where the listing allows it.</summary>
public record YouTubeSourceFilters(string? TitleInclude, string? TitleExclude, int? MinRuntimeSeconds, int? MaxRuntimeSeconds)
{
    public static readonly YouTubeSourceFilters None = new(null, null, null, null);

    public static YouTubeSourceFilters From(YouTubeSource source) =>
        new(source.TitleFilterInclude, source.TitleFilterExclude, source.MinRuntimeSeconds, source.MaxRuntimeSeconds);
}

/// <summary>Hard requirements every video must pass before it becomes a subdeck. Reasons use the ledger's machine prefixes.</summary>
public static class YouTubeContentPolicy
{
    public static string? CheckRuntime(int? runtimeSeconds, YouTubeSourceFilters filters)
    {
        if (runtimeSeconds is not > 0)
            return null;

        if (filters.MinRuntimeSeconds is > 0 && runtimeSeconds < filters.MinRuntimeSeconds)
            return $"runtime: {runtimeSeconds}s under the {filters.MinRuntimeSeconds}s minimum";

        if (filters.MaxRuntimeSeconds is > 0 && runtimeSeconds > filters.MaxRuntimeSeconds)
            return $"runtime: {runtimeSeconds}s over the {filters.MaxRuntimeSeconds}s maximum";

        return null;
    }

    public const int MinCharacters = 300;
    public const int MinCharactersPerMinute = 20;

    public static string? CheckTitle(string title, string? includePattern, string? excludePattern)
    {
        if (!string.IsNullOrEmpty(includePattern) && !Regex.IsMatch(title, includePattern, RegexOptions.IgnoreCase))
            return $"title-filter: include {includePattern}";

        if (!string.IsNullOrEmpty(excludePattern) && Regex.IsMatch(title, excludePattern, RegexOptions.IgnoreCase))
            return $"title-filter: exclude {excludePattern}";

        return null;
    }

    public static string? CheckDensity(YouTubeSubtitleCleanResult cleaned, int? runtimeSeconds)
    {
        var characters = cleaned.CharacterCount;
        var minutes = runtimeSeconds is > 0 ? runtimeSeconds.Value / 60.0 : 0;
        var perMinute = minutes > 0 ? characters / minutes : 0;

        if (characters < MinCharacters || (minutes > 0 && perMinute < MinCharactersPerMinute))
            return $"density: {characters} chars, {perMinute:0}/min";

        return null;
    }
}
