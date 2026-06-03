using System.Runtime.CompilerServices;
using FastText.NetWrapper;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jiten.Core.Services;

/// <summary>
/// Builds and serves dense FastText vocabulary vectors for parent decks, used for content-based
/// "similar media" recommendations.
///
/// Each deck is the TF-IDF-weighted average of its content words' FastText vectors, post-processed
/// with SIF / "all-but-the-top" (subtract the dataset mean and project out the top
/// <see cref="RemovedComponents"/> principal components — otherwise every deck vector collapses onto
/// the embedding space's common direction and all cosine similarities saturate near 1), then
/// L2-normalized so cosine == dot product.
///
/// Vectors live in Postgres (DeckEmbeddings) so they survive restarts and load into RAM at startup
/// without the FastText model. The fitted SIF transform + IDF (DeckEmbeddingSpace) is persisted so
/// decks added/reparsed between full rebuilds can be embedded into the same space incrementally.
/// Similarity (single-deck and arbitrary-set) is computed on the fly from the in-memory vectors.
/// </summary>
public class DeckVectorService
{
    /// <summary>Configuration key for the fastText .bin model path, shared by the job and the CLI.</summary>
    public const string ModelPathConfigKey = "FastTextModelPath";

    /// <summary>Decks with fewer content words than this produce noisy vectors and are skipped.</summary>
    public const int MinContentWords = 50;

    /// <summary>
    /// Size of a deck's distinctive-vocabulary signature (top-N content words by IDF). Large enough to
    /// cover the full vocabulary of decks in the gated short regime (&lt; ~1,300 content words), so the
    /// overlap metric matches the true distinctive overlap there rather than a truncated approximation.
    /// </summary>
    public const int SignatureSize = 1024;

    /// <summary>
    /// A word must be at least this distinctive (IDF) to enter a signature. Below it the word is
    /// common vocabulary shared by most dialogue decks (別れる/気持ち ≈ 1.0) and only produces false
    /// overlap; genuine franchise terms sit far above (筋斗雲 6.2, ブルマ 3.4). A deck with no word
    /// above the floor gets an empty signature and correctly matches nothing.
    /// </summary>
    public const float SignatureMinIdf = 2.0f;

    // --- Short-regime gating (calibrated from CLI `--similar-to --explain`; see DeckVectorSimilarity plan).
    /// <summary>Below this unique-word count the FastText cosine saturates and is gated by signature overlap.</summary>
    public const int ShortRegimeUniqueWords = 1500;
    /// <summary>Minimum distinctive-vocabulary overlap (shared mass / source mass) for a gated match.</summary>
    public const float DefaultOverlapFloor = 0.06f;
    /// <summary>Minimum count of shared distinctive words — cheap guard against single-word flukes.</summary>
    public const int MinSharedDistinctiveWords = 3;
    /// <summary>
    /// A gated match must share at least one word this distinctive (IDF) — an "anchor" like a character
    /// or entity name (孫悟空 4.3, 赤木 4.4) that effectively only occurs within one franchise. Generic
    /// action vocabulary (惑星 2.5, 暗黒 2.3) sits below it, so a match built only on generic words (a DB
    /// movie ↔ a Sailor Moon movie) is rejected even when it shares several such words.
    /// </summary>
    public const float MinAnchorIdf = 3.0f;
    /// <summary>Cosine candidates to over-fetch before the overlap filter, so mid-ranked real matches survive.</summary>
    public const int GatedOverFetch = 300;

    /// <summary>
    /// Top principal components removed (SIF / all-but-the-top). Empirically 3 is the sweet spot for
    /// 300-d cc.ja vectors — de-saturates cosine while keeping themes tight (≈ Mu &amp; Viswanath D≈d/100).
    /// </summary>
    private const int RemovedComponents = 3;

    private readonly IDbContextFactory<JitenDbContext> _contextFactory;
    private readonly ILogger<DeckVectorService> _logger;

    private volatile Dictionary<int, float[]> _vectors = new();
    private volatile Dictionary<int, int[]> _signatures = new();
    private volatile int _dimension;
    private volatile float[]? _mean;
    private volatile float[][] _components = [];
    private volatile Dictionary<int, float> _idf = new();

