using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Data;
using Jiten.Parser.Data.Redis;
using Jiten.Parser.Scoring;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Parser.Runtime;

internal sealed class ParserRuntime
{
    private readonly SemaphoreSlim _initSemaphore = new(1, 1);
    private bool _initialized;
    private ParserRuntimeSnapshot _snapshot = null!;

    public async Task<ParserRuntimeSnapshot> EnsureInitializedAsync(
        IDbContextFactory<JitenDbContext> contextFactory, Action<string>? log = null)
    {
        if (_initialized)
            return _snapshot;

        await _initSemaphore.WaitAsync();
        try
        {
            if (!_initialized)
            {
                _snapshot = await InitializeAsync(contextFactory, log);
                _initialized = true;
            }

            return _snapshot;
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    private static async Task<ParserRuntimeSnapshot> InitializeAsync(
        IDbContextFactory<JitenDbContext> contextFactory, Action<string>? log = null)
    {
        var runtimeSettings = ParserRuntimeSettings.Current;

        IDeckWordCache deckWordCache = new RedisDeckWordCache(runtimeSettings.Configuration);
        IJmDictCache jmDictCache = new RedisJmDictCache(runtimeSettings.Configuration, contextFactory);

        var overallSw = Stopwatch.StartNew();

        // Sudachi context creation and Deconjugator JSON load are independent of the DB —
        // run them concurrently with the three preload queries so they're free on the critical path.
        var sudachiSw = Stopwatch.StartNew();
        var sudachiWarmupTask = Task.Run(static async () =>
        {
            _ = Deconjugator.Instance;
            await new MorphologicalAnalyser().Parse("食べた");
        });

        var (lookups, wordFrequencyRanks, nameOnlyWordIds, expressionWordIds, wordMeta, wordObservedFrequencies, lookupsMs, freqMs, nameOnlyMs, metaMs) =
            await LoadPreloadDataAsync(contextFactory);
        var dbWallMs = overallSw.ElapsedMilliseconds;

        await sudachiWarmupTask;
        sudachiSw.Stop();

        log?.Invoke(
            $"Warmup phases — " +
            $"lookups: {lookupsMs}ms, freqRanks: {freqMs}ms, nameOnlyIds: {nameOnlyMs}ms, wordMeta: {metaMs}ms " +
            $"(DB wall: {dbWallMs}ms) | sudachi: {sudachiSw.ElapsedMilliseconds}ms " +
            $"(waited {Math.Max(0, sudachiSw.ElapsedMilliseconds - dbWallMs)}ms after DB)");

        // Redis prefill runs in the background — GetWordsAsync has a DB fallback so parsing
        // works correctly even while the cache is still being populated on a cold start.
        _ = Task.Run(() => PrefillRedisCacheAsync(jmDictCache, contextFactory));

        var kanjiBackedWordIds = BuildKanjiBackedWordIds(lookups);

        return new ParserRuntimeSnapshot(deckWordCache, jmDictCache, lookups, wordFrequencyRanks, nameOnlyWordIds, expressionWordIds, wordMeta, wordObservedFrequencies, kanjiBackedWordIds);
    }

    /// Word ids reachable from a lookup key containing at least one kanji — i.e. words that have
    /// a kanji written form. A pure-kana surface matching one of these via reading-key collision
    /// is only plausible when the word is also usually-kana (see Parser.IsKanaAppropriateId).
    private static HashSet<int> BuildKanjiBackedWordIds(Dictionary<string, List<int>> lookups)
    {
        var result = new HashSet<int>();
        foreach (var (key, ids) in lookups)
        {
            if (!KanaScoringHelpers.ContainsKanji(key)) continue;
            foreach (var id in ids)
                result.Add(id);
        }

        return result;
    }

    private static async Task<(Dictionary<string, List<int>> lookups, Dictionary<int, int> wordFrequencyRanks,
        HashSet<int> nameOnlyWordIds, HashSet<int> expressionWordIds, Dictionary<int, JmDictWordMeta> wordMeta,
        Dictionary<int, double> wordObservedFrequencies,
        long lookupsMs, long freqMs, long nameOnlyMs, long metaMs)>
        LoadPreloadDataAsync(IDbContextFactory<JitenDbContext> contextFactory)
    {
        await using var ctx1 = await contextFactory.CreateDbContextAsync();
        await using var ctx2 = await contextFactory.CreateDbContextAsync();
        await using var ctx3 = await contextFactory.CreateDbContextAsync();
        await using var ctx4 = await contextFactory.CreateDbContextAsync();
        await using var ctx5 = await contextFactory.CreateDbContextAsync();
        await using var ctx6 = await contextFactory.CreateDbContextAsync();
        await using var ctx7 = await contextFactory.CreateDbContextAsync();

        long lookupsMs = 0, freqMs = 0, nameOnlyMs = 0, metaMs = 0;
        var sw = Stopwatch.StartNew();

        var t1 = JmDictHelper.LoadLookupTable(ctx1)
            .ContinueWith(t => { lookupsMs = sw.ElapsedMilliseconds; return t.Result; },
                TaskContinuationOptions.ExecuteSynchronously);
        var t2 = JmDictHelper.LoadWordFrequencyRanks(ctx2)
            .ContinueWith(t => { freqMs = sw.ElapsedMilliseconds; return t.Result; },
                TaskContinuationOptions.ExecuteSynchronously);
        var t3 = JmDictHelper.LoadNameOnlyWordIds(ctx3)
            .ContinueWith(t => { nameOnlyMs = sw.ElapsedMilliseconds; return t.Result; },
                TaskContinuationOptions.ExecuteSynchronously);
        var t4 = JmDictHelper.LoadExpressionWordIds(ctx4);
        var t5 = JmDictHelper.LoadWordMetadataRaw(ctx5)
            .ContinueWith(t => { metaMs = sw.ElapsedMilliseconds; return t.Result; },
                TaskContinuationOptions.ExecuteSynchronously);
        var t6 = JmDictHelper.LoadFullyArchaicWordIds(ctx6);
        var t7 = JmDictHelper.LoadWordObservedFrequencies(ctx7);

        await Task.WhenAll(t1, t2, t3, t4, t5, t6, t7);

        var wordMeta = BuildWordMetadata(t5.Result, t6.Result);

        return (t1.Result, t2.Result, t3.Result, t4.Result, wordMeta, t7.Result, lookupsMs, freqMs, nameOnlyMs, metaMs);
    }

    private static Dictionary<int, JmDictWordMeta> BuildWordMetadata(
        List<(int WordId, string[] PartsOfSpeech, string[]? Priorities, WordOrigin Origin)> raw,
        HashSet<int> fullyArchaicIds)
    {
        var result = new Dictionary<int, JmDictWordMeta>(raw.Count);

        foreach (var (wordId, posStrings, priorities, origin) in raw)
        {
            var pos = new PartOfSpeech[posStrings.Length];
            bool hasUk = false;
            bool hasName = false;
            bool hasTrueName = false;
            for (int i = 0; i < posStrings.Length; i++)
            {
                pos[i] = posStrings[i].ToPartOfSpeech();
                if (posStrings[i] == "uk") hasUk = true;
                if (pos[i] == PartOfSpeech.Name)
                {
                    hasName = true;
                    if (posStrings[i] != "unclass") hasTrueName = true;
                }
            }

            int baseScore = ComputePriorityBaseScore(priorities, hasName);
            int ukDelta = hasUk ? 10 : 0;
            bool isArchaic = fullyArchaicIds.Contains(wordId);

            int kanaScore = baseScore + ukDelta;
            int kanjiScore = baseScore - ukDelta;

            if (isArchaic)
            {
                bool hasFreq = HasFrequencyMarker(priorities);
                if (!hasFreq)
                {
                    kanaScore -= 350;
                    kanjiScore -= 350;
                }
            }

            result[wordId] = new JmDictWordMeta(pos,
                (short)Math.Clamp(kanaScore, short.MinValue, short.MaxValue),
                (short)Math.Clamp(kanjiScore, short.MinValue, short.MaxValue),
                origin,
                hasTrueName);
        }

        return result;
    }

    private static int ComputePriorityBaseScore(string[]? priorities, bool hasName)
    {
        int score = 0;
        if (priorities is { Length: > 0 })
        {
            foreach (var p in priorities)
            {
                switch (p)
                {
                    case "jiten": score += 100; break;
                    case "ichi1": score += 20; break;
                    case "ichi2": score += 10; break;
                    case "news1": score += 15; break;
                    case "news2": score += 10; break;
                    case "gai1": score += 15; break;
                    case "gai2": score += 10; break;
                    default:
                        if (p.Length > 2 && p[0] == 'n' && p[1] == 'f')
                        {
                            if (int.TryParse(p.AsSpan(2), out int nfRank))
                                score += Math.Max(0, 5 - (int)Math.Round(nfRank / 10f));
                        }
                        break;
                }
            }

            if (score == 0)
            {
                foreach (var p in priorities)
                {
                    switch (p)
                    {
                        case "spec1": score += 15; break;
                        case "spec2": score += 5; break;
                    }
                }
            }
        }

        if (hasName) score -= 50;
        return score;
    }

    private static bool HasFrequencyMarker(string[]? priorities)
    {
        if (priorities == null) return false;
        foreach (var p in priorities)
            if (p is "ichi1" or "ichi2" or "news1" or "news2" or "gai1" or "gai2" or "spec1" or "spec2"
                || (p.Length > 2 && p[0] == 'n' && p[1] == 'f'))
                return true;
        return false;
    }

    private static async Task PrefillRedisCacheAsync(IJmDictCache jmDictCache, IDbContextFactory<JitenDbContext> contextFactory)
    {
        try
        {
            if (await jmDictCache.IsCacheInitializedAsync())
                return;

            await using var ctx = await contextFactory.CreateDbContextAsync();

            // Pre-compute the archaic flag from a small targeted query (only arch-tagged words + their def POS).
            // This avoids loading all 215K definitions into memory just to strip them immediately after.
            var fullyArchaicIds = await JmDictHelper.LoadFullyArchaicWordIds(ctx);

            // Stream words in small batches WITHOUT definitions (~2-3GB savings vs. LoadAllWords).
            // ComputeArchaicFlag in SetWordsAsync respects IsFullyArchaic when Definitions is empty.
            await JmDictHelper.StreamWordBatchesAsync(ctx, 2000, async batch =>
            {
                foreach (var word in batch)
                    word.IsFullyArchaic = fullyArchaicIds.Contains(word.WordId);

                await jmDictCache.SetWordsAsync(batch.ToDictionary(w => w.WordId, w => w));
            });

            await jmDictCache.SetCacheInitializedAsync();
        }
        catch
        {
            // Non-fatal: GetWordsAsync falls back to the database on cache misses.
        }
    }
}

internal sealed class ParserRuntimeSnapshot(
    IDeckWordCache deckWordCache,
    IJmDictCache jmDictCache,
    Dictionary<string, List<int>> lookups,
    Dictionary<int, int> wordFrequencyRanks,
    HashSet<int> nameOnlyWordIds,
    HashSet<int> expressionWordIds,
    Dictionary<int, JmDictWordMeta> wordMeta,
    Dictionary<int, double> wordObservedFrequencies,
    HashSet<int> kanjiBackedWordIds)
{
    public IDeckWordCache DeckWordCache { get; } = deckWordCache;
    public IJmDictCache JmDictCache { get; } = jmDictCache;
    public Dictionary<string, List<int>> Lookups { get; } = lookups;
    public Dictionary<int, int> WordFrequencyRanks { get; } = wordFrequencyRanks;
    public HashSet<int> NameOnlyWordIds { get; } = nameOnlyWordIds;
    public HashSet<int> ExpressionWordIds { get; } = expressionWordIds;
    public Dictionary<int, JmDictWordMeta> WordMeta { get; } = wordMeta;
    public Dictionary<int, double> WordObservedFrequencies { get; } = wordObservedFrequencies;
    public HashSet<int> KanjiBackedWordIds { get; } = kanjiBackedWordIds;
}
