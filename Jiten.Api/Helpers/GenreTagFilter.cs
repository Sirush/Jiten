using Jiten.Core.Data;

namespace Jiten.Api.Helpers;

/// <summary>Comma-separated genre and tag ids from a browse query; included ones must all be present, any excluded one rules an item out.</summary>
public sealed record GenreTagFilter(string? Genres, string? ExcludeGenres, string? Tags, string? ExcludeTags)
{
    public List<Genre> IncludedGenres => ParseIds(Genres).Select(id => (Genre)id).ToList();
    public List<Genre> ExcludedGenres => ParseIds(ExcludeGenres).Select(id => (Genre)id).ToList();
    public List<int> IncludedTags => ParseIds(Tags);
    public List<int> ExcludedTags => ParseIds(ExcludeTags);

    private static List<int> ParseIds(string? csv) =>
        string.IsNullOrEmpty(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                 .Select(v => int.TryParse(v, out var id) ? (int?)id : null)
                 .Where(id => id.HasValue)
                 .Select(id => id!.Value)
                 .Distinct()
                 .ToList();
}