    public DeckVectorService(IDbContextFactory<JitenDbContext> contextFactory, ILogger<DeckVectorService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public int VectorCount => _vectors.Count;
    public int Dimension => _dimension;
    public bool TryGetVector(int deckId, out float[] vector) => _vectors.TryGetValue(deckId, out vector!);

    /// <summary>
    /// Full rebuild: builds dense FastText deck vectors from the database and the model at
    /// <paramref name="modelPath"/>, fits the SIF transform, and replaces the in-memory state.
    /// </summary>
    public async Task ComputeAsync(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            throw new FileNotFoundException($"FastText model not found: '{modelPath}'");

        var (contentWordIds, idf, n) = await ComputeIdfAndContentWordsAsync();
        if (n == 0)
        {
            _logger.LogWarning("DeckVectorService: no parent decks found, nothing to compute");
            return;
        }

        Dictionary<int, string> wordTexts;
        await using (var context = await _contextFactory.CreateDbContextAsync())
        {
            context.ChangeTracker.AutoDetectChangesEnabled = false;
            wordTexts = await LoadWordTextsAsync(context, contentWordIds);
        }

        _logger.LogInformation("DeckVectorService: loading FastText model {Path}", modelPath);
        using var ft = new FastTextWrapper();
        ft.LoadModel(modelPath);
        var dim = ft.GetModelDimension();
        var wordVecs = BuildWordVectors(ft, wordTexts, idf);

        _logger.LogInformation("DeckVectorService: {Count} word vectors (dim {Dim}), building deck vectors", wordVecs.Count, dim);
        var deckIds = new List<int>();
        var rawVectors = new List<float[]>();
        var rawSignatures = new List<int[]>();
        await foreach (var (deckId, words) in StreamDeckContentWordsAsync(contentWordIds))
        {
            if (words.Count < MinContentWords)
                continue;
            var acc = BuildRawVector(words, idf, wordVecs, dim);
            if (acc == null)
                continue;
            deckIds.Add(deckId);
            rawVectors.Add(acc);
            rawSignatures.Add(BuildSignature(words, idf));
        }

        var (mean, components) = FitAndApplySif(rawVectors, dim, RemovedComponents);

        var vectors = new Dictionary<int, float[]>(deckIds.Count);
        var signatures = new Dictionary<int, int[]>(deckIds.Count);
        for (var i = 0; i < deckIds.Count; i++)
            if (L2Normalize(rawVectors[i]))
            {
                vectors[deckIds[i]] = rawVectors[i];
                signatures[deckIds[i]] = rawSignatures[i];
            }

        _idf = idf;
        _mean = mean;
        _components = components;
        _dimension = dim;
        _vectors = vectors;
        _signatures = signatures;
        _logger.LogInformation("DeckVectorService: built {Vectors} deck vectors (dim {Dim}, removed {K} components) from {N} parent decks",
            vectors.Count, dim, RemovedComponents, n);
    }

    /// <summary>
    /// Incremental: (re)embeds the given decks into the existing space using the persisted SIF
    /// transform + IDF, and upserts them to the DB and the in-memory cache. No-op for decks that
    /// don't qualify (too few content words). Requires a prior full build (transform must exist).
    /// </summary>
    public async Task<int> EmbedDecksAsync(string modelPath, IReadOnlyCollection<int> deckIds)
    {
        if (deckIds.Count == 0)
            return 0;
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            throw new FileNotFoundException($"FastText model not found: '{modelPath}'");

        if (_mean == null || _dimension == 0 || _idf.Count == 0)
        {
            if (!await LoadFromDbAsync())
            {
                _logger.LogWarning("DeckVectorService: cannot embed decks before a full rebuild has run (no transform)");
                return 0;
            }
        }

        var idf = _idf;
        var contentWordIds = idf.Keys.ToHashSet();

        Dictionary<int, List<(int WordId, long Occ)>> deckWords;
        Dictionary<int, string> wordTexts;
        await using (var context = await _contextFactory.CreateDbContextAsync())
        {
            context.ChangeTracker.AutoDetectChangesEnabled = false;
            deckWords = await GetDeckContentWordsAsync(context, deckIds, contentWordIds);
            var neededWordIds = deckWords.Values.SelectMany(w => w.Select(x => x.WordId)).ToHashSet();
            wordTexts = await LoadWordTextsForAsync(context, neededWordIds);
        }

        var dim = _dimension;
        using var ft = new FastTextWrapper();
        ft.LoadModel(modelPath);
        var wordVecs = BuildWordVectors(ft, wordTexts, idf);

        var embedded = new Dictionary<int, float[]>();
        var embeddedSignatures = new Dictionary<int, int[]>();
        foreach (var (deckId, words) in deckWords)
        {
            if (words.Count < MinContentWords)
                continue;
            var raw = BuildRawVector(words, idf, wordVecs, dim);
            if (raw == null)
                continue;
            ApplySif(raw, _mean!, _components);
            if (L2Normalize(raw))
            {
                embedded[deckId] = raw;
                embeddedSignatures[deckId] = BuildSignature(words, idf);
            }
        }

        if (embedded.Count > 0)
        {
            await UpsertEmbeddingsAsync(embedded, embeddedSignatures);

            // Swap new dictionaries so readers never see a torn state.
            var merged = new Dictionary<int, float[]>(_vectors);
            var mergedSigs = new Dictionary<int, int[]>(_signatures);
            foreach (var (id, vec) in embedded)
                merged[id] = vec;
            foreach (var (id, sig) in embeddedSignatures)
                mergedSigs[id] = sig;
            _vectors = merged;
            _signatures = mergedSigs;
        }

        _logger.LogInformation("DeckVectorService: incrementally embedded {Count}/{Requested} decks", embedded.Count, deckIds.Count);
        return embedded.Count;
    }

    private static Dictionary<int, float[]> BuildWordVectors(FastTextWrapper ft, Dictionary<int, string> wordTexts, Dictionary<int, float> idf)
    {
        var wordVecs = new Dictionary<int, float[]>(wordTexts.Count);
        foreach (var (wordId, text) in wordTexts)
        {
            if (string.IsNullOrWhiteSpace(text) || !idf.ContainsKey(wordId))
                continue;
            var vec = ft.GetWordVector(text);
            if (vec is { Length: > 0 })
                wordVecs[wordId] = vec;
        }

        return wordVecs;
    }

    private static float[]? BuildRawVector(List<(int WordId, long Occ)> words, Dictionary<int, float> idf, Dictionary<int, float[]> wordVecs, int dim)
    {
        var acc = new float[dim];
        var any = false;
        foreach (var (wordId, occ) in words)
        {
            if (!idf.TryGetValue(wordId, out var wordIdf) || !wordVecs.TryGetValue(wordId, out var vec))
                continue;
            var weight = (float)((1.0 + Math.Log(occ)) * wordIdf);
            for (var k = 0; k < dim; k++)
                acc[k] += weight * vec[k];
            any = true;
        }

        return any ? acc : null;
    }

    /// <summary>
    /// A deck's distinctive-vocabulary signature: its top-<see cref="SignatureSize"/> content words
    /// by IDF, returned as WordIds sorted ascending (for merge-intersection at query time). Dropping
    /// the low-IDF (generic) words is deliberate — it's the distinctive vocabulary that separates a
    /// real franchise match from a short-deck cosine artifact.
    /// </summary>
    private static int[] BuildSignature(List<(int WordId, long Occ)> words, Dictionary<int, float> idf)
    {
        return words.Where(w => idf.TryGetValue(w.WordId, out var v) && v >= SignatureMinIdf)
                    .OrderByDescending(w => idf[w.WordId])
                    .Take(SignatureSize)
                    .Select(w => w.WordId)
                    .OrderBy(w => w)
                    .ToArray();
    }

    // ---------------------------------------------------------------------------------------------
    // Shared corpus queries
    // ---------------------------------------------------------------------------------------------

    /// <summary>Builds the content-word set, the smooth-IDF table (word weights), and the parent-deck count.</summary>
    public async Task<(HashSet<int> ContentWordIds, Dictionary<int, float> Idf, int ParentDeckCount)> ComputeIdfAndContentWordsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        context.ChangeTracker.AutoDetectChangesEnabled = false;

        var contentWordIds = await LoadContentWordIdsAsync(context);
        _logger.LogInformation("DeckVectorService: {Count} content words in dictionary", contentWordIds.Count);

        var n = await context.Decks.AsNoTracking().CountAsync(d => d.ParentDeckId == null);
        if (n == 0)
            return (contentWordIds, new Dictionary<int, float>(), 0);

        var idf = await ComputeIdfAsync(context, contentWordIds, n);
        return (contentWordIds, idf, n);
    }

