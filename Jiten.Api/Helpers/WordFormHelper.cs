using Jiten.Api.Dtos;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Helpers;

public static class WordFormHelper
{
    /// <summary>
    /// Returns true if the card at (wordId, readingIndex) is a redundant kana form,
    /// meaning it's a kana reading AND at least one kanji reading for the same word
    /// exists in <paramref name="cardKeysByWord"/>.
    /// </summary>
    public static bool IsRedundantKanaCard(
        IWordFormSiblingCache cache, int wordId, byte readingIndex,
        Dictionary<int, List<byte>> cardKeysByWord)
    {
        if (cache.GetKanjiIndexesForKana(wordId, readingIndex) == null)
            return false;
        if (!cardKeysByWord.TryGetValue(wordId, out var siblings))
            return false;
        return siblings.Any(ri => cache.GetKanaIndexesForKanji(wordId, ri) != null);
    }

    public static Dictionary<int, List<byte>> GroupCardKeysByWord(
        HashSet<(int WordId, byte ReadingIndex)> allCardKeys)
    {
        var result = new Dictionary<int, List<byte>>();
        foreach (var (wordId, readingIndex) in allCardKeys)
        {
            if (!result.TryGetValue(wordId, out var list))
            {
                list = new List<byte>();
                result[wordId] = list;
            }
            list.Add(readingIndex);
        }
        return result;
    }

    public static async Task RemoveRedundantKanaSrsCards(
        UserDbContext userContext, IWordFormSiblingCache cache,
        string userId, int wordId, byte readingIndex)
    {
        var kanaIndexes = cache.GetKanaIndexesForKanji(wordId, readingIndex);
        if (kanaIndexes is not { Length: > 0 })
            return;
        var cards = await userContext.FsrsCards
            .Where(c => c.UserId == userId && c.WordId == wordId && kanaIndexes.Contains(c.ReadingIndex))
            .ToListAsync();
        if (cards.Count > 0)
            userContext.FsrsCards.RemoveRange(cards);
    }

    public static WordFormDto ToFormDto(JmDictWordForm form, JmDictWordFormFrequency? freq, Dictionary<int, int>? usedInMediaByType = null)
    {
        return new WordFormDto
        {
            Text = form.RubyText,
            ReadingIndex = (byte)form.ReadingIndex,
            ReadingType = (JmDictReadingType)(int)form.FormType,
            FrequencyRank = freq?.FrequencyRank ?? 0,
            FrequencyPercentage = freq?.FrequencyPercentage ?? 0,
            UsedInMediaAmount = freq?.UsedInMediaAmount ?? 0,
            UsedInMediaAmountByType = usedInMediaByType ?? new()
        };
    }

    public static WordFormDto ToPlainFormDto(JmDictWordForm form, JmDictWordFormFrequency? freq)
    {
        return new WordFormDto
        {
            Text = form.Text,
            ReadingIndex = (byte)form.ReadingIndex,
            ReadingType = (JmDictReadingType)(int)form.FormType,
            FrequencyRank = freq?.FrequencyRank ?? 0,
            FrequencyPercentage = freq?.FrequencyPercentage ?? 0,
            UsedInMediaAmount = freq?.UsedInMediaAmount ?? 0
        };
    }

    public static async Task<Dictionary<(int, short), JmDictWordForm>> LoadWordForms(JitenDbContext context, List<int> wordIds)
    {
        var forms = await context.WordForms
            .AsNoTracking()
            .Where(wf => wordIds.Contains(wf.WordId))
            .ToDictionaryAsync(wf => (wf.WordId, wf.ReadingIndex));
        RubyTextHelper.EnrichForms(forms);
        return forms;
    }

    public static async Task<Dictionary<(int, short), JmDictWordFormFrequency>> LoadWordFormFrequencies(JitenDbContext context, List<int> wordIds)
    {
        return await context.WordFormFrequencies
            .AsNoTracking()
            .Where(wff => wordIds.Contains(wff.WordId))
            .ToDictionaryAsync(wff => (wff.WordId, wff.ReadingIndex));
    }

    public static long EncodeWordKey(int wordId, byte readingIndex)
        => ((long)wordId << 8) | readingIndex;

    public static long EncodeWordKey(int wordId, short readingIndex)
        => ((long)wordId << 8) | (byte)readingIndex;

    public static async Task<HashSet<long>> GetKanaFormKeys(JitenDbContext context, IEnumerable<int> wordIds)
    {
        var distinctIds = wordIds.Distinct().ToList();
        if (distinctIds.Count == 0) return [];

        var kanaForms = await context.WordForms.AsNoTracking()
            .Where(wf => distinctIds.Contains(wf.WordId) && wf.FormType == JmDictFormType.KanaForm)
            .Select(wf => new { wf.WordId, wf.ReadingIndex })
            .ToListAsync();
        return kanaForms
            .Select(wf => EncodeWordKey(wf.WordId, wf.ReadingIndex))
            .ToHashSet();
    }

    public static void ExpandKanaRedundancyKeys(
        IWordFormSiblingCache cache,
        IEnumerable<(int WordId, byte ReadingIndex)> kanjiCards,
        HashSet<long> target)
    {
        foreach (var (wordId, ri) in kanjiCards)
        {
            var kanaIndexes = cache.GetKanaIndexesForKanji(wordId, ri);
            if (kanaIndexes == null) continue;
            foreach (var kanaRi in kanaIndexes)
                target.Add(EncodeWordKey(wordId, kanaRi));
        }
    }

    public static async Task<List<JmDictWordForm>> LoadWordFormsForWord(JitenDbContext context, int wordId)
    {
        var forms = await context.WordForms
            .AsNoTracking()
            .Where(wf => wf.WordId == wordId)
            .OrderBy(wf => wf.ReadingIndex)
            .ToListAsync();
        RubyTextHelper.EnrichForms(forms);
        return forms;
    }
}
