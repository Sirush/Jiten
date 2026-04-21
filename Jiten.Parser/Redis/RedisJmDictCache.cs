using Jiten.Core;
using Jiten.Core.Data.JMDict;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;
using System.Collections.Concurrent;

namespace Jiten.Parser.Data.Redis;

public class RedisJmDictCache : IJmDictCache
{
    private readonly IDatabase _redisDb;
    private static readonly MessagePackSerializerOptions MsgPackOptions =
        ContractlessStandardResolver.Options;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromDays(30);
    private const string InitializedKey = "jmdict:initialized";
    private readonly IDbContextFactory<JitenDbContext> _contextFactory;
    private static readonly ConcurrentDictionary<int, JmDictWord> LocalWordCache = new();
    private const int WordArraySize = 10_000_000;
    private static JmDictWord?[]? _wordArray;

    private static readonly SemaphoreSlim DbSemaphore = new SemaphoreSlim(10, 10);

    public RedisJmDictCache(IConfiguration configuration, IDbContextFactory<JitenDbContext> contextFactory)
    {
        _redisDb = RedisConnectionManager.GetDatabase(configuration);
        _contextFactory = contextFactory;
    }

    private string BuildLookupKey(string lookupText)
    {
        return $"jmdict:lookup:{lookupText}";
    }

    private string BuildWordKey(int wordId)
    {
        return $"jmdict:word:{wordId}";
    }

    public async Task<List<int>> GetLookupIdsAsync(string key)
    {
        var redisKey = BuildLookupKey(key);
        var value = await _redisDb.StringGetAsync(redisKey);
        Jiten.Parser.Diagnostics.ParserBenchmarkInstrumentation.RecordLookupCacheResult(
            hits: value.IsNullOrEmpty ? 0 : 1, misses: value.IsNullOrEmpty ? 1 : 0);
        if (value.IsNullOrEmpty)
        {
            await using var dbContext = await _contextFactory.CreateDbContextAsync();
            var lookupIds = await dbContext.Lookups
                                           .AsNoTracking()
                                           .Where(l => l.LookupKey == key)
                                           .Select(l => l.WordId)
                                           .ToListAsync();

            if (lookupIds.Any())
            {
                var bytes = MessagePackSerializer.Serialize(lookupIds, MsgPackOptions);
                await _redisDb.StringSetAsync(redisKey, bytes, expiry: _cacheExpiry);
            }

            return lookupIds;
        }

        try
        {
            return MessagePackSerializer.Deserialize<List<int>>((byte[])value!, MsgPackOptions) ?? new List<int>();
        }
        catch
        {
            return new List<int>();
        }
    }

    public async Task<Dictionary<string, List<int>>> GetLookupIdsAsync(IEnumerable<string> keys)
    {
        var uniqueKeys = keys.Distinct().ToList();
        if (!uniqueKeys.Any())
        {
            return new Dictionary<string, List<int>>();
        }

        var redisKeys = uniqueKeys.Select(k => (RedisKey)BuildLookupKey(k)).ToArray();

        // 1. Fetch all keys from Redis in a single MGET command
        var redisValues = await _redisDb.StringGetAsync(redisKeys);

        var results = new Dictionary<string, List<int>>();
        var missedKeys = new List<string>();

        // 2. Process the results from Redis
        for (int i = 0; i < redisKeys.Length; i++)
        {
            var lookupKey = uniqueKeys[i];
            var redisValue = redisValues[i];

            if (redisValue.IsNullOrEmpty)
            {
                missedKeys.Add(lookupKey);
            }
            else
            {
                try
                {
                    results[lookupKey] = MessagePackSerializer.Deserialize<List<int>>((byte[])redisValue!, MsgPackOptions) ?? new List<int>();
                }
                catch
                {
                    missedKeys.Add(lookupKey);
                }
            }
        }

        Jiten.Parser.Diagnostics.ParserBenchmarkInstrumentation.RecordLookupCacheResult(
            hits: uniqueKeys.Count - missedKeys.Count, misses: missedKeys.Count);

        // 3. If any keys were not in the cache, fetch them from the database in a single query
        if (missedKeys.Any())
        {
            await using var dbContext = await _contextFactory.CreateDbContextAsync();

            var dbLookups = await dbContext.Lookups
                                           .AsNoTracking()
                                           .Where(l => missedKeys.Contains(l.LookupKey))
                                           .Select(l => new { l.LookupKey, l.WordId })
                                           .ToListAsync();

            var dbResults = dbLookups
                            .GroupBy(l => l.LookupKey)
                            .ToDictionary(g => g.Key, g => g.Select(l => l.WordId).ToList());

            // 4. Add the database results to our main results and prepare to cache them
            var cacheBatch = _redisDb.CreateBatch();
            foreach (var kvp in dbResults)
            {
                results[kvp.Key] = kvp.Value;
                var redisKey = BuildLookupKey(kvp.Key);
                var bytes = MessagePackSerializer.Serialize(kvp.Value, MsgPackOptions);
                _ = cacheBatch.StringSetAsync(redisKey, bytes, expiry: _cacheExpiry);
            }

            // Execute the batch to write all new entries to Redis
            cacheBatch.Execute();
        }

        return results;
    }