    /// <summary>
    /// Streams parent decks one at a time, each with its content-word occurrence counts
    /// (aggregated across reading indexes). Holds only one deck's words in memory at a time.
    /// </summary>
    public async IAsyncEnumerable<(int DeckId, List<(int WordId, long Occ)> Words)> StreamDeckContentWordsAsync(
        HashSet<int> contentWordIds, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        context.ChangeTracker.AutoDetectChangesEnabled = false;

        const string sql =
            """
            SELECT dw."DeckId" AS "DeckId", dw."WordId" AS "WordId", SUM(dw."Occurrences") AS "Occ"
            FROM "jiten"."DeckWords" dw
            JOIN "jiten"."Decks" d ON d."DeckId" = dw."DeckId" AND d."ParentDeckId" IS NULL
            GROUP BY dw."DeckId", dw."WordId"
            ORDER BY dw."DeckId"
            """;

        var current = new List<(int, long)>();
        var currentDeckId = -1;

        var rows = context.Database.SqlQueryRaw<DeckWordAggRow>(sql).AsAsyncEnumerable();
        await foreach (var row in rows.WithCancellation(ct))
        {
            if (row.DeckId != currentDeckId)
            {
                if (currentDeckId >= 0)
                    yield return (currentDeckId, current);
                current = new List<(int, long)>();
                currentDeckId = row.DeckId;
            }

            if (row.Occ > 0 && contentWordIds.Contains(row.WordId))
                current.Add((row.WordId, row.Occ));
        }

        if (currentDeckId >= 0)
            yield return (currentDeckId, current);
    }

