using System.Text.Json;
using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public class DeckWordResolver(JitenDbContext context, UserDbContext userContext, ICurrentUserService currentUserService, IWordFormSiblingCache wordFormCache) : IDeckWordResolver
{
    public async Task<(List<DeckWord>? Words, IResult? Error)> ResolveDeckWords(DeckWordResolveRequest request)
    {
        var (deckId, deck, downloadType, order, minFrequency, maxFrequency,
            excludeMatureMasteredBlacklisted, excludeAllTrackedWords,
            targetPercentage, minOccurrences, maxOccurrences, posFilter, startFromKnown) = request;

        IQueryable<DeckWord> deckWordsQuery = context.DeckWords.AsNoTracking().Where(dw => dw.DeckId == deckId);

        if (!string.IsNullOrEmpty(posFilter))
        {
            var posTags = JsonSerializer.Deserialize<string[]>(posFilter);
            if (posTags is { Length: > 0 })
            {
                var wordIdsWithPos = context.JMDictWords.AsNoTracking()
                    .Where(w => w.PartsOfSpeech.Any(p => posTags.Contains(p)));
                deckWordsQuery = deckWordsQuery.Where(dw => wordIdsWithPos.Any(w => w.WordId == dw.WordId));
            }
        }

        List<DeckWord>? deckWordsRaw = null;

        switch (downloadType)
        {
            case DeckDownloadType.Full:
                break;

            case DeckDownloadType.TopGlobalFrequency:
                deckWordsQuery = deckWordsQuery.Where(dw => context.WordFormFrequencies
                                                                   .Any(wff => wff.WordId == dw.WordId &&
                                                                               wff.ReadingIndex == (short)dw.ReadingIndex &&
                                                                               wff.FrequencyRank >= minFrequency &&
                                                                               wff.FrequencyRank <= maxFrequency));
                break;

            case DeckDownloadType.TopDeckFrequency:
                deckWordsQuery = deckWordsQuery
                                 .OrderByDescending(dw => dw.Occurrences)
                                 .Skip(minFrequency)
                                 .Take(maxFrequency - minFrequency);
                break;

            case DeckDownloadType.TopChronological:
                deckWordsQuery = deckWordsQuery
                                 .OrderBy(dw => dw.DeckWordId)
                                 .Skip(minFrequency)
                                 .Take(maxFrequency - minFrequency);
                break;

            case DeckDownloadType.TargetCoverage:
                if (!currentUserService.IsAuthenticated)
                    return (null, Results.Unauthorized());

                if (targetPercentage is null or < 1 or > 100)
                    return (null, Results.BadRequest("Target percentage must be between 1 and 100"));

                var allDeckWordsForCoverage = await deckWordsQuery
                                                    .OrderByDescending(dw => dw.Occurrences)
                                                    .ToListAsync();

                var coverageWordKeys = allDeckWordsForCoverage
                                       .Select(dw => (dw.WordId, dw.ReadingIndex))
                                       .ToList();

                var coverageStates = await currentUserService.GetKnownWordsState(coverageWordKeys);

                var knownKeysSet = coverageStates
                                   .Where(kvp => kvp.Value.Any(s => s is KnownState.Mastered or KnownState.Blacklisted
                                                                   or KnownState.Mature))
                                   .Select(kvp => WordFormHelper.EncodeWordKey(kvp.Key.WordId, kvp.Key.ReadingIndex))
                                   .ToHashSet();

                int totalOccurrences = deck.WordCount;
                double targetCoverage = targetPercentage.Value;

                var resultWords = CollectCoverageWords(
                    allDeckWordsForCoverage, knownKeysSet, totalOccurrences, targetCoverage, startFromKnown);

                if (order == DeckOrder.Chronological)
                {
                    deckWordsRaw = resultWords.OrderBy(dw => dw.DeckWordId).ToList();
                }
                else if (order == DeckOrder.GlobalFrequency)
                {
                    var resultWordIds = resultWords.Select(dw => dw.WordId).Distinct().ToList();
                    var freqMap = await WordFormHelper.LoadWordFormFrequencies(context, resultWordIds);

                    deckWordsRaw = resultWords.OrderBy(dw =>
                                                           freqMap.TryGetValue((dw.WordId, (short)dw.ReadingIndex), out var wff)
                                                               ? wff.FrequencyRank
                                                               : int.MaxValue
                                                      ).ToList();
                }
                else
                {
                    deckWordsRaw = resultWords;
                }

                break;

            case DeckDownloadType.OccurrenceCount:
                if (minOccurrences.HasValue)
                    deckWordsQuery = deckWordsQuery.Where(dw => dw.Occurrences >= minOccurrences.Value);
                if (maxOccurrences.HasValue)
                    deckWordsQuery = deckWordsQuery.Where(dw => dw.Occurrences <= maxOccurrences.Value);
                break;

            default:
                return (null, Results.BadRequest());
        }

        if (deckWordsRaw == null)
        {
            switch (order)
            {
                case DeckOrder.Chronological:
                    deckWordsQuery = deckWordsQuery.OrderBy(dw => dw.DeckWordId);
                    break;

                case DeckOrder.GlobalFrequency:
                    deckWordsQuery = deckWordsQuery.OrderBy(dw => context.WordFormFrequencies
                                                                         .Where(wff => wff.WordId == dw.WordId &&
                                                                                       wff.ReadingIndex == (short)dw.ReadingIndex)
                                                                         .Select(wff => wff.FrequencyRank)
                                                                         .FirstOrDefault()
                                                           );
                    break;

                case DeckOrder.DeckFrequency:
                    deckWordsQuery = deckWordsQuery.OrderByDescending(dw => dw.Occurrences);
                    break;
                case DeckOrder.Random:
                    deckWordsRaw = await deckWordsQuery.ToListAsync();
                    var rng = Random.Shared;
                    for (var i = deckWordsRaw.Count - 1; i > 0; i--)
                    {
                        var j = rng.Next(i + 1);
                        (deckWordsRaw[i], deckWordsRaw[j]) = (deckWordsRaw[j], deckWordsRaw[i]);
                    }
                    break;
                default:
                    return (null, Results.BadRequest());
            }

            deckWordsRaw = await deckWordsQuery.ToListAsync();
        }

        if ((excludeMatureMasteredBlacklisted || excludeAllTrackedWords) && currentUserService.IsAuthenticated)
        {
            var wordKeys = deckWordsRaw.Select(dw => (dw.WordId, dw.ReadingIndex)).ToList();
            var knownStates = await currentUserService.GetKnownWordsState(wordKeys);

            deckWordsRaw = deckWordsRaw
                .Where(dw => !ShouldExcludeWord((dw.WordId, dw.ReadingIndex), knownStates,
                    excludeMatureMasteredBlacklisted, excludeAllTrackedWords))
                .ToList();
        }

        return (deckWordsRaw, null);
    }

    public async Task<HashSet<long>> GetStudyDeckWordKeys(List<int> deckIds)
    {
        var keys = await context.DeckWords
            .AsNoTracking()
            .Where(dw => deckIds.Contains(dw.DeckId))
            .Select(dw => ((long)dw.WordId << 8) | dw.ReadingIndex)
            .Distinct()
            .ToListAsync();

        return keys.ToHashSet();
    }

    public async Task<HashSet<long>> GetStaticDeckWordKeys(List<int> studyDeckIds)
    {
        var keys = await userContext.UserStudyDeckWords
            .AsNoTracking()
            .Where(w => studyDeckIds.Contains(w.UserStudyDeckId))
            .Select(w => ((long)w.WordId << 8) | (long)w.ReadingIndex)
            .Distinct()
            .ToListAsync();

        return keys.ToHashSet();
    }

    private IQueryable<JmDictWordFormFrequency> BuildGlobalFrequencyQuery(int? minFreq, int? maxFreq, string? posFilter)
    {
        var query = context.WordFormFrequencies.AsNoTracking();

        if (minFreq.HasValue)
            query = query.Where(wff => wff.FrequencyRank >= minFreq.Value);
        if (maxFreq.HasValue)
            query = query.Where(wff => wff.FrequencyRank <= maxFreq.Value);

        if (!string.IsNullOrEmpty(posFilter))
        {
            var posTags = JsonSerializer.Deserialize<string[]>(posFilter);
            if (posTags is { Length: > 0 })
            {
                var wordIdsWithPos = context.JMDictWords.AsNoTracking()
                    .Where(w => w.PartsOfSpeech.Any(p => posTags.Contains(p)));
                query = query.Where(wff => wordIdsWithPos.Any(w => w.WordId == wff.WordId));
            }
        }

        return query;
    }

    public async Task<GlobalDynamicResult> ResolveGlobalDynamicWords(int? minFreq, int? maxFreq, string? posFilter,
        bool excludeKana, bool excludeMatureMasteredBlacklisted, bool excludeAllTrackedWords)
    {
        var query = BuildGlobalFrequencyQuery(minFreq, maxFreq, posFilter);

        if (excludeKana)
            query = query.Where(wff => context.WordForms
                .Any(wf => wf.WordId == wff.WordId && wf.ReadingIndex == wff.ReadingIndex && wf.FormType != JmDictFormType.KanaForm));

        const int maxResults = 500_000;

        var excludedKeys = await BuildExcludedWordKeys(excludeMatureMasteredBlacklisted, excludeAllTrackedWords);

        var words = await query
            .OrderBy(wff => wff.FrequencyRank)
            .Take(maxResults + 1)
            .Select(wff => new ResolvedWord
            {
                WordId = wff.WordId,
                ReadingIndex = (byte)wff.ReadingIndex,
                Occurrences = 1,
                SortOrder = wff.FrequencyRank
            })
            .ToListAsync();

        var wasTruncated = words.Count > maxResults;
        if (wasTruncated)
            words = words.Take(maxResults).ToList();

        if (excludedKeys.Count > 0)
        {
            words = words
                .Where(w => !excludedKeys.Contains(WordFormHelper.EncodeWordKey(w.WordId, w.ReadingIndex)))
                .ToList();
        }

        return new GlobalDynamicResult(words, wasTruncated);
    }

    public async Task<List<ResolvedWord>> ResolveStaticDeckWords(int studyDeckId, int order,
        bool excludeMatureMasteredBlacklisted = false, bool excludeAllTrackedWords = false,
        DeckDownloadType downloadType = DeckDownloadType.Full,
        int minFrequency = 0, int maxFrequency = 0,
        int? minOccurrences = null, int? maxOccurrences = null,
        float? targetPercentage = null, bool startFromKnown = false)
    {
        var words = await userContext.UserStudyDeckWords
            .AsNoTracking()
            .Where(w => w.UserStudyDeckId == studyDeckId)
            .Select(w => new ResolvedWord
            {
                WordId = w.WordId,
                ReadingIndex = (byte)w.ReadingIndex,
                Occurrences = w.Occurrences,
                SortOrder = w.SortOrder
            })
            .ToListAsync();

        if (words.Count == 0) return words;

        // Global frequency ranks live in the jiten context, so filtering/sorting on them happens in memory.
        Dictionary<(int, short), JmDictWordFormFrequency>? freqMap = null;
        if (downloadType == DeckDownloadType.TopGlobalFrequency || order == (int)DeckOrder.GlobalFrequency)
        {
            var wordIds = words.Select(w => w.WordId).Distinct().ToList();
            freqMap = await WordFormHelper.LoadWordFormFrequencies(context, wordIds);
        }

        switch (downloadType)
        {
            case DeckDownloadType.Full:
                break;

            case DeckDownloadType.TopGlobalFrequency:
                words = words.Where(w => freqMap!.TryGetValue((w.WordId, (short)w.ReadingIndex), out var f) &&
                                         f.FrequencyRank >= minFrequency && f.FrequencyRank <= maxFrequency)
                             .ToList();
                break;

            case DeckDownloadType.TopDeckFrequency:
                words = words.OrderByDescending(w => w.Occurrences)
                             .Skip(minFrequency)
                             .Take(Math.Max(0, maxFrequency - minFrequency))
                             .ToList();
                break;

            case DeckDownloadType.TopChronological:
                words = words.OrderBy(w => w.SortOrder)
                             .Skip(minFrequency)
                             .Take(Math.Max(0, maxFrequency - minFrequency))
                             .ToList();
                break;

            case DeckDownloadType.OccurrenceCount:
                if (minOccurrences.HasValue)
                    words = words.Where(w => w.Occurrences >= minOccurrences.Value).ToList();
                if (maxOccurrences.HasValue)
                    words = words.Where(w => w.Occurrences <= maxOccurrences.Value).ToList();
                break;

            case DeckDownloadType.TargetCoverage:
            {
                if (targetPercentage is null or < 1 or > 100) return [];

                var byOccurrences = words.OrderByDescending(w => w.Occurrences).ToList();
                var keysWithOccurrences = byOccurrences
                    .Select(w => (Key: WordFormHelper.EncodeWordKey(w.WordId, w.ReadingIndex), w.Occurrences))
                    .ToList();

                HashSet<long>? knownKeys = null;
                if (currentUserService.IsAuthenticated)
                {
                    var states = await currentUserService.GetKnownWordsState(
                        byOccurrences.Select(w => (w.WordId, w.ReadingIndex)).ToList());
                    knownKeys = states
                        .Where(kvp => kvp.Value.Any(s => s is KnownState.Mastered or KnownState.Blacklisted or KnownState.Mature))
                        .Select(kvp => WordFormHelper.EncodeWordKey(kvp.Key.WordId, kvp.Key.ReadingIndex))
                        .ToHashSet();
                }

                var totalOccurrences = byOccurrences.Sum(w => w.Occurrences);
                var selected = CollectCoverageKeys(keysWithOccurrences, knownKeys, totalOccurrences,
                    targetPercentage.Value, startFromKnown);
                words = byOccurrences
                    .Where(w => selected.Contains(WordFormHelper.EncodeWordKey(w.WordId, w.ReadingIndex)))
                    .ToList();
                break;
            }

            default:
                return [];
        }

        if (order == (int)DeckOrder.GlobalFrequency)
        {
            words.Sort((a, b) =>
            {
                var rankA = freqMap!.TryGetValue((a.WordId, a.ReadingIndex), out var fa) ? fa.FrequencyRank : int.MaxValue;
                var rankB = freqMap!.TryGetValue((b.WordId, b.ReadingIndex), out var fb) ? fb.FrequencyRank : int.MaxValue;
                return rankA.CompareTo(rankB);
            });
        }
        else if (order == (int)DeckOrder.DeckFrequency)
        {
            words = words.OrderByDescending(w => w.Occurrences).ToList();
        }
        else if (order == (int)DeckOrder.Random)
        {
            var rng = Random.Shared;
            for (var i = words.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (words[i], words[j]) = (words[j], words[i]);
            }
        }
        else
        {
            words = words.OrderBy(w => w.SortOrder).ToList();
        }

        return FilterExcludedWords(words, await BuildExcludedWordKeys(excludeMatureMasteredBlacklisted, excludeAllTrackedWords));
    }

    public async Task<HashSet<long>> GetGlobalDynamicWordKeys(int? minFreq, int? maxFreq, string? posFilter)
    {
        var query = BuildGlobalFrequencyQuery(minFreq, maxFreq, posFilter);

        var keys = await query
            .Select(wff => ((long)wff.WordId << 8) | (byte)wff.ReadingIndex)
            .Distinct()
            .ToListAsync();

        return keys.ToHashSet();
    }

    public async Task<HashSet<long>> GetGlobalDynamicWordKeysForWordIds(int? minFreq, int? maxFreq, string? posFilter, List<int> wordIds, bool excludeKana = false)
    {
        if (wordIds.Count == 0) return [];

        var query = BuildGlobalFrequencyQuery(minFreq, maxFreq, posFilter)
            .Where(wff => wordIds.Contains(wff.WordId));

        if (excludeKana)
            query = query.Where(wff => context.WordForms
                .Any(wf => wf.WordId == wff.WordId && wf.ReadingIndex == wff.ReadingIndex && wf.FormType != JmDictFormType.KanaForm));

        var keys = await query
            .Select(wff => ((long)wff.WordId << 8) | (byte)wff.ReadingIndex)
            .Distinct()
            .ToListAsync();

        return keys.ToHashSet();
    }

    public async Task<(int Count, bool WasTruncated)> CountGlobalDynamicWords(int? minFreq, int? maxFreq, string? posFilter, bool excludeKana,
        bool excludeMatureMasteredBlacklisted = false, bool excludeAllTrackedWords = false)
    {
        var query = BuildGlobalFrequencyQuery(minFreq, maxFreq, posFilter);

        if (excludeKana)
            query = query.Where(wff => context.WordForms
                .Any(wf => wf.WordId == wff.WordId && wf.ReadingIndex == wff.ReadingIndex && wf.FormType != JmDictFormType.KanaForm));

        var excludedKeys = await BuildExcludedWordKeys(excludeMatureMasteredBlacklisted, excludeAllTrackedWords);

        if (excludedKeys.Count > 0)
        {
            const int maxResults = 500_000;
            var words = await query
                .OrderBy(wff => wff.FrequencyRank)
                .Take(maxResults + 1)
                .Select(wff => new { wff.WordId, ReadingIndex = (byte)wff.ReadingIndex })
                .ToListAsync();

            var wasTruncated = words.Count > maxResults;
            if (wasTruncated)
                words = words.Take(maxResults).ToList();

            var count = words.Count(w => !excludedKeys.Contains(WordFormHelper.EncodeWordKey(w.WordId, w.ReadingIndex)));
            return (count, wasTruncated);
        }

        return (await query.CountAsync(), false);
    }

    public async Task<(int Count, HashSet<long> WordKeys)> CountDeckWords(DeckWordResolveRequest request, bool excludeKana)
    {
        var (deckId, deck, downloadType, order, minFrequency, maxFrequency,
            excludeMatureMasteredBlacklisted, excludeAllTrackedWords,
            targetPercentage, minOccurrences, maxOccurrences, posFilter, startFromKnown) = request;

        IQueryable<DeckWord> query = context.DeckWords.AsNoTracking().Where(dw => dw.DeckId == deckId);

        switch (downloadType)
        {
            case DeckDownloadType.Full:
                break;
            case DeckDownloadType.TopGlobalFrequency:
                query = query.Where(dw => context.WordFormFrequencies
                    .Any(wff => wff.WordId == dw.WordId &&
                                wff.ReadingIndex == (short)dw.ReadingIndex &&
                                wff.FrequencyRank >= minFrequency &&
                                wff.FrequencyRank <= maxFrequency));
                break;
            case DeckDownloadType.TopDeckFrequency:
                query = query.OrderByDescending(dw => dw.Occurrences)
                             .Skip(minFrequency)
                             .Take(maxFrequency - minFrequency);
                break;
            case DeckDownloadType.TopChronological:
                query = query.OrderBy(dw => dw.DeckWordId)
                             .Skip(minFrequency)
                             .Take(maxFrequency - minFrequency);
                break;
            case DeckDownloadType.OccurrenceCount:
                if (minOccurrences.HasValue)
                    query = query.Where(dw => dw.Occurrences >= minOccurrences.Value);
                if (maxOccurrences.HasValue)
                    query = query.Where(dw => dw.Occurrences <= maxOccurrences.Value);
                break;
            default:
                return (0, []);
        }

        if (excludeKana)
            query = query.Where(dw => context.WordForms
                .Any(wf => wf.WordId == dw.WordId && wf.ReadingIndex == (short)dw.ReadingIndex && wf.FormType != JmDictFormType.KanaForm));

        if (!string.IsNullOrEmpty(posFilter))
        {
            var posTags = JsonSerializer.Deserialize<string[]>(posFilter);
            if (posTags is { Length: > 0 })
            {
                var wordIdsWithPos = context.JMDictWords.AsNoTracking()
                    .Where(w => w.PartsOfSpeech.Any(p => posTags.Contains(p)));
                query = query.Where(dw => wordIdsWithPos.Any(w => w.WordId == dw.WordId));
            }
        }

        var pairs = await query
            .Select(dw => new { dw.WordId, dw.ReadingIndex })
            .ToListAsync();

        if ((excludeMatureMasteredBlacklisted || excludeAllTrackedWords) && currentUserService.IsAuthenticated)
        {
            var wordKeys = pairs.Select(p => (p.WordId, p.ReadingIndex)).ToList();
            var knownStates = await currentUserService.GetKnownWordsState(wordKeys);

            pairs = pairs
                .Where(p => !ShouldExcludeWord((p.WordId, p.ReadingIndex), knownStates,
                    excludeMatureMasteredBlacklisted, excludeAllTrackedWords))
                .ToList();
        }

        var keySet = pairs.Select(p => WordFormHelper.EncodeWordKey(p.WordId, p.ReadingIndex)).ToHashSet();
        return (keySet.Count, keySet);
    }

    private static bool ShouldExcludeWord(
        (int WordId, byte ReadingIndex) key,
        Dictionary<(int WordId, byte ReadingIndex), List<KnownState>> knownStates,
        bool excludeMatureMasteredBlacklisted,
        bool excludeAllTrackedWords)
    {
        if (!knownStates.TryGetValue(key, out var states))
            return false;
        if (excludeAllTrackedWords && states.Any(s => s != KnownState.New))
            return true;
        if (excludeMatureMasteredBlacklisted &&
            states.Any(s => s is KnownState.Mastered or KnownState.Blacklisted or KnownState.Mature))
            return true;
        return false;
    }

    public async Task<(int Count, HashSet<long> WordKeys)> CountTargetCoverageWords(int deckId, Deck deck, float targetPercentage, bool excludeKana, string? posFilter = null, bool startFromKnown = false)
    {
        if (!currentUserService.IsAuthenticated)
            return (0, []);

        IQueryable<DeckWord> deckWordsQuery = context.DeckWords.AsNoTracking()
            .Where(dw => dw.DeckId == deckId);

        if (!string.IsNullOrEmpty(posFilter))
        {
            var posTags = JsonSerializer.Deserialize<string[]>(posFilter);
            if (posTags is { Length: > 0 })
            {
                var wordIdsWithPos = context.JMDictWords.AsNoTracking()
                    .Where(w => w.PartsOfSpeech.Any(p => posTags.Contains(p)));
                deckWordsQuery = deckWordsQuery.Where(dw => wordIdsWithPos.Any(w => w.WordId == dw.WordId));
            }
        }

        var allDeckWords = await deckWordsQuery
            .OrderByDescending(dw => dw.Occurrences)
            .Select(dw => new { dw.WordId, dw.ReadingIndex, dw.Occurrences })
            .ToListAsync();

        int totalOccurrences = deck.WordCount;

        var keysWithOccurrences = allDeckWords
            .Select(dw => (Key: WordFormHelper.EncodeWordKey(dw.WordId, dw.ReadingIndex), dw.Occurrences))
            .ToList();

        HashSet<long>? knownKeysSet = null;
        if (startFromKnown)
        {
            var coverageWordKeys = allDeckWords.Select(dw => (dw.WordId, dw.ReadingIndex)).ToList();
            var coverageStates = await currentUserService.GetKnownWordsState(coverageWordKeys);
            knownKeysSet = coverageStates
                .Where(kvp => kvp.Value.Any(s => s is KnownState.Mastered or KnownState.Blacklisted or KnownState.Mature))
                .Select(kvp => WordFormHelper.EncodeWordKey(kvp.Key.WordId, kvp.Key.ReadingIndex))
                .ToHashSet();
        }

        var resultKeys = CollectCoverageKeys(keysWithOccurrences, knownKeysSet, totalOccurrences, targetPercentage, startFromKnown);

        if (excludeKana)
        {
            var wordIds = resultKeys.Select(k => (int)(k >> 8)).Distinct();
            var kanaFormKeys = await WordFormHelper.GetKanaFormKeys(context, wordIds);
            if (kanaFormKeys.Count > 0)
                resultKeys.RemoveWhere(k => kanaFormKeys.Contains(k));
        }

        return (resultKeys.Count, resultKeys);
    }

    public async Task<(int Count, HashSet<long> WordKeys)> CountStaticDeckWords(int studyDeckId, bool excludeKana,
        bool excludeMatureMasteredBlacklisted = false, bool excludeAllTrackedWords = false)
    {
        IQueryable<UserStudyDeckWord> query = userContext.UserStudyDeckWords
            .AsNoTracking()
            .Where(w => w.UserStudyDeckId == studyDeckId);

        if (excludeKana)
            query = query.Where(w => context.WordForms
                .Any(wf => wf.WordId == w.WordId && wf.ReadingIndex == w.ReadingIndex && wf.FormType != JmDictFormType.KanaForm));

        var pairs = await query
            .Select(w => new { w.WordId, w.ReadingIndex })
            .ToListAsync();

        var keySet = pairs.Select(p => WordFormHelper.EncodeWordKey(p.WordId, p.ReadingIndex)).ToHashSet();

        var excludedKeys = await BuildExcludedWordKeys(excludeMatureMasteredBlacklisted, excludeAllTrackedWords);
        if (excludedKeys.Count > 0)
            keySet.ExceptWith(excludedKeys);

        return (keySet.Count, keySet);
    }

    private static List<ResolvedWord> FilterExcludedWords(List<ResolvedWord> words, HashSet<long> excludedKeys)
    {
        if (excludedKeys.Count == 0) return words;
        return words.Where(w => !excludedKeys.Contains(WordFormHelper.EncodeWordKey(w.WordId, w.ReadingIndex))).ToList();
    }

    private async Task<HashSet<long>> BuildExcludedWordKeys(bool excludeMatureMasteredBlacklisted, bool excludeAllTrackedWords)
    {
        if ((!excludeMatureMasteredBlacklisted && !excludeAllTrackedWords) || !currentUserService.IsAuthenticated)
            return [];

        var userId = currentUserService.UserId!;
        var excluded = new HashSet<long>();

        IQueryable<FsrsCard> cardQuery = userContext.FsrsCards.AsNoTracking()
            .Where(c => c.UserId == userId);

        if (excludeMatureMasteredBlacklisted && !excludeAllTrackedWords)
        {
            cardQuery = cardQuery.Where(c =>
                c.State == FsrsState.Mastered ||
                c.State == FsrsState.Blacklisted ||
                (c.LastReview != null && c.Due >= c.LastReview.Value.AddDays(21)));
        }

        var cards = await cardQuery
            .Select(c => new { c.WordId, c.ReadingIndex })
            .ToListAsync();

        foreach (var c in cards)
            excluded.Add(WordFormHelper.EncodeWordKey(c.WordId, (byte)c.ReadingIndex));

        var setStatesQuery = userContext.UserWordSetStates
            .AsNoTracking()
            .Where(uwss => uwss.UserId == userId);

        if (excludeMatureMasteredBlacklisted && !excludeAllTrackedWords)
            setStatesQuery = setStatesQuery.Where(s => s.State == WordSetStateType.Mastered || s.State == WordSetStateType.Blacklisted);

        var relevantSetIds = await setStatesQuery
            .Select(s => s.SetId)
            .ToListAsync();

        if (relevantSetIds.Count > 0)
        {
            var members = await context.WordSetMembers
                .AsNoTracking()
                .Where(wsm => relevantSetIds.Contains(wsm.SetId))
                .Select(wsm => new { wsm.WordId, wsm.ReadingIndex })
                .ToListAsync();

            foreach (var m in members)
            {
                excluded.Add(WordFormHelper.EncodeWordKey(m.WordId, m.ReadingIndex));
            }
        }

        WordFormHelper.ExpandKanaRedundancyKeys(wordFormCache,
            cards.Select(c => (c.WordId, (byte)c.ReadingIndex)),
            excluded);

        return excluded;
    }

    private static List<DeckWord> CollectCoverageWords(
        List<DeckWord> allWords, HashSet<long> knownKeysSet,
        int totalOccurrences, double targetPercentage, bool startFromKnown)
    {
        var keysWithOccurrences = allWords
            .Select(dw => (Key: WordFormHelper.EncodeWordKey(dw.WordId, dw.ReadingIndex), dw.Occurrences))
            .ToList();

        var selectedKeys = CollectCoverageKeys(keysWithOccurrences, knownKeysSet, totalOccurrences, (float)targetPercentage, startFromKnown);

        var result = new List<DeckWord>(selectedKeys.Count);
        for (int i = 0; i < allWords.Count && result.Count < selectedKeys.Count; i++)
        {
            var key = keysWithOccurrences[i].Key;
            if (selectedKeys.Contains(key))
                result.Add(allWords[i]);
        }
        return result;
    }

    private static HashSet<long> CollectCoverageKeys(
        List<(long Key, int Occurrences)> items, HashSet<long>? knownKeysSet,
        int totalOccurrences, float targetPercentage, bool startFromKnown)
    {
        var result = new HashSet<long>();

        if (startFromKnown && knownKeysSet != null)
        {
            int knownOccurrences = 0;
            foreach (var (key, occ) in items)
            {
                if (knownKeysSet.Contains(key))
                    knownOccurrences += occ;
            }

            int cumulative = knownOccurrences;
            foreach (var (key, occ) in items)
            {
                if (knownKeysSet.Contains(key))
                    continue;

                result.Add(key);
                cumulative += occ;
                if ((double)cumulative / totalOccurrences * 100 >= targetPercentage)
                    break;
            }
        }
        else
        {
            int cumulative = 0;
            foreach (var (key, occ) in items)
            {
                cumulative += occ;
                if (knownKeysSet == null || !knownKeysSet.Contains(key))
                    result.Add(key);
                if ((double)cumulative / totalOccurrences * 100 >= targetPercentage)
                    break;
            }
        }

        return result;
    }
}
