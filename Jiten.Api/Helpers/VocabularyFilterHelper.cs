using Jiten.Api.Enums;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Helpers;

public static class VocabularyFilterHelper
{
    public static string[] ParseCommaSeparatedTags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static async Task<HashSet<int>?> GetPosFilteredWordIds(
        JitenDbContext context, string? posCsv, IEnumerable<int> candidateWordIds)
    {
        var tags = ParseCommaSeparatedTags(posCsv);
        if (tags.Length == 0) return null;

        var wordIdList = candidateWordIds.Distinct().ToList();
        return (await context.JMDictWords.AsNoTracking()
            .Where(w => wordIdList.Contains(w.WordId) && w.PartsOfSpeech.Any(p => tags.Contains(p)))
            .Select(w => w.WordId)
            .ToListAsync())
            .ToHashSet();
    }

    /// <summary>Search text, parts of speech to keep or drop, and kana-only words, as the vocabulary lists accept them.</summary>
    public static async Task<IQueryable<DeckWord>> ApplyWordFilters(JitenDbContext context, IQueryable<DeckWord> query, string? search,
                                                                    string? pos, string? excludePos, bool hideKanaOnly)
    {
        if (!string.IsNullOrWhiteSpace(search))
        {
            var matchingWordIds = await SearchHelper.ResolveSearchWordIds(context, search);
            query = query.Where(dw => matchingWordIds.Contains(dw.WordId));
        }

        var posTags = ParseCommaSeparatedTags(pos);
        if (posTags.Length > 0)
        {
            var wordIdsWithPos = context.JMDictWords.AsNoTracking()
                                        .Where(w => w.PartsOfSpeech.Any(p => posTags.Contains(p)));
            query = query.Where(dw => wordIdsWithPos.Any(w => w.WordId == dw.WordId));
        }

        var excludePosTags = ParseCommaSeparatedTags(excludePos);
        if (excludePosTags.Length > 0)
        {
            var wordIdsToExclude = context.JMDictWords.AsNoTracking()
                                          .Where(w => w.PartsOfSpeech.Any(p => excludePosTags.Contains(p)));
            query = query.Where(dw => !wordIdsToExclude.Any(w => w.WordId == dw.WordId));
        }

        if (hideKanaOnly)
        {
            query = query.Where(dw => context.WordForms
                                             .Any(wf => wf.WordId == dw.WordId && wf.ReadingIndex == (short)dw.ReadingIndex
                                                        && wf.FormType != JmDictFormType.KanaForm));
        }

        return query;
    }

    /// <summary>One page sorted by globalFreq, deckFreq or chrono (default), ties on DeckWordId, with the total after the display filter.</summary>
    public static async Task<(List<DeckWord> Page, int Total)> PageAsync(JitenDbContext context, ICurrentUserService currentUserService,
                                                                          IQueryable<DeckWord> query, VocabularyDisplayFilter displayFilter,
                                                                          string? sortBy, SortOrder sortOrder, MediaType? frequencySource,
                                                                          int skip, int take)
    {
        if (!currentUserService.IsAuthenticated || !displayFilter.IsActive)
            return (await Sort(context, query, sortBy, sortOrder, frequencySource).Skip(skip).Take(take).ToListAsync(),
                    await query.CountAsync());

        var all = await query.ToListAsync();
        var knownStates = await currentUserService.GetKnownWordsState(all.Select(dw => (dw.WordId, dw.ReadingIndex)).ToList());
        var filtered = all.Where(dw => displayFilter.Matches(knownStates.GetValueOrDefault((dw.WordId, dw.ReadingIndex), [KnownState.New])))
                          .ToList();

        var sorted = await SortInMemoryAsync(context, filtered, sortBy, sortOrder, frequencySource);
        return (sorted.Skip(skip).Take(take).ToList(), filtered.Count);
    }

    /// <summary>Unranked words sort as rank 0 site-wide and last in both directions for a media type.</summary>
    private static IQueryable<DeckWord> Sort(JitenDbContext context, IQueryable<DeckWord> query, string? sortBy, SortOrder sortOrder,
                                             MediaType? frequencySource)
    {
        var ascending = sortOrder == SortOrder.Ascending;

        switch (sortBy)
        {
            case "globalFreq":
            {
                var ranked = frequencySource is { } source
                    ? query.Select(dw => new
                    {
                        Word = dw,
                        Rank = context.WordFormFrequenciesByType
                                      .Where(f => f.MediaType == source && f.WordId == dw.WordId && f.ReadingIndex == (short)dw.ReadingIndex)
                                      .Select(f => (int?)f.FrequencyRank)
                                      .FirstOrDefault() ?? int.MaxValue
                    })
                    : query.Select(dw => new
                    {
                        Word = dw,
                        Rank = context.WordFormFrequencies
                                      .Where(f => f.WordId == dw.WordId && f.ReadingIndex == (short)dw.ReadingIndex)
                                      .Select(f => f.FrequencyRank)
                                      .FirstOrDefault()
                    });
                ranked = ascending
                    ? ranked.OrderBy(r => r.Rank).ThenBy(r => r.Word.DeckWordId)
                    : ranked.OrderByDescending(r => r.Rank).ThenBy(r => r.Word.DeckWordId);
                return ranked.Select(r => r.Word);
            }
            case "deckFreq":
                return ascending
                    ? query.OrderByDescending(dw => dw.Occurrences).ThenBy(dw => dw.DeckWordId)
                    : query.OrderBy(dw => dw.Occurrences).ThenBy(dw => dw.DeckWordId);
            default:
                return ascending
                    ? query.OrderBy(dw => dw.DeckWordId)
                    : query.OrderByDescending(dw => dw.DeckWordId);
        }
    }

    private static async Task<List<DeckWord>> SortInMemoryAsync(JitenDbContext context, List<DeckWord> words, string? sortBy, SortOrder sortOrder,
                                                                MediaType? frequencySource)
    {
        var ascending = sortOrder == SortOrder.Ascending;

        switch (sortBy)
        {
            case "globalFreq":
            {
                var wordIds = words.Select(w => w.WordId).Distinct().ToList();
                var rows = frequencySource is { } source
                    ? await context.WordFormFrequenciesByType.AsNoTracking()
                                   .Where(f => f.MediaType == source && wordIds.Contains(f.WordId))
                                   .Select(f => new { f.WordId, f.ReadingIndex, f.FrequencyRank })
                                   .ToListAsync()
                    : await context.WordFormFrequencies.AsNoTracking()
                                   .Where(f => wordIds.Contains(f.WordId))
                                   .Select(f => new { f.WordId, f.ReadingIndex, f.FrequencyRank })
                                   .ToListAsync();

                var ranks = new Dictionary<(int, short), int>();
                foreach (var r in rows)
                    ranks.TryAdd((r.WordId, r.ReadingIndex), r.FrequencyRank);

                var missing = frequencySource.HasValue ? int.MaxValue : 0;
                int Rank(DeckWord w) => ranks.GetValueOrDefault((w.WordId, (short)w.ReadingIndex), missing);

                return (ascending ? words.OrderBy(Rank) : words.OrderByDescending(Rank)).ThenBy(w => w.DeckWordId).ToList();
            }
            case "deckFreq":
                return (ascending ? words.OrderByDescending(w => w.Occurrences) : words.OrderBy(w => w.Occurrences))
                       .ThenBy(w => w.DeckWordId).ToList();
            default:
                return (ascending ? words.OrderBy(w => w.DeckWordId) : words.OrderByDescending(w => w.DeckWordId)).ToList();
        }
    }
}
