using Jiten.Api.Dtos;
using Jiten.Core;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Helpers;

public static class CompositionHelper
{
    public static async Task<List<WordSummaryDto>?> LoadComposedOf(
        IDbContextFactory<JitenDbContext> contextFactory, int wordId, short readingIndex)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync();

        var rows = await ctx.WordCompositions
            .AsNoTracking()
            .Where(c => c.WordId == wordId && c.ReadingIndex == readingIndex)
            .OrderBy(c => c.Position)
            .Select(c => new
            {
                c.Position,
                c.ComponentWordId,
                c.ComponentReadingIndex,
                c.ComponentSurface,
                c.IsGrammatical
            })
            .ToListAsync();

        if (rows.Count == 0) return null;

        var componentIds = rows.Where(r => !r.IsGrammatical).Select(r => r.ComponentWordId).Distinct().ToList();

        var definitions = await ctx.Definitions
            .AsNoTracking()
            .Where(d => componentIds.Contains(d.WordId))
            .OrderBy(d => d.WordId).ThenBy(d => d.SenseIndex)
            .Select(d => new { d.WordId, d.EnglishMeanings })
            .ToListAsync();

        var firstDefByWord = definitions
            .GroupBy(d => d.WordId)
            .ToDictionary(g => g.Key, g => g.First().EnglishMeanings.FirstOrDefault());

        var forms = await WordFormHelper.LoadWordForms(ctx, componentIds);

        var freqs = await ctx.WordFormFrequencies
            .AsNoTracking()
            .Where(wff => componentIds.Contains(wff.WordId))
            .ToDictionaryAsync(wff => (wff.WordId, wff.ReadingIndex));

        return rows.Select(r =>
        {
            // The surface keeps the particle's in-phrase form; the dictionary form would misspell the expression.
            if (r.IsGrammatical)
                return new WordSummaryDto
                {
                    WordId = r.ComponentWordId,
                    ReadingIndex = (byte)r.ComponentReadingIndex,
                    Reading = r.ComponentSurface,
                    ReadingFurigana = r.ComponentSurface,
                    IsGrammatical = true
                };

            var form = forms.GetValueOrDefault((r.ComponentWordId, r.ComponentReadingIndex));
            var freq = freqs.GetValueOrDefault((r.ComponentWordId, r.ComponentReadingIndex));
            return new WordSummaryDto
            {
                WordId = r.ComponentWordId,
                ReadingIndex = (byte)r.ComponentReadingIndex,
                Reading = form?.Text ?? r.ComponentSurface,
                ReadingFurigana = form?.RubyText ?? r.ComponentSurface,
                MainDefinition = firstDefByWord.GetValueOrDefault(r.ComponentWordId),
                FrequencyRank = freq?.FrequencyRank > 0 ? freq.FrequencyRank : null
            };
        }).ToList();
    }

    public static async Task<(List<WordSummaryDto> Items, int Total)> LoadUsedIn(
        IDbContextFactory<JitenDbContext> contextFactory, int componentWordId, short componentReadingIndex,
        int skip, int take)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync();

        var distinctParents = ctx.WordCompositions
            .AsNoTracking()
            .Where(c => c.ComponentWordId == componentWordId && c.ComponentReadingIndex == componentReadingIndex
                        && !c.IsGrammatical)
            .Select(c => new { c.WordId, c.ReadingIndex })
            .Distinct();

        var total = await distinctParents.CountAsync();
        if (total == 0) return (new List<WordSummaryDto>(), 0);

        var pagedParents = await distinctParents
            .Select(p => new
            {
                p.WordId,
                p.ReadingIndex,
                Rank = ctx.WordFormFrequencies
                    .Where(f => f.WordId == p.WordId && f.ReadingIndex == p.ReadingIndex)
                    .Select(f => (int?)f.FrequencyRank)
                    .FirstOrDefault()
            })
            .OrderBy(x => x.Rank == null)
            .ThenBy(x => x.Rank)
            .ThenBy(x => x.WordId)
            .ThenBy(x => x.ReadingIndex)
            .Skip(skip).Take(take)
            .ToListAsync();

        if (pagedParents.Count == 0) return (new List<WordSummaryDto>(), total);

        var parentIds = pagedParents.Select(p => p.WordId).Distinct().ToList();

        var definitions = await ctx.Definitions
            .AsNoTracking()
            .Where(d => parentIds.Contains(d.WordId))
            .OrderBy(d => d.WordId).ThenBy(d => d.SenseIndex)
            .Select(d => new { d.WordId, d.EnglishMeanings })
            .ToListAsync();

        var firstDefByWord = definitions
            .GroupBy(d => d.WordId)
            .ToDictionary(g => g.Key, g => g.First().EnglishMeanings.FirstOrDefault());

        var forms = await WordFormHelper.LoadWordForms(ctx, parentIds);

        var surfaceRows = await ctx.WordCompositions
            .AsNoTracking()
            .Where(c => c.ComponentWordId == componentWordId
                        && c.ComponentReadingIndex == componentReadingIndex
                        && !c.IsGrammatical
                        && parentIds.Contains(c.WordId))
            .OrderBy(c => c.WordId).ThenBy(c => c.ReadingIndex).ThenBy(c => c.Position)
            .Select(c => new { c.WordId, c.ReadingIndex, c.ComponentSurface })
            .ToListAsync();

        var surfaceMap = surfaceRows
            .GroupBy(s => (s.WordId, s.ReadingIndex))
            .ToDictionary(g => g.Key, g => g.First().ComponentSurface);

        var items = pagedParents.Select(p =>
        {
            var form = forms.GetValueOrDefault((p.WordId, p.ReadingIndex));
            return new WordSummaryDto
            {
                WordId = p.WordId,
                ReadingIndex = (byte)p.ReadingIndex,
                Reading = form?.Text ?? "",
                ReadingFurigana = form?.RubyText ?? form?.Text ?? "",
                MainDefinition = firstDefByWord.GetValueOrDefault(p.WordId),
                FrequencyRank = p.Rank > 0 ? p.Rank : null,
                MatchSurface = surfaceMap.GetValueOrDefault((p.WordId, p.ReadingIndex))
            };
        }).ToList();

        return (items, total);
    }
}
