using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Data.Redis;
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

        var (lookups, wordFrequencyRanks, nameOnlyWordIds, expressionWordIds, lookupsMs, freqMs, nameOnlyMs) =
            await LoadPreloadDataAsync(contextFactory);
        var dbWallMs = overallSw.ElapsedMilliseconds;

        await sudachiWarmupTask;
        sudachiSw.Stop();

        log?.Invoke(
            $"Warmup phases — " +
            $"lookups: {lookupsMs}ms, freqRanks: {freqMs}ms, nameOnlyIds: {nameOnlyMs}ms " +
            $"(DB wall: {dbWallMs}ms) | sudachi: {sudachiSw.ElapsedMilliseconds}ms " +
            $"(waited {Math.Max(0, sudachiSw.ElapsedMilliseconds - dbWallMs)}ms after DB)");

        // Redis prefill runs in the background — GetWordsAsync has a DB fallback so parsing
        // works correctly even while the cache is still being populated on a cold start.
        _ = Task.Run(() => PrefillRedisCacheAsync(jmDictCache, contextFactory));

        // Non-archaic POS map — Redis-backed. Tries Redis first (fast); falls back to
        // a single DB query and writes the result back. Runs synchronously on the
        // critical path so IchiranPropScorer has the override BEFORE any parsing starts.
        var napSw = Stopwatch.StartNew();
        var cachedMap = await jmDictCache.GetNonArchaicPosMapAsync();
        if (cachedMap != null)
        {
            Scoring.IchiranPropScorer.NonArchaicPosOverride = cachedMap;
            log?.Invoke($"NonArchaicPos map: {cachedMap.Count} words from Redis in {napSw.ElapsedMilliseconds}ms");
        }
        else
        {
            try
            {
                await using var napCtx = await contextFactory.CreateDbContextAsync();
                var map = await JmDictHelper.LoadNonArchaicPosMapAsync(napCtx);
                Scoring.IchiranPropScorer.NonArchaicPosOverride = map;
                _ = Task.Run(() => jmDictCache.SetNonArchaicPosMapAsync(map));
                log?.Invoke($"NonArchaicPos map: {map.Count} words from DB in {napSw.ElapsedMilliseconds}ms");
            }
            catch
            {
                // Non-fatal: scorer falls back to word.PartsOfSpeech.
            }
        }

        var ambSw = Stopwatch.StartNew();
        var ambiguousSurfaces = await BuildAmbiguousSurfacesAsync(lookups, jmDictCache, contextFactory);
        log?.Invoke($"Ambiguous surfaces: {ambiguousSurfaces.Count} in {ambSw.ElapsedMilliseconds}ms");

        return new ParserRuntimeSnapshot(deckWordCache, jmDictCache, lookups, wordFrequencyRanks, nameOnlyWordIds, expressionWordIds, ambiguousSurfaces);
    }

    private static async Task<(Dictionary<string, List<int>> lookups, Dictionary<int, int> wordFrequencyRanks,
        HashSet<int> nameOnlyWordIds, HashSet<int> expressionWordIds, long lookupsMs, long freqMs, long nameOnlyMs)>
        LoadPreloadDataAsync(IDbContextFactory<JitenDbContext> contextFactory)
    {
        await using var ctx1 = await contextFactory.CreateDbContextAsync();
        await using var ctx2 = await contextFactory.CreateDbContextAsync();
        await using var ctx3 = await contextFactory.CreateDbContextAsync();
        await using var ctx4 = await contextFactory.CreateDbContextAsync();

        // ContinueWith captures elapsed time at the moment each individual task completes,
        // giving the per-task duration even though all three run concurrently.
        long lookupsMs = 0, freqMs = 0, nameOnlyMs = 0;
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

        await Task.WhenAll(t1, t2, t3, t4);

        return (t1.Result, t2.Result, t3.Result, t4.Result, lookupsMs, freqMs, nameOnlyMs);
    }

    private static async Task<HashSet<string>> BuildAmbiguousSurfacesAsync(
        Dictionary<string, List<int>> lookups, IJmDictCache jmDictCache,
        IDbContextFactory<JitenDbContext> contextFactory)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        // Collect all WordIds we need to check (non-JMNedict, from multi-entry surfaces)
        var neededIds = new HashSet<int>();
        foreach (var (_, wordIds) in lookups)
        {
            if (wordIds.Count < 2) continue;
            foreach (var wid in wordIds)
                if (wid < 5_000_000) neededIds.Add(wid);
        }

        // Load POS + priorities for needed words. Try word array first (warm start),
        // fall back to DB query (cold start).
        var wordPos = new Dictionary<int, (List<string> Pos, bool HasPriority)>(neededIds.Count);
        var wordArray = jmDictCache.GetWordArray();
        List<int>? uncached = null;

        foreach (var wid in neededIds)
        {
            JmDictWord? word = null;
            if (wordArray != null && (uint)wid < (uint)wordArray.Length)
                word = wordArray[wid];
            if (word != null)
            {
                bool hasPri = word.Priorities is { Count: > 0 };
                if (!hasPri && word.Forms != null)
                    foreach (var f in word.Forms)
                        if (f.Priorities is { Count: > 0 }) { hasPri = true; break; }
                wordPos[wid] = (word.PartsOfSpeech, hasPri);
            }
            else
            {
                uncached ??= new List<int>();
                uncached.Add(wid);
            }
        }

        if (uncached is { Count: > 0 })
        {
            await using var ctx = await contextFactory.CreateDbContextAsync();
            await using var conn = ctx.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT w."WordId", w."PartsOfSpeech", w."Priorities",
                       EXISTS(SELECT 1 FROM jmdict."WordForms" f WHERE f."WordId" = w."WordId" AND array_length(f."Priorities", 1) > 0) AS has_form_pri
                FROM jmdict."Words" w
                WHERE w."WordId" = ANY(@ids)
                """;
            var param = cmd.CreateParameter();
            param.ParameterName = "ids";
            param.Value = uncached.ToArray();
            cmd.Parameters.Add(param);
            cmd.CommandTimeout = 30;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var wid = reader.GetInt32(0);
                var pos = reader.IsDBNull(1) ? new List<string>() : ((string[])reader.GetValue(1)).ToList();
                var hasPri = !reader.IsDBNull(2) && ((string[])reader.GetValue(2)).Length > 0;
                if (!hasPri) hasPri = reader.GetBoolean(3);
                wordPos[wid] = (pos, hasPri);
            }
        }

        // Check each multi-entry surface for POS overlap between common entries.
        // Skip short kana-only surfaces (≤2 chars) — these are particles/interjections
        // where the beam's frequency scoring always picks the right entry.
        foreach (var (surface, wordIds) in lookups)
        {
            if (wordIds.Count < 2) continue;
            if (surface.Length <= 2 && IsAllKana(surface)) continue;

            List<List<string>>? commonPos = null;
            foreach (var wid in wordIds)
            {
                if (wid >= 5_000_000) continue;
                if (!wordPos.TryGetValue(wid, out var wp) || !wp.HasPriority) continue;
                commonPos ??= new List<List<string>>();
                commonPos.Add(wp.Pos);
            }

            if (commonPos == null || commonPos.Count < 2) continue;

            bool overlapping = false;
            for (int i = 0; i < commonPos.Count && !overlapping; i++)
                for (int j = i + 1; j < commonPos.Count; j++)
                {
                    foreach (var p in commonPos[i])
                        if (commonPos[j].Contains(p)) { overlapping = true; break; }
                    if (overlapping) break;
                }

            if (overlapping)
                result.Add(surface);
        }

        // Manual overrides
        var overridePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "ambiguous_overrides.json");
        if (File.Exists(overridePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(overridePath);
                var overrides = System.Text.Json.JsonSerializer.Deserialize<List<AmbiguousOverride>>(json,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (overrides != null)
                    foreach (var o in overrides)
                        if (!string.IsNullOrEmpty(o.Surface))
                            result.Add(o.Surface);
            }
            catch { }
        }

        return result;
    }

    private static bool IsAllKana(string s)
    {
        foreach (char c in s)
            if (!((c >= '\u3040' && c <= '\u309F') || (c >= '\u30A0' && c <= '\u30FF')))
                return false;
        return true;
    }

    private sealed record AmbiguousOverride(string Surface, string? Reason);

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
    HashSet<string> ambiguousSurfaces)
{
    public IDeckWordCache DeckWordCache { get; } = deckWordCache;
    public IJmDictCache JmDictCache { get; } = jmDictCache;
    public Dictionary<string, List<int>> Lookups { get; } = lookups;
    public Dictionary<int, int> WordFrequencyRanks { get; } = wordFrequencyRanks;
    public HashSet<int> NameOnlyWordIds { get; } = nameOnlyWordIds;
    public HashSet<int> ExpressionWordIds { get; } = expressionWordIds;
    public HashSet<string> AmbiguousSurfaces { get; } = ambiguousSurfaces;
}
