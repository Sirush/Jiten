using Jiten.Core.Data;
using Jiten.Parser.Diagnostics;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace Jiten.Parser.Data.Redis;

public class RedisDeckWordCache : IDeckWordCache
{
    private readonly IDatabase _redisDb;
    private static readonly MessagePackSerializerOptions MsgPackOptions =
        ContractlessStandardResolver.Options;

    public RedisDeckWordCache(IConfiguration configuration)
    {
        _redisDb = RedisConnectionManager.GetDatabase(configuration);
    }

    private static string BuildRedisKey(DeckWordCacheKey key)
    {
        // Context flags are part of the key because they can affect dictionary matching (e.g., honorifics, name-likeness).
        return $"deckword:{key.Text}:{key.PartOfSpeech}:{key.DictionaryForm}:{key.Reading}:{(key.IsPersonNameContext ? 1 : 0)}:{(key.IsNameLikeSudachiNoun ? 1 : 0)}";
    }

    public async Task<DeckWord?> GetAsync(DeckWordCacheKey key)
    {
        var redisKey = BuildRedisKey(key);
        var value = await _redisDb.StringGetAsync(redisKey);
        if (value.IsNullOrEmpty)
            return null;

        try
        {
            return MessagePackSerializer.Deserialize<DeckWord>((byte[])value!, MsgPackOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task<Dictionary<DeckWordCacheKey, DeckWord?>> GetManyAsync(IReadOnlyList<DeckWordCacheKey> keys)
    {
        if (keys.Count == 0)
            return new Dictionary<DeckWordCacheKey, DeckWord?>();

        var redisKeys = new RedisKey[keys.Count];
        for (int i = 0; i < keys.Count; i++)
            redisKeys[i] = BuildRedisKey(keys[i]);

        var values = await _redisDb.StringGetAsync(redisKeys);

        var results = new Dictionary<DeckWordCacheKey, DeckWord?>(keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            if (values[i].IsNullOrEmpty)
            {
                results[keys[i]] = null;
                Interlocked.Increment(ref ParserCounters.DeckWordCacheMisses);
            }
            else
            {
                try
                {
                    results[keys[i]] = MessagePackSerializer.Deserialize<DeckWord>((byte[])values[i]!, MsgPackOptions);
                    Interlocked.Increment(ref ParserCounters.DeckWordCacheHits);
                }
                catch
                {
                    results[keys[i]] = null;
                }
            }
        }

        return results;
    }

    public async Task SetAsync(DeckWordCacheKey key, DeckWord word, CommandFlags flags = CommandFlags.None)
    {
        var redisKey = BuildRedisKey(key);
        var bytes = MessagePackSerializer.Serialize(word, MsgPackOptions);

        await _redisDb.StringSetAsync(redisKey, bytes, expiry: TimeSpan.FromDays(30), flags: flags);
    }

    public Task SetManyAsync(IReadOnlyList<(DeckWordCacheKey key, DeckWord word)> entries, CommandFlags flags = CommandFlags.None)
    {
        if (entries.Count == 0)
            return Task.CompletedTask;

        var batch = _redisDb.CreateBatch();
        foreach (var (key, word) in entries)
        {
            var redisKey = BuildRedisKey(key);
            var bytes = MessagePackSerializer.Serialize(word, MsgPackOptions);
            _ = batch.StringSetAsync(redisKey, bytes, expiry: TimeSpan.FromDays(30), flags: flags);
        }
        batch.Execute();
        return Task.CompletedTask;
    }
}
