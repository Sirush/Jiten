using System.Collections.Concurrent;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Scoring;

namespace Jiten.Parser.Data.Redis;

/// <summary>
/// In-process bounded cache of <see cref="JmDictWord"/> sitting in front of an inner (Redis) cache.
/// Eliminates the repeated Redis MGET + MessagePack deserialize for hot vocabulary that recurs across
/// documents/sentences (の/する/いる/common kanji…), which the per-document batch cache cannot.
///
/// Storage is a two-generation approximate LRU (mirrors <c>KanaConverter</c>): bounded to ~2×
/// <see cref="MaxGen0Entries"/> resident. With ~0.8–0.95 KB/word that is ~40–90 MB for 50–100K words.
///
/// Cached instances are SHARED across documents and threads and treated as READ-ONLY. At insert,
/// before an instance becomes visible, we (a) apply <see cref="PriorityOverrides"/> once and (b)
/// pre-warm the lazy CachedPOS/CachedPOSMask/IsSuruVerb fields, so concurrent readers never trigger a
/// lazy-init race and never need to mutate the instance. Callers that copy a word's POS list into an
/// escaping <c>DeckWord</c> must copy the list (the Parser DeckWord builds do: <c>[..word.CachedPOS]</c>).
/// </summary>
public sealed class InProcessJmDictCache : IJmDictCache
{
    private readonly IJmDictCache _inner;

    // 50K per generation → 50–100K resident across gen0+gen1.
    private const int MaxGen0Entries = 50_000;

    private volatile ConcurrentDictionary<int, JmDictWord> _gen0 = new();
    private volatile ConcurrentDictionary<int, JmDictWord>? _gen1;
    private int _gen0Count;
    private int _rotating;

    public InProcessJmDictCache(IJmDictCache inner) => _inner = inner;

    private bool TryGetLocal(int id, out JmDictWord word)
    {
        if (_gen0.TryGetValue(id, out word!))
            return true;

        var gen1 = _gen1;
        if (gen1 != null && gen1.TryGetValue(id, out word!))
        {
            Add(id, word); // promote previous-generation hit into gen0
            return true;
        }

        word = null!;
        return false;
    }

    // Prepare a freshly fetched instance once, then publish it to gen0.
    private void Insert(int id, JmDictWord word)
    {
        PriorityOverrides.Apply(word);
        _ = word.CachedPOS;     // pre-warm lazy fields while still single-owner
        _ = word.CachedPOSMask;
        _ = word.IsSuruVerb;
        Add(id, word);
    }

    private void Add(int id, JmDictWord word)
    {
        if (_gen0.TryAdd(id, word))
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
            _gen0 = new ConcurrentDictionary<int, JmDictWord>();
            Interlocked.Exchange(ref _gen0Count, 0);
        }
        finally
        {
            Interlocked.Exchange(ref _rotating, 0);
        }
    }

    public async Task<JmDictWord?> GetWordAsync(int wordId)
    {
        if (TryGetLocal(wordId, out var local))
            return local;

        var word = await _inner.GetWordAsync(wordId);
        if (word != null)
            Insert(wordId, word);
        return word;
    }

    public async Task<Dictionary<int, JmDictWord>> GetWordsAsync(IEnumerable<int> wordIds)
    {
        var result = new Dictionary<int, JmDictWord>();
        List<int>? missed = null;

        foreach (var id in wordIds)
        {
            if (result.ContainsKey(id))
                continue;
            if (TryGetLocal(id, out var local))
                result[id] = local;
            else
                (missed ??= new List<int>()).Add(id);
        }

        if (missed != null)
        {
            var fetched = await _inner.GetWordsAsync(missed);
            foreach (var (id, word) in fetched)
            {
                Insert(id, word);
                result[id] = word;
            }
        }

        return result;
    }

    // Writes go straight to the inner store; drop any stale local copies so the next read reloads.
    public Task<bool> SetWordAsync(int wordId, JmDictWord word)
    {
        Evict(wordId);
        return _inner.SetWordAsync(wordId, word);
    }

    public Task<bool> SetWordsAsync(Dictionary<int, JmDictWord> words)
    {
        foreach (var id in words.Keys)
            Evict(id);
        return _inner.SetWordsAsync(words);
    }

    private void Evict(int id)
    {
        _gen0.TryRemove(id, out _);
        _gen1?.TryRemove(id, out _);
    }

    public Task<bool> IsCacheInitializedAsync() => _inner.IsCacheInitializedAsync();
    public Task SetCacheInitializedAsync() => _inner.SetCacheInitializedAsync();
}
