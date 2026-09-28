using System.Collections.Concurrent;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Diagnostics;
using Jiten.Parser.Scoring;

namespace Jiten.Parser.Data.Redis;

/// <summary>Two-generation LRU over Redis; instances are shared read-only, so escaping DeckWords must copy CachedPOS.</summary>
public sealed class InProcessJmDictCache : IJmDictCache
{
    private readonly IJmDictCache _inner;

    public const int DefaultMaxGen0Entries = 400_000;

    // Per generation; resident count is up to twice this across gen0+gen1.
    private readonly int _maxGen0Entries;

    private volatile ConcurrentDictionary<int, JmDictWord> _gen0 = new();
    private volatile ConcurrentDictionary<int, JmDictWord>? _gen1;
    private int _gen0Count;
    private int _rotating;

    public InProcessJmDictCache(IJmDictCache inner, int maxGen0Entries = DefaultMaxGen0Entries)
    {
        _inner = inner;
        _maxGen0Entries = Math.Max(1, maxGen0Entries);
    }

    private bool TryGetLocal(int id, out JmDictWord word)
    {
        if (_gen0.TryGetValue(id, out word!))
            return true;

        var gen1 = _gen1;
        if (gen1 != null && gen1.TryGetValue(id, out word!))
        {
            Add(id, word);
            return true;
        }

        word = null!;
        return false;
    }

    private void Insert(int id, JmDictWord word)
    {
        PriorityOverrides.Apply(word);
        _ = word.CachedPOS;     // Pre-warm before publishing; concurrent readers would race the lazy init.
        _ = word.CachedPOSMask;
        _ = word.IsSuruVerb;
        Add(id, word);
    }

    private void Add(int id, JmDictWord word)
    {
        if (_gen0.TryAdd(id, word))
            if (Interlocked.Increment(ref _gen0Count) > _maxGen0Entries)
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
            {
                result[id] = local;
                Interlocked.Increment(ref ParserCounters.JmDictInProcessHits);
            }
            else
            {
                (missed ??= new List<int>()).Add(id);
                Interlocked.Increment(ref ParserCounters.JmDictInProcessMisses);
            }
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
