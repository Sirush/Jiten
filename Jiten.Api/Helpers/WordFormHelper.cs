using Jiten.Api.Dtos;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Helpers;

/// <summary>A card that a Mastered sibling covers, paired with the sibling that covers it.</summary>
public record RedundantFormPair(byte ReadingIndex, byte CoveringReadingIndex);

/// <summary>
/// The candidates an import must not insert, each mapped to the form covering it, and how many of them carried
/// history worth archiving.
/// </summary>
public record RedundantImportResult(Dictionary<FsrsCard, byte> Covering, int Archived)
{
    public int Count => Covering.Count;

    public bool Contains(FsrsCard card) => Covering.ContainsKey(card);
}

/// <summary>Everything needed to render a (WordId, ReadingIndex) pair in a list.</summary>
public record WordPresentation(
    Dictionary<(int, short), JmDictWordForm> Forms,
    Dictionary<(int, short), JmDictWordFormFrequency> Frequencies,
    Dictionary<int, string?> Definitions)
{
    public string FormText(int wordId, byte readingIndex)
    {
        var form = Forms.GetValueOrDefault((wordId, (short)readingIndex));
        return form?.RubyText ?? form?.Text ?? "";
    }

    public string? Definition(int wordId) => Definitions.GetValueOrDefault(wordId);

    public int FrequencyRank(int wordId, byte readingIndex)
        => Frequencies.GetValueOrDefault((wordId, (short)readingIndex))?.FrequencyRank ?? 0;
}

public static class WordFormHelper
{
    /// <summary>
    /// The one redundancy gate every importer goes through. Picks the cards a sibling form in the same collection
    /// dominates (a kanji form the card is a kana-degradation of, or a script variant), archives the history the
    /// file carried for them so skipping them does not destroy it, and returns them paired with the covering form
    /// so the caller can drop them from its own lists. Does not save.
    /// </summary>
    public static async Task<RedundantImportResult> ArchiveRedundantImportCards(
        UserDbContext userContext, IWordFormSiblingCache cache, string userId,
        IReadOnlyList<FsrsCard> candidates, HashSet<(int WordId, byte ReadingIndex)> collectionKeys,
        Func<FsrsCard, IReadOnlyList<PackedReview>> historyOf, FsrsScheduler? scheduler = null)
    {
        var redundant = FindRedundantImportCards(cache, candidates, GroupCardKeysByWord(collectionKeys));
        if (redundant.Count == 0)
            return new RedundantImportResult(redundant, 0);

        var entries = new List<(FsrsCard Card, IReadOnlyList<PackedReview> Reviews)>();
        foreach (var card in redundant.Keys)
        {
            var reviews = historyOf(card);
            if (reviews.Count == 0)
                continue;

            var replay = scheduler ?? new FsrsScheduler(enableFuzzing: false);
            var logs = ToLogs(reviews);

            if (card.LastReview == null)
                FsrsReplay.Recompute(card, logs, replay, replay);
            else
                card.Lapses = FsrsReplay.CountLapses(logs, replay);

            entries.Add((card, reviews));
        }

        var archived = await CardArchiveService.ArchiveUninsertedCardsAsync(
            userContext, userId, entries, CardArchiveReason.KanaRedundancy,
            c => redundant.TryGetValue(c, out var ri) ? ri : null);

        return new RedundantImportResult(redundant, archived);
    }

    private static List<FsrsReviewLog> ToLogs(IReadOnlyList<PackedReview> reviews)
        => reviews.Select(r => new FsrsReviewLog
                               {
                                   Rating = r.Rating, ReviewDateTime = r.ReviewDateTime, ReviewDuration = r.ReviewDuration
                               })
                  .ToList();

    /// <summary>
    /// The dominating sibling present in <paramref name="cardKeysByWord"/> that makes this form redundant, or
    /// null if none is
    /// </summary>
    private static byte? FindCoveringIndex(
        IWordFormSiblingCache cache, int wordId, byte readingIndex,
        Dictionary<int, List<byte>> cardKeysByWord,
        HashSet<(int WordId, byte ReadingIndex)>? excluded = null)
    {
        var dominators = cache.GetKanjiIndexesForKana(wordId, readingIndex);
        if (dominators == null)
            return null;
        if (!cardKeysByWord.TryGetValue(wordId, out var siblings))
            return null;

        byte? covering = null;
        foreach (var ri in siblings)
        {
            if (!dominators.Contains(ri) || excluded?.Contains((wordId, ri)) == true)
                continue;
            if (covering == null || ri < covering)
                covering = ri;
        }

        return covering;
    }

