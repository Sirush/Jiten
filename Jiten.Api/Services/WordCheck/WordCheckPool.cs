using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.WordCheck;

public readonly record struct PoolWord(int WordId, byte ReadingIndex, int Rank, string Text);

public sealed class WordCheckPoolData
{
    /// <summary>Sorted by rank.</summary>
    public required IReadOnlyList<PoolWord> Words { get; init; }

    /// <summary>Kanji compounds and verb blends that exist in no dictionary.</summary>
    public required IReadOnlyList<string> PseudoWords { get; init; }

    public IEnumerable<PoolWord> InRange(int minRank, int maxRank) => Words.Where(w => w.Rank >= minRank && w.Rank <= maxRank);
}

public interface IWordCheckPool
{
    Task<WordCheckPoolData> GetAsync();
}

/// <summary>Probe words measure general vocabulary: widely used kanji forms that no other entry shares, with no names or special-usage tags.</summary>
public sealed class WordCheckPool(IDbContextFactory<JitenDbContext> contextFactory, ILogger<WordCheckPool> logger) : IWordCheckPool
{
    private const int BreadthBucketSize = 500;
    private const int PseudoWordTarget = 300;
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(24);

    private static readonly HashSet<string> ExcludedPos = new(StringComparer.Ordinal)
    {
        "uk", "arch", "obs", "exp", "num", "ctr", "suf", "pref", "n-suf", "n-pref", "abbr", "prt", "aux", "aux-v", "aux-adj",
        "cop", "int", "on-mim", "vulg", "X", "sl", "male", "fem", "hon", "hum", "pol", "dated", "rare"
    };

    private static readonly HashSet<string> ExcludedFormTags = new(StringComparer.OrdinalIgnoreCase) { "rK", "sK", "oK", "iK", "io" };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile WordCheckPoolData? _data;
    private DateTime _loadedAt;
    private int _refreshing;

