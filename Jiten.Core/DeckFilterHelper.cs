using Jiten.Core.Data;
using Jiten.Core.Data.Billing;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core;

/// <summary>
/// Resolves a <see cref="FrequencyListDefinition"/> to the set of matching primary decks.
///
/// The filter semantics deliberately mirror the browse endpoint
/// (<c>MediaDeckController.GetMediaDecks</c>): media-type membership, release-year range, AND-combined
/// genre/tag includes, genre/tag excludes, and the difficulty expression
/// <c>(DifficultyOverride > -1 ? DifficultyOverride : Difficulty) + DeckDifficulty.UserAdjustment</c>.
/// </summary>
public static class DeckFilterHelper
{
    /// <summary>
    /// Builds the filtered primary-deck query (does not enumerate). Hand-picked mode restricts to the
    /// definition's <see cref="FrequencyListDefinition.DeckIds"/> intersected with existing primary decks.
    /// </summary>
    public static IQueryable<Deck> BuildQuery(JitenDbContext context, FrequencyListDefinition def, FrequencyListMode mode)
    {
        var query = context.Decks.AsNoTracking().Where(d => d.ParentDeckId == null);

        if (mode == FrequencyListMode.HandPicked)
        {
            var ids = def.DeckIds.Distinct().ToList();
            return query.Where(d => ids.Contains(d.DeckId));
        }

        if (def.MediaTypes.Count > 0)
        {
            var mediaTypes = def.MediaTypes.Select(m => (MediaType)m).ToList();
            query = query.Where(d => mediaTypes.Contains(d.MediaType));
        }

        if (def.YearFrom.HasValue)
            query = query.Where(d => d.ReleaseDate.Year >= def.YearFrom.Value);

        if (def.YearTo.HasValue)
            query = query.Where(d => d.ReleaseDate.Year <= def.YearTo.Value);

        if (def.DifficultyMin.HasValue)
        {
            var min = (float)def.DifficultyMin.Value;
            query = query.Where(d => (d.DifficultyOverride > -1 ? d.DifficultyOverride : d.Difficulty)
                + (float)(d.DeckDifficulty != null ? d.DeckDifficulty.UserAdjustment : 0) >= min);
        }

        if (def.DifficultyMax.HasValue)
        {
            var max = (float)def.DifficultyMax.Value;
            query = query.Where(d => (d.DifficultyOverride > -1 ? d.DifficultyOverride : d.Difficulty)
                + (float)(d.DeckDifficulty != null ? d.DeckDifficulty.UserAdjustment : 0) <= max);
        }

        // Genre include = AND semantics (one .Any per genre), matching browse.
        foreach (var genreId in def.GenresInclude.Distinct())
        {
            var genre = (Genre)genreId;
            query = query.Where(d => d.DeckGenres.Any(dg => dg.Genre == genre));
        }

        if (def.GenresExclude.Count > 0)
        {
            var excluded = def.GenresExclude.Select(g => (Genre)g).ToList();
            query = query.Where(d => !d.DeckGenres.Any(dg => excluded.Contains(dg.Genre)));
        }

        // Tag include = AND semantics, matching browse.
        foreach (var tagId in def.TagsInclude.Distinct())
        {
            query = query.Where(d => d.DeckTags.Any(dt => dt.TagId == tagId));
        }

        if (def.TagsExclude.Count > 0)
        {
            var excludedTags = def.TagsExclude.Distinct().ToList();
            query = query.Where(d => !d.DeckTags.Any(dt => excludedTags.Contains(dt.TagId)));
        }

        return query;
    }

    /// <summary>Resolves the matching primary-deck ids.</summary>
    public static async Task<List<int>> ResolveDeckIdsAsync(JitenDbContext context, FrequencyListDefinition def,
                                                            FrequencyListMode mode)
    {
        return await BuildQuery(context, def, mode).Select(d => d.DeckId).ToListAsync();
    }

    /// <summary>Cheap preview for the live builder: matched count plus a small sample of titles.</summary>
    public static async Task<(int Count, List<string> SampleTitles)> PreviewAsync(JitenDbContext context,
                                                                                  FrequencyListDefinition def,
                                                                                  FrequencyListMode mode, int sampleSize = 8)
    {
        var query = BuildQuery(context, def, mode);
        var count = await query.CountAsync();
        var sample = await query.OrderByDescending(d => d.CharacterCount)
                                .Take(sampleSize)
                                .Select(d => d.OriginalTitle)
                                .ToListAsync();
        return (count, sample);
    }

    /// <summary>
    /// Per-genre and per-tag deck counts for the current filtered set, so the builder can annotate each chip
    /// with how many matching decks carry it. Counts reflect all active filters (including this facet's own
    /// selection), so an AND-included genre reports the full match count and an excluded one reports zero.
    /// </summary>
    public static async Task<(Dictionary<int, int> Genres, Dictionary<int, int> Tags)> FacetCountsAsync(
        JitenDbContext context, FrequencyListDefinition def, FrequencyListMode mode)
    {
        var query = BuildQuery(context, def, mode);

        var genreCounts = await query.SelectMany(d => d.DeckGenres)
                                     .GroupBy(dg => dg.Genre)
                                     .Select(g => new { g.Key, Count = g.Count() })
                                     .ToDictionaryAsync(g => (int)g.Key, g => g.Count);

        var tagCounts = await query.SelectMany(d => d.DeckTags)
                                   .GroupBy(dt => dt.TagId)
                                   .Select(g => new { g.Key, Count = g.Count() })
                                   .ToDictionaryAsync(g => g.Key, g => g.Count);

        return (genreCounts, tagCounts);
    }
}
