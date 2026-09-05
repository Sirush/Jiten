using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jiten.Core.Services;

/// <summary>
/// Natural-language media search over parent deck descriptions. Vectors live in
/// DeckDescriptionEmbeddings and are held in RAM; a query is embedded on the fly and ranked
/// by brute-force cosine plus a lexical boost for keywords found verbatim in the description.
/// </summary>
public class DescriptionSearchService(
    IDbContextFactory<JitenDbContext> contextFactory,
    Func<SentenceEmbedder?> embedderFactory,
    ILogger<DescriptionSearchService> logger)
{
    /// <summary>Shorter descriptions are provider stubs ("No synopsis yet") and embed as noise.</summary>
    public const int MinDescriptionLength = 80;

    /// <summary>Forward-pass batch size for the sync job; bounded by padding waste, not memory.</summary>
    private const int EmbedBatchSize = 32;

    /// <summary>Full keyword coverage adds this much to a cosine that sits in the 0.45 to 0.70 band.</summary>
    private const float KeywordBoost = 0.12f;

    /// <summary>Below this the model has nothing to say about the query; results would be the centre of the space.</summary>
    private const float AbsoluteFloor = 0.50f;

    /// <summary>Results this far below the best one are padding, not matches.</summary>
    private const float RelativeMargin = 0.10f;

    public sealed record Match(int DeckId, float Score, float Cosine, float KeywordCoverage, string[] KeywordsHit);

    private volatile Dictionary<int, float[]> _vectors = new();
    private volatile Dictionary<int, string> _foldedTexts = new();
    private readonly Lazy<SentenceEmbedder?> _embedder = new(embedderFactory, LazyThreadSafetyMode.ExecutionAndPublication);

    public int VectorCount => _vectors.Count;
    public bool IsAvailable => _embedder.Value != null;

    public async Task<int> LoadFromDbAsync()
    {
        var modelName = _embedder.Value?.ModelName;
        if (modelName == null)
            return 0;

        await using var context = await contextFactory.CreateDbContextAsync();
        var rows = await context.DeckDescriptionEmbeddings.AsNoTracking()
                                .Where(e => e.Model == modelName)
                                .Select(e => new { e.DeckId, e.Vector })
                                .ToListAsync();
        var vectors = new Dictionary<int, float[]>(rows.Count);
        foreach (var row in rows)
            vectors[row.DeckId] = BytesToFloats(row.Vector);

        var ids = vectors.Keys.ToList();
        var descriptions = await context.Decks.AsNoTracking()
                                        .Where(d => ids.Contains(d.DeckId) && d.Description != null)
                                        .Select(d => new { d.DeckId, d.Description })
                                        .ToListAsync();
        var folded = new Dictionary<int, string>(descriptions.Count);
        foreach (var d in descriptions)
            folded[d.DeckId] = DescriptionKeywords.Fold(d.Description!);

        _vectors = vectors;
        _foldedTexts = folded;
        return vectors.Count;
    }

    /// <summary>
    /// Embeds every parent deck whose description changed since its stored hash (or has no vector),
    /// and drops vectors for decks whose description was removed. Returns (embedded, removed).
    /// </summary>
    public async Task<(int Embedded, int Removed)> SyncAsync(bool force = false, CancellationToken ct = default)
    {
        var embedder = _embedder.Value;
        if (embedder == null)
        {
            logger.LogWarning("DescriptionSearchService: embedding model not available, skipping sync");
            return (0, 0);
        }

        List<(int DeckId, string Description)> candidates;
        Dictionary<int, (string Hash, string Model)> existing;
        await using (var context = await contextFactory.CreateDbContextAsync())
        {
            candidates = (await context.Decks.AsNoTracking()
                                       .Where(d => d.ParentDeckId == null && d.Description != null && d.Description.Length >= MinDescriptionLength)
                                       .Select(d => new { d.DeckId, d.Description })
                                       .ToListAsync(ct))
                         .Select(d => (d.DeckId, d.Description!))
                         .ToList();
            existing = await context.DeckDescriptionEmbeddings.AsNoTracking()
                                    .ToDictionaryAsync(e => e.DeckId, e => (e.TextHash, e.Model), ct);
        }

        var candidateIds = candidates.Select(c => c.DeckId).ToHashSet();
        var stale = existing.Keys.Where(id => !candidateIds.Contains(id)).ToList();

        var work = new List<(int DeckId, string Text, string Hash)>();
        foreach (var (deckId, description) in candidates)
        {
            var text = NormalizeDescription(description);
            var hash = SentenceEmbedder.HashText(text);
            if (!force && existing.TryGetValue(deckId, out var row) && row.Hash == hash && row.Model == embedder.ModelName)
                continue;
            work.Add((deckId, text, hash));
        }

        logger.LogInformation("DescriptionSearchService: {Work} descriptions to embed, {Stale} stale vectors to drop", work.Count, stale.Count);

        var merged = new Dictionary<int, float[]>(_vectors);
        var mergedTexts = new Dictionary<int, string>(_foldedTexts);
        foreach (var id in stale)
        {
            merged.Remove(id);
            mergedTexts.Remove(id);
        }

        for (var offset = 0; offset < work.Count; offset += EmbedBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = work.Skip(offset).Take(EmbedBatchSize).ToList();
            var vectors = embedder.EmbedPassages(batch.Select(b => b.Text).ToList());

            await using var context = await contextFactory.CreateDbContextAsync();
            var ids = batch.Select(b => b.DeckId).ToList();
            await context.DeckDescriptionEmbeddings.Where(e => ids.Contains(e.DeckId)).ExecuteDeleteAsync(ct);
            for (var i = 0; i < batch.Count; i++)
            {
                context.DeckDescriptionEmbeddings.Add(new DeckDescriptionEmbedding
                {
                    DeckId = batch[i].DeckId,
                    Vector = FloatsToBytes(vectors[i]),
                    TextHash = batch[i].Hash,
                    Model = embedder.ModelName
                });
                merged[batch[i].DeckId] = vectors[i];
                mergedTexts[batch[i].DeckId] = DescriptionKeywords.Fold(batch[i].Text);
            }

            await context.SaveChangesAsync(ct);
        }

        if (stale.Count > 0)
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            await context.DeckDescriptionEmbeddings.Where(e => stale.Contains(e.DeckId)).ExecuteDeleteAsync(ct);
        }

        _vectors = merged;
        _foldedTexts = mergedTexts;
        return (work.Count, stale.Count);
    }

    /// <summary>Loads the model ahead of the first query so that request never pays the multi-second startup.</summary>
    public void EnsureModelLoaded() => _ = _embedder.Value;

    /// <summary>
    /// Ranks embedded decks against the query. <paramref name="allowedDeckIds"/> restricts the pool before
    /// ranking: filtering afterwards starves narrow pools, since same-language descriptions dominate the top ranks.
    /// The list is cut where scores stop meaning anything, so a query the model cannot read returns few or no rows.
    /// </summary>
    public List<Match> Search(string query, int limit, IReadOnlySet<int>? allowedDeckIds = null, bool cutNoise = true)
    {
        var embedder = _embedder.Value;
        var vectors = _vectors;
        if (embedder == null || vectors.Count == 0 || string.IsNullOrWhiteSpace(query))
            return [];

        var q = embedder.EmbedQuery(DescriptionQueryGlossary.Expand(query.Trim()));
        var keywords = DescriptionKeywords.Extract(query);
        var texts = _foldedTexts;
        // Rarity-weighted: a hit on "onmyoji" (two descriptions) outweighs a hit on "girl" (thousands).
        var weights = DescriptionKeywords.RarityWeights(keywords, texts.Values);
        var scored = new List<Match>(allowedDeckIds?.Count ?? vectors.Count);
        foreach (var (deckId, v) in vectors)
        {
            if (allowedDeckIds != null && !allowedDeckIds.Contains(deckId))
                continue;
            var cosine = Dot(q, v);
            var hits = keywords.Count > 0 && texts.TryGetValue(deckId, out var text) ? DescriptionKeywords.Hits(keywords, text) : [];
            var coverage = DescriptionKeywords.WeightedCoverage(hits, weights);
            scored.Add(new Match(deckId, cosine + KeywordBoost * coverage, cosine, coverage, hits));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (cutNoise && scored.Count > 0)
        {
            var floor = Math.Max(AbsoluteFloor, scored[0].Score - RelativeMargin);
            var keep = scored.FindIndex(m => m.Score < floor);
            if (keep >= 0)
                scored.RemoveRange(keep, scored.Count - keep);
        }

        if (scored.Count > limit)
            scored.RemoveRange(limit, scored.Count - limit);
        return scored;
    }

    /// <summary>Collapses provider line breaks so formatting alone never changes the hash.</summary>
    private static string NormalizeDescription(string description)
    {
        var text = description.Replace("\r\n", "\n").Replace('\n', ' ');
        while (text.Contains("  "))
            text = text.Replace("  ", " ");
        return text.Trim();
    }

    private static float Dot(float[] a, float[] b)
    {
        var sum = 0f;
        for (var i = 0; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }

    private static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, floats.Length * sizeof(float));
        return floats;
    }
}
