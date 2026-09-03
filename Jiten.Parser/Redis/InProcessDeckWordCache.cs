using System.Collections.Concurrent;
using Jiten.Core.Data;
using Jiten.Parser.Diagnostics;
using StackExchange.Redis;

namespace Jiten.Parser.Data.Redis;

/// <summary>
/// In-process bounded cache of resolved <see cref="DeckWord"/> entries in front of the Redis cache,
/// so recurring vocabulary skips the MGET + MessagePack round trip. Two-generation approximate LRU
/// like <see cref="InProcessJmDictCache"/>.
///
/// Callers mutate the DeckWord they receive (occurrence counting), so every read hands out a fresh
/// clone and every write stores one. Only positive results are cached: a miss must keep reaching
/// Redis, where another process may have resolved the word since.
/// </summary>
public sealed class InProcessDeckWordCache : IDeckWordCache
{
    private readonly IDeckWordCache _inner;

    private const int MaxGen0Entries = 400_000;

    private volatile ConcurrentDictionary<DeckWordCacheKey, DeckWord> _gen0 = new();
    private volatile ConcurrentDictionary<DeckWordCacheKey, DeckWord>? _gen1;
    private int _gen0Count;
    private int _rotating;

    public InProcessDeckWordCache(IDeckWordCache inner) => _inner = inner;

    private bool TryGetLocal(DeckWordCacheKey key, out DeckWord word)
    {
        if (_gen0.TryGetValue(key, out word!))
            return true;

        var gen1 = _gen1;
        if (gen1 != null && gen1.TryGetValue(key, out word!))
        {
            Add(key, word);
            return true;
        }

        word = null!;
        return false;
    }

    private void Add(DeckWordCacheKey key, DeckWord word)
    {
        if (_gen0.TryAdd(key, word))
            if (Interlocked.Increment(ref _gen0Count) > MaxGen0Entries)
                Rotate();
    }

    private void Rotate()
    {
        if (Interlocked.CompareExchange(ref _rotating, 1, 0) != 0)
            return;
        try
        {
            _gen1 = _gen0;
            _gen0 = new ConcurrentDictionary<DeckWordCacheKey, DeckWord>();
            Interlocked.Exchange(ref _gen0Count, 0);
        }
        finally
        {
            Interlocked.Exchange(ref _rotating, 0);
        }
    }

    public async Task<DeckWord?> GetAsync(DeckWordCacheKey key)
    {
        if (TryGetLocal(key, out var local))
        {
            Interlocked.Increment(ref ParserCounters.DeckWordInProcessHits);
            return local.Clone();
        }

        var word = await _inner.GetAsync(key);
        if (word != null)
            Add(key, word.Clone());
        return word;
    }

    public async Task<Dictionary<DeckWordCacheKey, DeckWord?>> GetManyAsync(IReadOnlyList<DeckWordCacheKey> keys)
    {
        var results = new Dictionary<DeckWordCacheKey, DeckWord?>(keys.Count);
        List<DeckWordCacheKey>? missed = null;

        foreach (var key in keys)
        {
            if (TryGetLocal(key, out var local))
            {
                results[key] = local.Clone();
                Interlocked.Increment(ref ParserCounters.DeckWordInProcessHits);
            }
            else
            {
                (missed ??= new List<DeckWordCacheKey>()).Add(key);
            }
        }

        if (missed != null)
        {
            var fetched = await _inner.GetManyAsync(missed);
            foreach (var (key, word) in fetched)
            {
                if (word != null)
                    Add(key, word.Clone());
                results[key] = word;
            }
        }

        return results;
    }

    public Task SetAsync(DeckWordCacheKey key, DeckWord word, CommandFlags flags = CommandFlags.None)
    {
        Add(key, word.Clone());
        return _inner.SetAsync(key, word, flags);
    }

    public Task SetManyAsync(IReadOnlyList<(DeckWordCacheKey key, DeckWord word)> entries, CommandFlags flags = CommandFlags.None)
    {
        foreach (var (key, word) in entries)
            Add(key, word.Clone());
        return _inner.SetManyAsync(entries, flags);
    }
}
