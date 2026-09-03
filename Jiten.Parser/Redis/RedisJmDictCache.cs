using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Diagnostics;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace Jiten.Parser.Data.Redis;

public class RedisJmDictCache : IJmDictCache
{
    private readonly IDatabase _redisDb;
    private static readonly MessagePackSerializerOptions MsgPackOptions =
        ContractlessStandardResolver.Options;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromDays(30);
    private const string InitializedKey = "jmdict:initialized";
    private readonly IDbContextFactory<JitenDbContext> _contextFactory;

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
        var redisKey = BuildWordKey(wordId);
        var value = await _redisDb.StringGetAsync(redisKey);
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
                var bytes = MessagePackSerializer.Serialize(word, MsgPackOptions);
                await _redisDb.StringSetAsync(redisKey, bytes, expiry: _cacheExpiry);
            }

            return word;
        }

        try
        {
            return MessagePackSerializer.Deserialize<JmDictWord>((byte[])value!, MsgPackOptions);
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

        var redisKeys = uniqueIds.Select(id => (RedisKey)BuildWordKey(id)).ToArray();
        var redisValues = await _redisDb.StringGetAsync(redisKeys);

        var results = new Dictionary<int, JmDictWord>();
        var missedIds = new List<int>();

        for (int i = 0; i < redisKeys.Length; i++)
        {
            var id = uniqueIds[i];
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
                        results[id] = word;
                }
                catch
                {
                    missedIds.Add(id);
                }
            }
        }

        if (missedIds.Any())
        {
            ParserCounters.Add(ref ParserCounters.JmDictRedisMisses, missedIds.Count);
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

                            ParserCounters.Add(ref ParserCounters.JmDictDbRows, dbWords.Count);
                            if (dbWords.Any())
                            {
                                var cacheBatch = _redisDb.CreateBatch();
                                foreach (var word in dbWords)
                                {
                                    ComputeArchaicFlag(word);
                                    StripDefinitionMeanings(word);
                                    results[word.WordId] = word;
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
            def.Pos.Clear();
            def.Field.Clear();
            def.Dial.Clear();
            def.SenseInfo.Clear();
            def.GlossTypes.Clear();
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
}