    private static async Task<Dictionary<int, List<(int WordId, long Occ)>>> GetDeckContentWordsAsync(
        JitenDbContext context, IReadOnlyCollection<int> deckIds, HashSet<int> contentWordIds)
    {
        var rows = await context.DeckWords.AsNoTracking()
                                .Where(dw => deckIds.Contains(dw.DeckId))
                                .GroupBy(dw => new { dw.DeckId, dw.WordId })
                                .Select(g => new { g.Key.DeckId, g.Key.WordId, Occ = (long)g.Sum(x => x.Occurrences) })
                                .ToListAsync();

        var result = new Dictionary<int, List<(int, long)>>();
        foreach (var r in rows)
        {
            if (r.Occ <= 0 || !contentWordIds.Contains(r.WordId))
                continue;
            if (!result.TryGetValue(r.DeckId, out var list))
                result[r.DeckId] = list = new List<(int, long)>();
            list.Add((r.WordId, r.Occ));
        }

        return result;
    }

    private static async Task<HashSet<int>> LoadContentWordIdsAsync(JitenDbContext context)
    {
        var result = new HashSet<int>();
        var rows = context.JMDictWords.AsNoTracking().Select(w => new { w.WordId, w.PartsOfSpeech }).AsAsyncEnumerable();
        await foreach (var row in rows)
        {
            var mask = PosMask.FromList(row.PartsOfSpeech.ToPartOfSpeech());
            if ((mask & PosMask.ContentWord) != 0)
                result.Add(row.WordId);
        }

        return result;
    }

    private async Task<Dictionary<int, float>> ComputeIdfAsync(JitenDbContext context, HashSet<int> contentWordIds, int n)
    {
        const string sql =
            """
            SELECT dw."WordId" AS "WordId", COUNT(DISTINCT dw."DeckId") AS "Df"
            FROM "jiten"."DeckWords" dw
            JOIN "jiten"."Decks" d ON d."DeckId" = dw."DeckId" AND d."ParentDeckId" IS NULL
            GROUP BY dw."WordId"
            """;

        var idf = new Dictionary<int, float>();
        var rows = context.Database.SqlQueryRaw<DfRow>(sql).AsAsyncEnumerable();
        await foreach (var row in rows)
        {
            if (row.Df <= 0 || !contentWordIds.Contains(row.WordId))
                continue;
            idf[row.WordId] = (float)Math.Log(1.0 + (double)n / row.Df);
        }

        return idf;
    }

    /// <summary>Primary surface text (kanji form preferred, else lowest-index kana) for all content words.</summary>
    private static async Task<Dictionary<int, string>> LoadWordTextsAsync(JitenDbContext context, HashSet<int> wordIds)
    {
        var best = new Dictionary<int, (short Idx, string Text, JmDictFormType Type)>(wordIds.Count);
        var rows = context.WordForms.AsNoTracking()
                          .Select(f => new { f.WordId, f.ReadingIndex, f.Text, f.FormType })
                          .AsAsyncEnumerable();
        await foreach (var f in rows)
        {
            if (wordIds.Contains(f.WordId))
                KeepBestForm(best, f.WordId, f.ReadingIndex, f.Text, f.FormType);
        }

        return best.ToDictionary(kv => kv.Key, kv => kv.Value.Text);
    }

    /// <summary>Primary surface text for a small id set (targeted query for incremental embedding).</summary>
    private static async Task<Dictionary<int, string>> LoadWordTextsForAsync(JitenDbContext context, HashSet<int> wordIds)
    {
        if (wordIds.Count == 0)
            return new Dictionary<int, string>();

        var ids = wordIds.ToList();
        var forms = await context.WordForms.AsNoTracking()
                                 .Where(f => ids.Contains(f.WordId))
                                 .Select(f => new { f.WordId, f.ReadingIndex, f.Text, f.FormType })
                                 .ToListAsync();

        var best = new Dictionary<int, (short Idx, string Text, JmDictFormType Type)>(wordIds.Count);
        foreach (var f in forms)
            KeepBestForm(best, f.WordId, f.ReadingIndex, f.Text, f.FormType);

        return best.ToDictionary(kv => kv.Key, kv => kv.Value.Text);
    }

    /// <summary>Keeps the preferred surface form per word (kanji form first, then lowest reading index).</summary>
    private static void KeepBestForm(Dictionary<int, (short Idx, string Text, JmDictFormType Type)> best,
                                     int wordId, short idx, string text, JmDictFormType type)
    {
        if (!best.TryGetValue(wordId, out var cur) || IsBetterForm(type, idx, cur.Type, cur.Idx))
            best[wordId] = (idx, text, type);
    }

    private static bool IsBetterForm(JmDictFormType type, short idx, JmDictFormType curType, short curIdx)
    {
        var isKanji = type == JmDictFormType.KanjiForm;
        var curIsKanji = curType == JmDictFormType.KanjiForm;
        if (isKanji != curIsKanji)
            return isKanji;
        return idx < curIdx;
    }