    public async Task<JmDictWord?> GetWordAsync(int wordId)
    {
        if (LocalWordCache.TryGetValue(wordId, out var localWord))
        {
            Jiten.Parser.Diagnostics.ParserBenchmarkInstrumentation.RecordWordCacheResult(hits: 1, misses: 0);
            return localWord;
        }

        var redisKey = BuildWordKey(wordId);
        var value = await _redisDb.StringGetAsync(redisKey);
        Jiten.Parser.Diagnostics.ParserBenchmarkInstrumentation.RecordWordCacheResult(
            hits: value.IsNullOrEmpty ? 0 : 1, misses: value.IsNullOrEmpty ? 1 : 0);
        if (value.IsNullOrEmpty)
        {
            await using var dbContext = await _contextFactory.CreateDbContextAsync();

            var word = await dbContext.JMDictWords
                                      .AsNoTracking()
                                      .Include(w => w.Forms.OrderBy(f => f.ReadingIndex))
                                      .Include(w => w.Definitions)
                                      .FirstOrDefaultAsync(w => w.WordId == wordId);

            if (word != null)
            {
                ComputeArchaicFlag(word);
                StripDefinitionMeanings(word);
                CacheWordLocally(wordId, word);
                var bytes = MessagePackSerializer.Serialize(word, MsgPackOptions);
                await _redisDb.StringSetAsync(redisKey, bytes, expiry: _cacheExpiry);
            }

            return word;
        }

        try
        {
            var word = MessagePackSerializer.Deserialize<JmDictWord>((byte[])value!, MsgPackOptions);
            if (word != null) CacheWordLocally(wordId, word);
            return word;
        }
        catch
        {
            return null;
        }
    }


    public async Task<Dictionary<int, JmDictWord>> GetWordsAsync(IEnumerable<int> wordIds)
    {
        var uniqueIds = wordIds.Distinct().ToList();
        if (!uniqueIds.Any())
        {
            return new Dictionary<int, JmDictWord>();
        }

        var results = new Dictionary<int, JmDictWord>();
        var unresolvedIds = new List<int>();
        foreach (var id in uniqueIds)
        {
            if (LocalWordCache.TryGetValue(id, out var localWord))
                results[id] = localWord;
            else
                unresolvedIds.Add(id);
        }

        if (!unresolvedIds.Any())
        {
            Jiten.Parser.Diagnostics.ParserBenchmarkInstrumentation.RecordWordCacheResult(
                hits: uniqueIds.Count, misses: 0);
            return results;
        }

        var redisKeys = unresolvedIds.Select(id => (RedisKey)BuildWordKey(id)).ToArray();
        var redisValues = await _redisDb.StringGetAsync(redisKeys);
        var missedIds = new List<int>();

        for (int i = 0; i < redisKeys.Length; i++)
        {
            var id = unresolvedIds[i];
            var value = redisValues[i];

            if (value.IsNullOrEmpty)
            {
                missedIds.Add(id);
            }
            else
            {
                try
                {
                    var word = MessagePackSerializer.Deserialize<JmDictWord>((byte[])value!, MsgPackOptions);
                    if (word != null)
                    {
                        results[id] = word;
                        CacheWordLocally(id, word);
                    }
                }
                catch
                {
                    missedIds.Add(id);
                }
            }
        }

        Jiten.Parser.Diagnostics.ParserBenchmarkInstrumentation.RecordWordCacheResult(
            hits: uniqueIds.Count - missedIds.Count, misses: missedIds.Count);

        if (missedIds.Any())
        {
            const int batchSize = 1000;

            for (int i = 0; i < missedIds.Count; i += batchSize)
            {
                var batchIds = missedIds.Skip(i).Take(batchSize).ToList();

                if (!await DbSemaphore.WaitAsync(TimeSpan.FromSeconds(5)))
                {
                    continue;
                }

                try
                {
                    const int maxRetries = 3;
                    for (int retry = 0; retry < maxRetries; retry++)
                    {
                        try
                        {
                            await using var dbContext = await _contextFactory.CreateDbContextAsync();

                            dbContext.Database.SetCommandTimeout(TimeSpan.FromSeconds(5));

                            var dbWords = await dbContext.JMDictWords
                                .AsNoTracking()
                                .Include(w => w.Forms.OrderBy(f => f.ReadingIndex))
                                .Include(w => w.Definitions)
                                .Where(w => batchIds.Contains(w.WordId))
                                .ToListAsync();

                            if (dbWords.Any())
                            {
                                var cacheBatch = _redisDb.CreateBatch();
                                foreach (var word in dbWords)
                                {
                                    ComputeArchaicFlag(word);
                                    StripDefinitionMeanings(word);
                                    results[word.WordId] = word;
                                    CacheWordLocally(word.WordId, word);
                                    var redisKey = BuildWordKey(word.WordId);
                                    var bytes = MessagePackSerializer.Serialize(word, MsgPackOptions);
                                    _ = cacheBatch.StringSetAsync(redisKey, bytes, expiry: _cacheExpiry, flags: CommandFlags.FireAndForget);
                                }
                                cacheBatch.Execute();
                            }

                            break;
                        }
                        catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "53300" && retry < maxRetries - 1)
                        {
                            var backoffMs = (int)Math.Pow(2, retry) * 100 + Random.Shared.Next(50);
                            await Task.Delay(backoffMs);
                        }
                        catch when (retry < maxRetries - 1)
                        {
                            var backoffMs = (int)Math.Pow(2, retry) * 200 + Random.Shared.Next(100);
                            await Task.Delay(backoffMs);
                        }
                    }
                }
                finally
                {
                    DbSemaphore.Release();
                }
            }
        }