    /// <summary>
    /// The cards an import must not insert because a sibling form in the same collection dominates them, each
    /// paired with that sibling
    /// </summary>
    private static Dictionary<FsrsCard, byte> FindRedundantImportCards(
        IWordFormSiblingCache cache, IEnumerable<FsrsCard> cards, Dictionary<int, List<byte>> cardKeysByWord)
    {
        var redundant = new Dictionary<FsrsCard, byte>();
        var dropped = new HashSet<(int WordId, byte ReadingIndex)>();

        foreach (var card in cards.OrderBy(c => c.WordId).ThenBy(c => c.ReadingIndex))
        {
            var covering = FindCoveringIndex(cache, card.WordId, card.ReadingIndex, cardKeysByWord, dropped);
            if (covering == null)
                continue;

            redundant[card] = covering.Value;
            dropped.Add((card.WordId, card.ReadingIndex));
        }

        return redundant;
    }

    private static Dictionary<int, List<byte>> GroupCardKeysByWord(
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

    public static List<RedundantFormPair> FindRedundantForms(
        IWordFormSiblingCache cache, int wordId, IReadOnlyList<FsrsCard> cards,
        IReadOnlyDictionary<long, int>? reviewCountsByCardId = null)
    {
        if (cards.Count < 2)
            return [];
        var info = cache.GetWordFormInfo(wordId);
        if (info == null)
            return [];

        var present = cards.Select(c => c.ReadingIndex).ToHashSet();

        int Dominated(byte ri) => info.RedundantBySource.TryGetValue(ri, out var targets)
            ? targets.Count(present.Contains)
            : 0;

        var ordered = cards
            .OrderByDescending(c => Dominated(c.ReadingIndex))
            .ThenByDescending(c => c.State == FsrsState.Mastered)
            .ThenByDescending(c => reviewCountsByCardId?.GetValueOrDefault(c.CardId) ?? 0)
            .ThenBy(c => c.ReadingIndex)
            .ToList();

        var kept = new List<FsrsCard>();
        var redundant = new List<RedundantFormPair>();

        foreach (var card in ordered)
        {
            if (card.State == FsrsState.Blacklisted)
            {
                kept.Add(card);
                continue;
            }

            var dominators = cache.GetKanjiIndexesForKana(wordId, card.ReadingIndex);
            var covering = dominators == null
                ? null
                : kept.FirstOrDefault(k => k.State == FsrsState.Mastered && dominators.Contains(k.ReadingIndex));

            if (covering != null)
                redundant.Add(new RedundantFormPair(card.ReadingIndex, covering.ReadingIndex));
            else
                kept.Add(card);
        }

        return redundant;
    }

    /// <summary>
    /// Deletes the caller's cards that a Mastered sibling form covers, restricted to <paramref name="wordIds"/>.
    /// </summary>
    public static async Task<int> PruneRedundantForms(
        UserDbContext userContext, IWordFormSiblingCache cache, string userId, List<int> wordIds)
    {
        if (wordIds.Count == 0)
            return 0;

        var candidateWordIds = await userContext.FsrsCards
                                                .Where(c => c.UserId == userId && wordIds.Contains(c.WordId))
                                                .GroupBy(c => c.WordId)
                                                .Where(g => g.Count() > 1 && g.Any(c => c.State == FsrsState.Mastered))
                                                .Select(g => g.Key)
                                                .ToListAsync();

        if (candidateWordIds.Count == 0)
            return 0;

        var cards = await userContext.FsrsCards
                                     .Where(c => c.UserId == userId && candidateWordIds.Contains(c.WordId))
                                     .ToListAsync();

        var reviewCounts = await LoadReviewCounts(userContext, cards.Select(c => c.CardId).ToList());

        var toRemove = new List<FsrsCard>();
        var coveringByCardId = new Dictionary<long, byte>();
        foreach (var group in cards.GroupBy(c => c.WordId))
        {
            var byIndex = group.ToDictionary(c => c.ReadingIndex);
            foreach (var pair in FindRedundantForms(cache, group.Key, group.ToList(), reviewCounts))
                if (byIndex.TryGetValue(pair.ReadingIndex, out var card))
                {
                    toRemove.Add(card);
                    coveringByCardId[card.CardId] = pair.CoveringReadingIndex;
                }
        }

        if (toRemove.Count > 0)
        {
            var withHistory = toRemove.Where(c => reviewCounts.GetValueOrDefault(c.CardId) > 0).ToList();
            await CardArchiveService.ArchiveCardsAsync(userContext, userId, withHistory, CardArchiveReason.FormPrune,
                                                       c => CoveringOf(coveringByCardId, c));
            userContext.FsrsCards.RemoveRange(toRemove);
        }

        return toRemove.Count;
    }

    public static Task<int> RemoveRedundantKanaSrsCards(
        UserDbContext userContext, IWordFormSiblingCache cache,
        string userId, int wordId, byte readingIndex)
        => RemoveRedundantKanaSrsCards(userContext, cache, userId, [(wordId, readingIndex)]);

    /// <summary>
    /// Archives and deletes the kana siblings of forms the caller has just marked Mastered
    /// </summary>
    public static async Task<int> RemoveRedundantKanaSrsCards(
        UserDbContext userContext, IWordFormSiblingCache cache, string userId,
        IReadOnlyList<(int WordId, byte ReadingIndex)> masteredForms)
    {
        if (masteredForms.Count == 0)
            return 0;

        var kanaByWord = new Dictionary<int, HashSet<byte>>();
        var anchorsByWord = new Dictionary<int, HashSet<byte>>();

        foreach (var (wordId, readingIndex) in masteredForms)
        {
            var kanaIndexes = cache.GetKanaIndexesForKanji(wordId, readingIndex);
            if (kanaIndexes is not { Length: > 0 })
                continue;

            if (!kanaByWord.TryGetValue(wordId, out var kana))
                kanaByWord[wordId] = kana = [];
            foreach (var kanaRi in kanaIndexes)
                kana.Add(kanaRi);

            if (!anchorsByWord.TryGetValue(wordId, out var anchors))
                anchorsByWord[wordId] = anchors = [];
            anchors.Add(readingIndex);
        }

        if (kanaByWord.Count == 0)
            return 0;

        var wordIds = kanaByWord.Keys.ToList();
        var cards = await userContext.FsrsCards
                                     .Where(c => c.UserId == userId && wordIds.Contains(c.WordId))
                                     .ToListAsync();

        var reviewCounts = await LoadReviewCounts(userContext, cards.Select(c => c.CardId).ToList());

        var toRemove = new List<FsrsCard>();
        var coveringByCardId = new Dictionary<long, byte>();

        foreach (var (wordId, kanaIndexes) in kanaByWord)
        {
            var wordCards = cards.Where(c => c.WordId == wordId).ToList();

            foreach (var anchor in anchorsByWord[wordId])
                if (wordCards.All(c => c.ReadingIndex != anchor))
                    wordCards.Add(new FsrsCard(userId, wordId, anchor, state: FsrsState.Mastered));

            var byIndex = wordCards.ToDictionary(c => c.ReadingIndex);
            foreach (var pair in FindRedundantForms(cache, wordId, wordCards, reviewCounts))
            {
                if (!kanaIndexes.Contains(pair.ReadingIndex))
                    continue;
                if (!byIndex.TryGetValue(pair.ReadingIndex, out var card) || card.CardId == 0)
                    continue;

                toRemove.Add(card);
                coveringByCardId[card.CardId] = pair.CoveringReadingIndex;
            }
        }

        if (toRemove.Count == 0)
            return 0;

        await CardArchiveService.ArchiveCardsAsync(userContext, userId, toRemove, CardArchiveReason.KanaRedundancy,
                                                   c => CoveringOf(coveringByCardId, c));
        userContext.FsrsCards.RemoveRange(toRemove);
        return toRemove.Count;
    }

    private static byte? CoveringOf(Dictionary<long, byte> map, FsrsCard card)
        => map.TryGetValue(card.CardId, out var covering) ? covering : null;

    public static async Task<Dictionary<long, int>> LoadReviewCounts(UserDbContext userContext, List<long> cardIds)
    {
        if (cardIds.Count == 0)
            return [];

        return await userContext.FsrsReviewLogs
                                .Where(l => cardIds.Contains(l.CardId))
                                .GroupBy(l => l.CardId)
                                .Select(g => new { CardId = g.Key, Count = g.Count() })
                                .ToDictionaryAsync(g => g.CardId, g => g.Count);
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

    public static async Task<WordPresentation> LoadWordPresentation(JitenDbContext context, List<int> wordIds)
    {
        var forms = await LoadWordForms(context, wordIds);
        var frequencies = await LoadWordFormFrequencies(context, wordIds);

        var definitions = new Dictionary<int, string?>();
        var rows = await context.Definitions
                                .AsNoTracking()
                                .Where(d => wordIds.Contains(d.WordId))
                                .OrderBy(d => d.WordId).ThenBy(d => d.SenseIndex)
                                .Select(d => new { d.WordId, Meaning = d.EnglishMeanings.FirstOrDefault() })
                                .ToListAsync();

        foreach (var row in rows)
            definitions.TryAdd(row.WordId, row.Meaning);

        return new WordPresentation(forms, frequencies, definitions);
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