    /// <summary>A stale pool is served while one background reload replaces it, so checks never wait on a rebuild after the first load.</summary>
    public async Task<WordCheckPoolData> GetAsync()
    {
        var data = _data;
        if (data != null)
        {
            if (DateTime.UtcNow - _loadedAt >= RefreshAfter && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ReloadAsync();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Word check pool reload failed");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _refreshing, 0);
                    }
                });
            return data;
        }

        await _gate.WaitAsync();
        try
        {
            return _data ?? await ReloadAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<WordCheckPoolData> ReloadAsync()
    {
        var data = await LoadAsync();
        _loadedAt = DateTime.UtcNow;
        _data = data;
        logger.LogInformation("Word check pool loaded: {Words} words, {Pseudo} pseudo-words", data.Words.Count, data.PseudoWords.Count);
        return data;
    }

    private async Task<WordCheckPoolData> LoadAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var rows = await (from ff in context.WordFormFrequencies.AsNoTracking()
                          join wf in context.WordForms.AsNoTracking()
                              on new { ff.WordId, ff.ReadingIndex } equals new { wf.WordId, wf.ReadingIndex }
                          where (ff.WordId < SentenceComprehension.NameEntryFloor || ff.WordId >= SentenceComprehension.NameEntryCeiling)
                                && ff.FrequencyRank > 0
                                && ff.FrequencyRank <= WordCheckEngine.MaxProbeRank
                                && wf.FormType == JmDictFormType.KanjiForm
                                && !wf.IsObsolete && !wf.IsSearchOnly
                          select new
                          {
                              ff.WordId, ff.ReadingIndex, ff.FrequencyRank, ff.UsedInMediaAmount, wf.Text, wf.InfoTags
                          }).ToListAsync();

        var candidates = rows.Where(r => IsProbeSurface(r.Text) && (r.InfoTags == null || !r.InfoTags.Any(ExcludedFormTags.Contains)))
                             .ToList();

        var wordIds = candidates.Select(r => r.WordId).Distinct().ToList();
        var posByWord = (await context.JMDictWords.AsNoTracking()
                                      .Where(w => wordIds.Contains(w.WordId))
                                      .Select(w => new { w.WordId, w.PartsOfSpeech })
                                      .ToListAsync())
            .ToDictionary(w => w.WordId, w => w.PartsOfSpeech);

        var texts = candidates.Select(r => r.Text).Distinct().ToList();
        var ambiguous = (await context.WordForms.AsNoTracking()
                                      .Where(wf => texts.Contains(wf.Text) && wf.FormType == JmDictFormType.KanjiForm)
                                      .Select(wf => new { wf.Text, wf.WordId })
                                      .ToListAsync())
            .GroupBy(x => x.Text)
            .Where(g => g.Select(x => x.WordId).Distinct().Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        var eligible = candidates.Where(r => !ambiguous.Contains(r.Text)
                                             && posByWord.TryGetValue(r.WordId, out var pos)
                                             && !pos.Any(ExcludedPos.Contains))
                                 .GroupBy(r => r.WordId)
                                 .Select(g => g.OrderBy(r => r.FrequencyRank).First())
                                 .ToList();

        var probe = eligible.GroupBy(r => r.FrequencyRank / BreadthBucketSize)
                            .SelectMany(bucket =>
                            {
                                var amounts = bucket.Select(r => r.UsedInMediaAmount).OrderBy(x => x).ToList();
                                var median = amounts[amounts.Count / 2];
                                return bucket.Where(r => r.UsedInMediaAmount >= median);
                            })
                            .Select(r => new PoolWord(r.WordId, (byte)r.ReadingIndex, r.FrequencyRank, r.Text))
                            .OrderBy(w => w.Rank)
                            .ToList();

        var pseudo = await BuildPseudoWordsAsync(context, probe);

        return new WordCheckPoolData { Words = probe, PseudoWords = pseudo };
    }

    /// <summary>Recombines halves of real probe words and keeps only results that match no dictionary entry or name.</summary>
    private static async Task<List<string>> BuildPseudoWordsAsync(JitenDbContext context, List<PoolWord> probe)
    {
        var random = new Random(20261107);
        var compounds = probe.Select(w => w.Text).Where(t => t.Length == 2 && t.All(JapaneseTextHelper.IsKanjiOrIterationMark)).Distinct().ToList();
        var verbs = probe.Select(w => w.Text)
                         .Where(t => t.Length is >= 2 and <= 4 && JapaneseTextHelper.IsKanjiOrIterationMark(t[0]) && t.Skip(1).All(JapaneseTextHelper.IsHiragana))
                         .Distinct()
                         .ToList();

        var generated = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < PseudoWordTarget * 4 && compounds.Count > 1; i++)
        {
            var a = compounds[random.Next(compounds.Count)];
            var b = compounds[random.Next(compounds.Count)];
            if (a[0] != b[1] && a[1] != b[1]) generated.Add($"{a[0]}{b[1]}");
        }

        for (var i = 0; i < PseudoWordTarget && verbs.Count > 1; i++)
        {
            var a = verbs[random.Next(verbs.Count)];
            var b = verbs[random.Next(verbs.Count)];
            if (a[0] != b[0] && a[1..] != b[1..]) generated.Add($"{a[0]}{b[1..]}");
        }

        var candidates = generated.ToList();
        var existing = (await context.Lookups.AsNoTracking()
                                     .Where(l => candidates.Contains(l.LookupKey))
                                     .Select(l => l.LookupKey)
                                     .ToListAsync())
            .Concat(await context.WordForms.AsNoTracking()
                                 .Where(wf => candidates.Contains(wf.Text))
                                 .Select(wf => wf.Text)
                                 .ToListAsync())
            .ToHashSet(StringComparer.Ordinal);

        return candidates.Where(c => !existing.Contains(c)).Take(PseudoWordTarget).ToList();
    }

    private static bool IsProbeSurface(string text)
        => text.Length >= 2 && text.Any(JapaneseTextHelper.IsKanjiOrIterationMark)
                            && text.All(c => JapaneseTextHelper.IsKanjiOrIterationMark(c) || JapaneseTextHelper.IsHiragana(c));
}
