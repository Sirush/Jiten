using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data.JMDict;
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

    /// <summary>One row per parent word, not per form: a word whose spellings all contain the component is listed once, ranked by its best form.</summary>
    public static async Task<(List<WordSummaryDto> Items, int Total)> LoadUsedIn(
        IDbContextFactory<JitenDbContext> contextFactory, int componentWordId, short componentReadingIndex,
        int skip, int take)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync();

        var matches = ctx.WordCompositions
            .AsNoTracking()
            .Where(c => c.ComponentWordId == componentWordId && c.ComponentReadingIndex == componentReadingIndex
                        && !c.IsGrammatical);

        var total = await matches.Select(c => c.WordId).Distinct().CountAsync();
        if (total == 0) return (new List<WordSummaryDto>(), 0);

        var pagedParents = await (
                from c in matches
                join f in ctx.WordFormFrequencies.Where(f => f.FrequencyRank > 0)
                    on new { c.WordId, c.ReadingIndex } equals new { f.WordId, f.ReadingIndex } into fs
                from f in fs.DefaultIfEmpty()
                group (int?)f.FrequencyRank by c.WordId
                into g
                select new { WordId = g.Key, Rank = g.Min() })
            .OrderBy(x => x.Rank == null)
            .ThenBy(x => x.Rank)
            .ThenBy(x => x.WordId)
            .Skip(skip).Take(take)
            .ToListAsync();

        if (pagedParents.Count == 0) return (new List<WordSummaryDto>(), total);

        var parentIds = pagedParents.Select(p => p.WordId).ToList();

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
        var freqs = await WordFormHelper.LoadWordFormFrequencies(ctx, parentIds);
        var componentText = await ctx.WordForms
            .AsNoTracking()
            .Where(f => f.WordId == componentWordId && f.ReadingIndex == componentReadingIndex)
            .Select(f => f.Text)
            .FirstOrDefaultAsync() ?? "";

        var surfaceRows = await matches
            .Where(c => parentIds.Contains(c.WordId))
            .OrderBy(c => c.WordId).ThenBy(c => c.ReadingIndex).ThenBy(c => c.Position)
            .Select(c => new { c.WordId, c.ReadingIndex, c.ComponentSurface })
            .ToListAsync();

        var surfacesByWord = surfaceRows
            .GroupBy(s => s.WordId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(s => s.ReadingIndex).ToDictionary(r => r.Key, r => r.First().ComponentSurface));

        var items = pagedParents.Select(p =>
        {
            var surfaces = surfacesByWord[p.WordId];
            var readingIndex = PickDisplayForm(p.WordId, componentText, surfaces, forms, freqs);
            var form = forms.GetValueOrDefault((p.WordId, readingIndex));
            return new WordSummaryDto
            {
                WordId = p.WordId,
                ReadingIndex = (byte)readingIndex,
                Reading = form?.Text ?? "",
                ReadingFurigana = form?.RubyText ?? form?.Text ?? "",
                MainDefinition = firstDefByWord.GetValueOrDefault(p.WordId),
                FrequencyRank = p.Rank,
                MatchSurface = surfaces[readingIndex]
            };
        }).ToList();

        return (items, total);
    }

    /// <summary>
    /// Picks among the forms that spell the component the way the component's page does (another kanji variant or a kana
    /// slot would not highlight), then the JMDict headword among those, then the best-ranked one.
    /// </summary>
    internal static short PickDisplayForm(int wordId, string componentText,
                                          IReadOnlyDictionary<short, string> surfacesByReadingIndex,
                                          IReadOnlyDictionary<(int, short), JmDictWordForm> forms,
                                          IReadOnlyDictionary<(int, short), JmDictWordFormFrequency> freqs)
    {
        var all = surfacesByReadingIndex.Keys.Order().ToList();
        var sameSpelling = all.Where(ri => SpellsLike(surfacesByReadingIndex[ri], componentText)).ToList();
        var candidates = sameSpelling.Count > 0 ? sameSpelling : all;

        foreach (var ri in candidates)
            if (forms.GetValueOrDefault((wordId, ri)) is { } form && IsHeadwordKanji(form))
                return ri;

        return candidates
            .OrderBy(ri => freqs.GetValueOrDefault((wordId, ri))?.FrequencyRank is int rank and > 0 ? rank : int.MaxValue)
            .ThenBy(ri => ri)
            .First();
    }

    /// <summary>Compares kanji only, so an inflected slot (食べ in 食べ物) matches 食べる and any kana slot matches a kana page.</summary>
    internal static bool SpellsLike(string surface, string componentText)
        => surface.Where(JapaneseTextHelper.IsKanji).SequenceEqual(componentText.Where(JapaneseTextHelper.IsKanji));

    private static bool IsHeadwordKanji(JmDictWordForm form)
        => form.FormType == JmDictFormType.KanjiForm
           && form.IsActiveInLatestSource
           && !form.IsObsolete
           && !form.IsSearchOnly
           && form.InfoTags?.Any(t => t is "rK" or "iK") != true;
}