    // ---------------------------------------------------------------------------------------------
    // SIF transform
    // ---------------------------------------------------------------------------------------------

    /// <summary>Fits the SIF transform on the raw vectors, applies it in place, and returns (mean, components).</summary>
    private static (float[] Mean, float[][] Components) FitAndApplySif(List<float[]> vectors, int dim, int removeComponents)
    {
        var mean = new float[dim];
        if (vectors.Count == 0)
            return (mean, []);

        var sum = new double[dim];
        foreach (var v in vectors)
            for (var k = 0; k < dim; k++)
                sum[k] += v[k];
        for (var k = 0; k < dim; k++)
            mean[k] = (float)(sum[k] / vectors.Count);

        foreach (var v in vectors)
            for (var k = 0; k < dim; k++)
                v[k] -= mean[k];

        var components = removeComponents > 0 ? TopPrincipalComponents(vectors, dim, removeComponents) : [];
        foreach (var u in components)
            foreach (var v in vectors)
                ProjectOut(v, u);

        return (mean, components);
    }

    /// <summary>Applies an already-fitted transform (subtract mean, project out components) to one vector.</summary>
    private static void ApplySif(float[] vec, float[] mean, float[][] components)
    {
        for (var k = 0; k < vec.Length; k++)
            vec[k] -= mean[k];
        foreach (var u in components)
            ProjectOut(vec, u);
    }

    private static void ProjectOut(float[] v, float[] u)
    {
        double dot = 0;
        for (var k = 0; k < v.Length; k++)
            dot += v[k] * u[k];
        for (var k = 0; k < v.Length; k++)
            v[k] -= (float)(dot * u[k]);
    }

    private static float[][] TopPrincipalComponents(List<float[]> vectors, int dim, int k)
    {
        var c = new double[dim, dim];
        foreach (var v in vectors)
            for (var a = 0; a < dim; a++)
            {
                double va = v[a];
                if (va == 0) continue;
                for (var b = 0; b < dim; b++)
                    c[a, b] += va * v[b];
            }

        var evd = Matrix<double>.Build.DenseOfArray(c).Evd(Symmetricity.Symmetric);
        var eigenVectors = evd.EigenVectors; // columns ordered by ascending eigenvalue

        var result = new float[Math.Min(k, dim)][];
        for (var i = 0; i < result.Length; i++)
        {
            var col = dim - 1 - i;
            var u = new float[dim];
            for (var r = 0; r < dim; r++)
                u[r] = (float)eigenVectors[r, col];
            result[i] = u;
        }

        return result;
    }

    private static bool L2Normalize(float[] vec)
    {
        double sumSq = 0;
        foreach (var v in vec)
            sumSq += (double)v * v;
        if (sumSq <= 0)
            return false;

        var norm = (float)Math.Sqrt(sumSq);
        for (var i = 0; i < vec.Length; i++)
            vec[i] /= norm;
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Runtime queries (on the fly from in-memory vectors)
    // ---------------------------------------------------------------------------------------------

    /// <summary>Top-<paramref name="limit"/> decks most similar to <paramref name="deckId"/> (top-K selection, no full sort).</summary>
    public List<(int DeckId, float Similarity)> FindSimilar(int deckId, int limit)
    {
        if (!_vectors.TryGetValue(deckId, out var source))
            return [];
        return TopK(source, limit, id => id == deckId);
    }

    /// <summary>
    /// Recommendation entry point: picks the right similarity strategy for the deck. Short/saturated-regime
    /// source decks (few unique words, raw cosine unreliable) are gated by distinctive-vocabulary overlap;
    /// everything else takes the cheap pure-cosine path. Callers don't need to know the gating internals.
    /// </summary>
    public async Task<List<(int DeckId, float Similarity)>> FindSimilarForAsync(int deckId, int limit)
    {
        if (!_vectors.ContainsKey(deckId))
            return [];

        var srcUnique = await GetUniqueWordCountAsync(deckId);
        if (srcUnique is > 0 and < ShortRegimeUniqueWords && _signatures.ContainsKey(deckId))
        {
            return FindSimilarGated(deckId, limit, Math.Max(limit, GatedOverFetch))
                   .Select(g => (g.DeckId, g.Similarity)).ToList();
        }

        return FindSimilar(deckId, limit);
    }

    private async Task<int?> GetUniqueWordCountAsync(int deckId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Decks.AsNoTracking()
                            .Where(d => d.DeckId == deckId)
                            .Select(d => (int?)d.UniqueWordCount)
                            .FirstOrDefaultAsync();
    }

    /// <summary>IDF weight of a word (0 if unknown). Diagnostic helper.</summary>
    public float Idf(int wordId) => _idf.GetValueOrDefault(wordId);

    /// <summary>The distinctive words shared by two decks' signatures, most distinctive first. Diagnostic helper.</summary>
    public IReadOnlyList<int> SharedSignatureWords(int sourceDeckId, int candidateDeckId)
    {
        if (!_signatures.TryGetValue(sourceDeckId, out var a) || !_signatures.TryGetValue(candidateDeckId, out var b))
            return [];

        var shared = new List<int>();
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { shared.Add(a[i]); i++; j++; }
            else if (a[i] < b[j]) i++;
            else j++;
        }

        var idf = _idf;
        shared.Sort((x, y) => idf.GetValueOrDefault(y).CompareTo(idf.GetValueOrDefault(x)));
        return shared;
    }

