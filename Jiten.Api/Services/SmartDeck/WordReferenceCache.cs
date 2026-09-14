using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.SmartDeck;

/// <summary>Per-form global frequency rank and kana-form flag for every JMDict form, held in process so a rebuild never queries them.</summary>
public interface IWordReferenceCache
{
    Task EnsureLoaded(CancellationToken ct = default);
    bool TryGetRank(long key, out int rank);
    bool IsKanaForm(long key);
    Task Reload(CancellationToken ct = default);
}

public class WordReferenceCache(IDbContextFactory<JitenDbContext> contextFactory, ILogger<WordReferenceCache> logger) : IWordReferenceCache
{
    private volatile Dictionary<long, int> _ranks = new();
    private volatile HashSet<long> _kanaKeys = new();
    private volatile bool _loaded;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    public async Task EnsureLoaded(CancellationToken ct = default)
    {
        if (_loaded) return;
        await _loadGate.WaitAsync(ct);
        try
        {
            if (_loaded) return;
            await LoadCore(ct);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public bool TryGetRank(long key, out int rank) => _ranks.TryGetValue(key, out rank);

    public bool IsKanaForm(long key) => _kanaKeys.Contains(key);

    public async Task Reload(CancellationToken ct = default)
    {
        await _loadGate.WaitAsync(ct);
        try
        {
            await LoadCore(ct);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task LoadCore(CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var ranks = new Dictionary<long, int>(1_100_000);
        await foreach (var f in context.WordFormFrequencies.AsNoTracking()
                                      .Select(f => new { f.WordId, f.ReadingIndex, f.FrequencyRank })
                                      .AsAsyncEnumerable().WithCancellation(ct))
            ranks[WordFormHelper.EncodeWordKey(f.WordId, f.ReadingIndex)] = f.FrequencyRank;

        var kana = new HashSet<long>(400_000);
        await foreach (var f in context.WordForms.AsNoTracking()
                                      .Where(wf => wf.FormType == JmDictFormType.KanaForm)
                                      .Select(wf => new { wf.WordId, wf.ReadingIndex })
                                      .AsAsyncEnumerable().WithCancellation(ct))
            kana.Add(WordFormHelper.EncodeWordKey(f.WordId, f.ReadingIndex));

        _ranks = ranks;
        _kanaKeys = kana;
        _loaded = true;
        logger.LogInformation("Word reference cache loaded: {Ranks} ranks, {Kana} kana forms", ranks.Count, kana.Count);
    }
}