        return results;
    }

    public async Task<bool> SetLookupIdsAsync(Dictionary<string, List<int>> lookups)
    {
        var batch = _redisDb.CreateBatch();
        var tasks = new List<Task<bool>>();

        foreach (var lookup in lookups)
        {
            var redisKey = BuildLookupKey(lookup.Key);
            var bytes = MessagePackSerializer.Serialize(lookup.Value, MsgPackOptions);
            tasks.Add(batch.StringSetAsync(redisKey, bytes, expiry: _cacheExpiry));
        }

        batch.Execute();
        await Task.WhenAll(tasks);

        return tasks.All(t => t.Result);
    }

    public async Task<bool> SetWordAsync(int wordId, JmDictWord word)
    {
        CacheWordLocally(wordId, word);
        var redisKey = BuildWordKey(wordId);
        var bytes = MessagePackSerializer.Serialize(word, MsgPackOptions);
        return await _redisDb.StringSetAsync(redisKey, bytes, expiry: _cacheExpiry);
    }

    public async Task<bool> SetWordsAsync(Dictionary<int, JmDictWord> words)
    {
        var batch = _redisDb.CreateBatch();
        var tasks = new List<Task<bool>>();

        foreach (var (wordId, word) in words)
        {
            ComputeArchaicFlag(word);
            StripDefinitionMeanings(word);
            CacheWordLocally(wordId, word);
            var redisKey = BuildWordKey(wordId);
            var bytes = MessagePackSerializer.Serialize(word, MsgPackOptions);
            tasks.Add(batch.StringSetAsync(redisKey, bytes, expiry: _cacheExpiry));
        }

        batch.Execute();
        await Task.WhenAll(tasks);

        return tasks.All(t => t.Result);
    }

    private static void StripDefinitionMeanings(JmDictWord word)
    {
        foreach (var def in word.Definitions)
        {
            def.EnglishMeanings.Clear();
            def.DutchMeanings.Clear();
            def.FrenchMeanings.Clear();
            def.GermanMeanings.Clear();
            def.SpanishMeanings.Clear();
            def.HungarianMeanings.Clear();
            def.RussianMeanings.Clear();
            def.SlovenianMeanings.Clear();
            def.Pos.Clear();
            def.Field.Clear();
            def.Dial.Clear();
        }
    }

    private static void ComputeArchaicFlag(JmDictWord word)
    {
        if (!word.PartsOfSpeech.Contains("arch"))
            return;

        // Definitions weren't loaded — IsFullyArchaic was pre-computed by the caller.
        if (word.Definitions.Count == 0)
            return;

        var englishDefs = word.Definitions.Where(d => d.EnglishMeanings.Count > 0).ToList();
        word.IsFullyArchaic = englishDefs.Count > 0
                              && englishDefs.All(d => d.PartsOfSpeech.Contains("arch"));
    }

    public async Task<bool> IsCacheInitializedAsync()
    {
        return await _redisDb.KeyExistsAsync(InitializedKey);
    }

    public async Task SetCacheInitializedAsync()
    {
        await _redisDb.StringSetAsync(InitializedKey, "1", expiry: _cacheExpiry);
    }

    private const string NonArchaicPosMapKey = "jmdict:non_arch_pos_map";

    public async Task<Dictionary<int, List<string>>?> GetNonArchaicPosMapAsync()
    {
        var value = await _redisDb.StringGetAsync(NonArchaicPosMapKey);
        if (value.IsNullOrEmpty) return null;
        try
        {
            return MessagePackSerializer.Deserialize<Dictionary<int, List<string>>>((byte[])value!, MsgPackOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task SetNonArchaicPosMapAsync(Dictionary<int, List<string>> map)
    {
        var bytes = MessagePackSerializer.Serialize(map, MsgPackOptions);
        await _redisDb.StringSetAsync(NonArchaicPosMapKey, bytes, expiry: _cacheExpiry);
    }

    public bool TryGetWordLocal(int wordId, out JmDictWord? word)
    {
        var arr = _wordArray;
        if (arr != null && (uint)wordId < (uint)arr.Length)
        {
            word = arr[wordId];
            return word != null;
        }
        return LocalWordCache.TryGetValue(wordId, out word);
    }

    public JmDictWord?[]? GetWordArray() => _wordArray;

    public async Task PreloadWordsAsync(IEnumerable<int> wordIds)
    {
        var needed = new HashSet<int>();
        foreach (var id in wordIds)
            if (!LocalWordCache.ContainsKey(id))
                needed.Add(id);

        if (needed.Count == 0) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using var ctx = await _contextFactory.CreateDbContextAsync();
        var conn = (Npgsql.NpgsqlConnection)ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        var words = new Dictionary<int, JmDictWord>(needed.Count);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT ""WordId"", ""PartsOfSpeech"", ""Priorities"" FROM jmdict.""Words"" ORDER BY ""WordId""";
            cmd.CommandTimeout = 30;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var wordId = reader.GetInt32(0);
                if (!needed.Contains(wordId)) continue;
                var pos = reader.IsDBNull(1) ? new List<string>() : ((string[])reader.GetValue(1)).ToList();
                var priorities = reader.IsDBNull(2) ? null : ((string[])reader.GetValue(2)).ToList();
                words[wordId] = new JmDictWord
                {
                    WordId = wordId,
                    PartsOfSpeech = pos,
                    Priorities = priorities,
                    Forms = [],
                    Definitions = []
                };
            }
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT ""WordId"", ""ReadingIndex"", ""Text"", ""FormType"", ""Priorities"", ""IsNoKanji"", ""IsSearchOnly"", ""IsObsolete"" FROM jmdict.""WordForms"" ORDER BY ""WordId"", ""ReadingIndex""";
            cmd.CommandTimeout = 30;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var wordId = reader.GetInt32(0);
                if (!words.TryGetValue(wordId, out var word)) continue;
                word.Forms.Add(new JmDictWordForm
                {
                    WordId = wordId,
                    ReadingIndex = reader.GetInt16(1),
                    Text = reader.GetString(2),
                    FormType = (JmDictFormType)reader.GetInt32(3),
                    Priorities = reader.IsDBNull(4) ? null : ((string[])reader.GetValue(4)).ToList(),
                    IsNoKanji = reader.GetBoolean(5),
                    IsSearchOnly = reader.GetBoolean(6),
                    IsObsolete = reader.GetBoolean(7)
                });
            }
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT ""WordId"", ""SenseIndex"", ""Misc"", ""PartsOfSpeech"", (array_length(""EnglishMeanings"", 1) IS NOT NULL) AS has_eng FROM jmdict.""Definitions"" ORDER BY ""WordId"", ""SenseIndex""";
            cmd.CommandTimeout = 30;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var wordId = reader.GetInt32(0);
                if (!words.TryGetValue(wordId, out var word)) continue;
                word.Definitions.Add(new JmDictDefinition
                {
                    WordId = wordId,
                    SenseIndex = reader.GetInt32(1),
                    Misc = reader.IsDBNull(2) ? [] : ((string[])reader.GetValue(2)).ToList(),
                    PartsOfSpeech = reader.IsDBNull(3) ? [] : ((string[])reader.GetValue(3)).ToList(),
                    EnglishMeanings = reader.GetBoolean(4) ? ["_"] : []
                });
            }
        }

        var arr = new JmDictWord?[WordArraySize];
        foreach (var word in words.Values)
        {
            ComputeArchaicFlag(word);
            StripDefinitionMeanings(word);
            LocalWordCache.TryAdd(word.WordId, word);
            if ((uint)word.WordId < (uint)arr.Length)
                arr[word.WordId] = word;
        }
        _wordArray = arr;

        sw.Stop();
        Console.Error.WriteLine($"[BeamWordPreload] Loaded {words.Count}/{needed.Count} words in {sw.ElapsedMilliseconds}ms");
    }

    private static void CacheWordLocally(int wordId, JmDictWord word)
    {
        LocalWordCache[wordId] = word;
        var arr = _wordArray;
        if (arr != null && (uint)wordId < (uint)arr.Length)
            arr[wordId] = word;
    }
}