    /// <summary>
    /// Fraction of <paramref name="sourceDeckId"/>'s distinctive-vocabulary IDF mass that is shared
    /// with <paramref name="candidateDeckId"/> (signature intersection, in RAM). 0 if either lacks a
    /// signature. High for real franchise matches, near-zero for short-deck cosine artifacts.
    /// </summary>
    public float SignatureOverlap(int sourceDeckId, int candidateDeckId)
    {
        if (!_signatures.TryGetValue(sourceDeckId, out var a) || !_signatures.TryGetValue(candidateDeckId, out var b))
            return 0f;
        return OverlapAndCount(a, b, _idf, SignatureMass(a, _idf)).Overlap;
    }

    /// <summary>Total IDF mass of a signature's words. Hoisted so a gated query computes the source mass once.</summary>
    private static float SignatureMass(int[] sig, Dictionary<int, float> idf)
    {
        float mass = 0f;
        foreach (var w in sig)
            mass += idf.GetValueOrDefault(w);
        return mass;
    }

    /// <summary>
    /// IDF-weighted overlap of two ascending-sorted signatures (shared mass / <paramref name="sourceMass"/>)
    /// together with the count of shared distinctive words. The count guards against tiny signatures producing
    /// a high percentage from a single coincidental word — a real franchise match shares many distinctive words.
    /// </summary>
    private static (float Overlap, int SharedCount, float PeakIdf) OverlapAndCount(int[] a, int[] b, Dictionary<int, float> idf, float sourceMass)
    {
        float shared = 0f, peak = 0f;
        int count = 0, i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j])
            {
                var wordIdf = idf.GetValueOrDefault(a[i]);
                shared += wordIdf;
                if (wordIdf > peak) peak = wordIdf;
                count++; i++; j++;
            }
            else if (a[i] < b[j]) i++;
            else j++;
        }

        return (sourceMass > 0f ? shared / sourceMass : 0f, count, peak);
    }

    /// <summary>
    /// Like <see cref="FindSimilar"/> but for short/saturated-regime source decks where the raw cosine
    /// is unreliable: over-fetches <paramref name="overFetch"/> by cosine, keeps only candidates whose
    /// distinctive-vocabulary overlap clears <paramref name="overlapFloor"/>, and ranks the survivors by
    /// that overlap (cosine breaks ties). Returns up to <paramref name="limit"/>; empty if none clear it.
    /// </summary>
    public List<(int DeckId, float Similarity, float Overlap, int SharedCount)> FindSimilarGated(
        int deckId, int limit, int overFetch = GatedOverFetch, float overlapFloor = DefaultOverlapFloor,
        int minSharedWords = MinSharedDistinctiveWords, float minAnchorIdf = MinAnchorIdf)
    {
        if (!_vectors.TryGetValue(deckId, out var source) || !_signatures.TryGetValue(deckId, out var srcSig))
            return [];

        var srcMass = SignatureMass(srcSig, _idf);
        var pool = TopK(source, overFetch, id => id == deckId);
        var survivors = new List<(int DeckId, float Similarity, float Overlap, int SharedCount)>();
        foreach (var (candId, sim) in pool)
        {
            if (!_signatures.TryGetValue(candId, out var candSig))
                continue;
            var (overlap, count, peak) = OverlapAndCount(srcSig, candSig, _idf, srcMass);
            if (overlap >= overlapFloor && count >= minSharedWords && peak >= minAnchorIdf)
                survivors.Add((candId, sim, overlap, count));
        }

        survivors.Sort((x, y) =>
        {
            var byOverlap = y.Overlap.CompareTo(x.Overlap);
            return byOverlap != 0 ? byOverlap : y.Similarity.CompareTo(x.Similarity);
        });

        if (survivors.Count > limit)
            survivors.RemoveRange(limit, survivors.Count - limit);
        return survivors;
    }

    /// <summary>Diagnostic: shared content-word overlap between a source deck and candidates.</summary>
    public sealed record DeckOverlap(
        int DeckId,
        int SharedCount,
        int TargetContentWords,
        float DistinctiveOverlap,
        IReadOnlyList<(string Text, float Idf)> TopShared);

    /// <summary>
    /// Diagnostic for spurious similarity: for each candidate, the count of shared content words,
    /// the fraction of the source deck's distinctive (IDF) mass that is shared, and the top shared
    /// words by IDF. A real match shares distinctive vocabulary; a spurious high-cosine pair does not.
    /// Uses the in-memory IDF table plus one DeckWords query — no model needed.
    /// </summary>
    public async Task<(int SourceContentWords, List<DeckOverlap> Overlaps)> ExplainOverlapAsync(
        int sourceDeckId, IReadOnlyCollection<int> candidateIds, int topN = 8)
    {
        var idf = _idf;
        var contentWordIds = new HashSet<int>(idf.Keys);

        await using var context = await _contextFactory.CreateDbContextAsync();
        context.ChangeTracker.AutoDetectChangesEnabled = false;

        var allIds = candidateIds.Append(sourceDeckId).Distinct().ToList();
        var deckWords = await GetDeckContentWordsAsync(context, allIds, contentWordIds);

        deckWords.TryGetValue(sourceDeckId, out var srcWords);
        srcWords ??= [];
        var srcSet = srcWords.Select(w => w.WordId).ToHashSet();
        var srcIdfSum = srcWords.Sum(w => idf.GetValueOrDefault(w.WordId));

        var perCandidate = new List<(int DeckId, List<int> Shared, int TargetCount)>();
        var neededTexts = new HashSet<int>();
        foreach (var candId in candidateIds)
        {
            deckWords.TryGetValue(candId, out var candWords);
            candWords ??= [];
            var shared = candWords.Where(w => srcSet.Contains(w.WordId)).Select(w => w.WordId).ToList();
            perCandidate.Add((candId, shared, candWords.Count));
            foreach (var wid in shared.OrderByDescending(w => idf.GetValueOrDefault(w)).Take(topN))
                neededTexts.Add(wid);
        }

        var texts = await LoadWordTextsAsync(context, neededTexts);

        var overlaps = new List<DeckOverlap>(perCandidate.Count);
        foreach (var (candId, shared, targetCount) in perCandidate)
        {
            var sharedIdf = shared.Sum(w => idf.GetValueOrDefault(w));
            var top = shared.OrderByDescending(w => idf.GetValueOrDefault(w)).Take(topN)
                            .Select(w => (texts.GetValueOrDefault(w, w.ToString()), idf.GetValueOrDefault(w)))
                            .ToList<(string, float)>();
            overlaps.Add(new DeckOverlap(candId, shared.Count, targetCount,
                srcIdfSum > 0 ? sharedIdf / srcIdfSum : 0f, top));
        }

        return (srcWords.Count, overlaps);
    }

    /// <summary>Maintains a bounded ascending list of the best <paramref name="limit"/> hits — O(N·limit), no full sort.</summary>
    private List<(int DeckId, float Similarity)> TopK(float[] query, int limit, Func<int, bool> exclude)
    {
        if (limit <= 0)
            return [];

        var best = new List<(int DeckId, float Sim)>(limit + 1);
        var worst = float.NegativeInfinity;

        foreach (var (id, vec) in _vectors)
        {
            if (exclude(id))
                continue;

            var sim = Cosine(query, vec);
            if (best.Count == limit && sim <= worst)
                continue;

            var idx = best.FindIndex(x => sim > x.Sim);
            if (idx < 0) idx = best.Count;
            best.Insert(idx, (id, sim));
            if (best.Count > limit)
                best.RemoveAt(best.Count - 1);
            worst = best[^1].Sim;
        }

        return best;
    }

    /// <summary>Cosine similarity of two L2-normalized dense vectors == dot product.</summary>
    public static float Cosine(float[] a, float[] b)
    {
        float dot = 0;
        for (var i = 0; i < a.Length; i++)
            dot += a[i] * b[i];
        return dot;
    }

    // ---------------------------------------------------------------------------------------------
    // Postgres persistence
    // ---------------------------------------------------------------------------------------------

    /// <summary>Persists all in-memory vectors + the fitted transform/IDF to Postgres (bulk replace).</summary>
    public async Task SaveToDbAsync()
    {
        var vectors = _vectors;
        var dim = _dimension;
        if (vectors.Count == 0 || _mean == null)
        {
            _logger.LogWarning("DeckVectorService: nothing to save (no vectors/transform)");
            return;
        }

        await using var context = await _contextFactory.CreateDbContextAsync();
        context.ChangeTracker.AutoDetectChangesEnabled = false;

        var signatures = _signatures;
        await context.DeckEmbeddings.ExecuteDeleteAsync();
        var batch = new List<DeckEmbedding>(10000);
        foreach (var (deckId, vec) in vectors)
        {
            batch.Add(new DeckEmbedding
            {
                DeckId = deckId,
                Vector = FloatsToBytes(vec),
                Signature = signatures.TryGetValue(deckId, out var sig) ? IntsToBytes(sig) : null
            });
            if (batch.Count < 10000) continue;
            context.DeckEmbeddings.AddRange(batch);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            batch.Clear();
        }

        if (batch.Count > 0)
        {
            context.DeckEmbeddings.AddRange(batch);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }

        await context.DeckEmbeddingSpaces.ExecuteDeleteAsync();
        context.DeckEmbeddingSpaces.Add(new DeckEmbeddingSpace
        {
            Id = 1,
            Dimension = dim,
            Mean = FloatsToBytes(_mean),
            Components = FloatsToBytes(_components.SelectMany(c => c).ToArray()),
            Idf = IdfToBytes(_idf)
        });
        await context.SaveChangesAsync();

        _logger.LogInformation("DeckVectorService: saved {Vectors} vectors + transform to Postgres", vectors.Count);
    }

    /// <summary>Loads vectors + transform/IDF from Postgres into RAM. Returns false if no data yet.</summary>
    public async Task<bool> LoadFromDbAsync()
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            context.ChangeTracker.AutoDetectChangesEnabled = false;

            var space = await context.DeckEmbeddingSpaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1);
            if (space == null)
            {
                _logger.LogWarning("DeckVectorService: no embedding space in DB, serving empty");
                return false;
            }

            var dim = space.Dimension;
            var mean = BytesToFloats(space.Mean);
            var flatComponents = BytesToFloats(space.Components);
            var componentCount = dim > 0 ? flatComponents.Length / dim : 0;
            var components = new float[componentCount][];
            for (var i = 0; i < componentCount; i++)
                components[i] = flatComponents[(i * dim)..((i + 1) * dim)];
            var idf = BytesToIdf(space.Idf);

            var vectors = new Dictionary<int, float[]>();
            var signatures = new Dictionary<int, int[]>();
            var rows = context.DeckEmbeddings.AsNoTracking().Select(e => new { e.DeckId, e.Vector, e.Signature }).AsAsyncEnumerable();
            await foreach (var row in rows)
            {
                vectors[row.DeckId] = BytesToFloats(row.Vector);
                if (row.Signature is { Length: > 0 })
                    signatures[row.DeckId] = BytesToInts(row.Signature);
            }

            _dimension = dim;
            _mean = mean;
            _components = components;
            _idf = idf;
            _vectors = vectors;
            _signatures = signatures;
            _logger.LogInformation("DeckVectorService: loaded {Vectors} vectors ({Sigs} signatures, dim {Dim}) from Postgres",
                vectors.Count, signatures.Count, dim);
            return vectors.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeckVectorService: failed to load vectors from Postgres");
            return false;
        }
    }

    private async Task UpsertEmbeddingsAsync(Dictionary<int, float[]> embedded, Dictionary<int, int[]> signatures)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        context.ChangeTracker.AutoDetectChangesEnabled = false;

        var ids = embedded.Keys.ToList();
        await context.DeckEmbeddings.Where(e => ids.Contains(e.DeckId)).ExecuteDeleteAsync();
        context.DeckEmbeddings.AddRange(embedded.Select(kv => new DeckEmbedding
        {
            DeckId = kv.Key,
            Vector = FloatsToBytes(kv.Value),
            Signature = signatures.TryGetValue(kv.Key, out var sig) ? IntsToBytes(sig) : null
        }));
        await context.SaveChangesAsync();
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

    private static byte[] IntsToBytes(int[] ints)
    {
        var bytes = new byte[ints.Length * sizeof(int)];
        Buffer.BlockCopy(ints, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static int[] BytesToInts(byte[] bytes)
    {
        var ints = new int[bytes.Length / sizeof(int)];
        Buffer.BlockCopy(bytes, 0, ints, 0, ints.Length * sizeof(int));
        return ints;
    }

    private static byte[] IdfToBytes(Dictionary<int, float> idf)
    {
        var bytes = new byte[idf.Count * 8];
        var offset = 0;
        foreach (var (wordId, value) in idf)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(offset, 4), wordId);
            BitConverter.TryWriteBytes(bytes.AsSpan(offset + 4, 4), value);
            offset += 8;
        }

        return bytes;
    }

    private static Dictionary<int, float> BytesToIdf(byte[] bytes)
    {
        var idf = new Dictionary<int, float>(bytes.Length / 8);
        for (var offset = 0; offset + 8 <= bytes.Length; offset += 8)
        {
            var wordId = BitConverter.ToInt32(bytes, offset);
            var value = BitConverter.ToSingle(bytes, offset + 4);
            idf[wordId] = value;
        }

        return idf;
    }

    private class DfRow
    {
        public int WordId { get; set; }
        public long Df { get; set; }
    }

    private class DeckWordAggRow
    {
        public int DeckId { get; set; }
        public int WordId { get; set; }
        public long Occ { get; set; }
    }
}
