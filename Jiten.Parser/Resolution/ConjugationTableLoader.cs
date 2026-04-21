using System.Diagnostics;
using Jiten.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jiten.Parser.Resolution;

/// <summary>
/// Loads jmdict."ConjugatedForms" into an in-memory lookup keyed by surface.
/// Used by <see cref="Jiten.Parser.Resegmentation.TableCandidateProvider"/>.
///
/// Always used by the beam when available. The load runs once per process; a
/// second call is a no-op.
///
/// Two load paths:
///   1. Binary file (preferred): packed cache at <c>resources/conjugations.bin</c>.
///      &lt;5 s cold load, ~1.5 GB RAM. Produced by --generate-conjugations.
///   2. Postgres fallback: used when the binary file is missing or a version
///      mismatch. ~80 s cold load because of Npgsql per-row allocations +
///      ORDER BY Surface. Kept so fresh checkouts still work.
///
/// RAM is sensitive to row count (~27M rows). Three dedupe / flatten layers:
///   1. Tag strings (e.g. "past", "(infinitive)") — ~100 unique values across
///      all rows but Npgsql hands us a fresh instance per row. Interned.
///   2. Chain arrays — most rows share one of a few hundred identical chains
///      (every "past" form has chain=["past"]). Interned.
///   3. One backing `Hits[]` array instead of 25M small per-surface arrays.
///      Each surface stores (offset, count) into this flat buffer, eliminating
///      ~24 bytes of array header × 25M surfaces ≈ 600 MB of header overhead.
///
/// Without any of these the loader uses ~9 GB RAM. All together: ~1.5 GB.
/// </summary>
public sealed class ConjugationTable
{
    private readonly Dictionary<string, HitRange> _index;
    private readonly ConjugatedFormHit[] _hits;
    private readonly int[] _maxLenByFirstChar;

    internal ConjugationTable(Dictionary<string, HitRange> index, ConjugatedFormHit[] hits, int[]? maxLenByFirstChar = null)
    {
        _index = index;
        _hits = hits;
        _maxLenByFirstChar = maxLenByFirstChar ?? BuildMaxLenByFirstChar(index);
    }

    private static int[] BuildMaxLenByFirstChar(Dictionary<string, HitRange> index)
    {
        var t = new int[65536];
        foreach (var key in index.Keys)
        {
            if (key.Length == 0) continue;
            int keyLen = Math.Min(key.Length, 16);
            char fc = key[0];
            if (keyLen > t[fc]) t[fc] = keyLen;
        }
        return t;
    }

    public int SurfaceCount => _index.Count;
    public int HitCount => _hits.Length;
    public int[] MaxLenByFirstChar => _maxLenByFirstChar;

    public HashSet<int> GetUniqueWordIds()
    {
        var set = new HashSet<int>();
        foreach (var hit in _hits)
            set.Add(hit.WordId);
        return set;
    }

    /// <summary>
    /// Returns the hits for the given surface as a materialised array.
    /// The array is fresh per call — consumers typically need it as a typed
    /// collection anyway for SurfaceCandidate construction.
    /// </summary>
    public ConjugatedFormHit[] GetHits(string surface)
    {
        if (!_index.TryGetValue(surface, out var range) || range.Count == 0)
            return Array.Empty<ConjugatedFormHit>();
        var result = new ConjugatedFormHit[range.Count];
        Array.Copy(_hits, range.Offset, result, 0, range.Count);
        return result;
    }

    /// <summary>
    /// True if any entry exists for the given surface — cheaper than GetHits
    /// when the caller only needs presence.
    /// </summary>
    public bool ContainsSurface(string surface) =>
        _index.TryGetValue(surface, out var range) && range.Count > 0;

    /// <summary>
    /// Enumerates hits without materialising an array. Useful when the caller
    /// wants to check a condition without allocating.
    /// </summary>
    public ReadOnlySpan<ConjugatedFormHit> GetHitsSpan(string surface)
    {
        if (!_index.TryGetValue(surface, out var range) || range.Count == 0)
            return ReadOnlySpan<ConjugatedFormHit>.Empty;
        return new ReadOnlySpan<ConjugatedFormHit>(_hits, range.Offset, range.Count);
    }

    // Internal accessors used by ConjugationTableBinaryFile to serialise.
    internal ConjugatedFormHit[] HitsBuffer => _hits;
    internal IEnumerable<KeyValuePair<string, HitRange>> EnumerateSurfaces() => _index;

    internal readonly record struct HitRange(int Offset, int Count);
}

public static class ConjugationTableLoader
{
    private static readonly SemaphoreSlim _loadGate = new(1, 1);
    private static ConjugationTable? _table;
    private static long _loadElapsedMs;
    private static long _rowCount;

    public static ConjugationTable? Table => _table;
    public static bool IsLoaded => _table != null;
    public static long LoadElapsedMs => _loadElapsedMs;
    public static long RowCount => _rowCount;

    /// <summary>
    /// Populate the singleton. Tries the binary cache first (fast path), then
    /// falls back to a full Postgres scan. Safe to call concurrently; only the
    /// first caller does work.
    /// </summary>
    public static async Task EnsureLoadedAsync(
        IDbContextFactory<JitenDbContext> contextFactory,
        Action<string>? log = null)
    {
        if (_table != null) return;

        await _loadGate.WaitAsync();
        try
        {
            if (_table != null) return;

            var sw = Stopwatch.StartNew();

            var binPath = ConjugationTableBinaryFile.DefaultPath;
            var fromFile = ConjugationTableBinaryFile.TryRead(binPath, log);
            if (fromFile != null)
            {
                _table = fromFile;
                _rowCount = fromFile.HitCount;
                sw.Stop();
                _loadElapsedMs = sw.ElapsedMilliseconds;
                return;
            }

            (log ?? Console.Error.WriteLine)(
                $"ConjugationTable: no binary cache at {binPath}, falling back to Postgres (slow — run --generate-conjugations to produce the cache)");

            var built = await BuildFromDatabaseAsync(contextFactory, log);
            _table = built;
            _rowCount = built.HitCount;
            sw.Stop();
            _loadElapsedMs = sw.ElapsedMilliseconds;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// Build a fresh ConjugationTable from Postgres without touching the
    /// singleton. Used by --generate-conjugations after a reinsert to dump the
    /// binary cache. Memory profile matches EnsureLoadedAsync's legacy path.
    /// </summary>
    public static async Task<ConjugationTable> BuildFromDatabaseAsync(
        IDbContextFactory<JitenDbContext> contextFactory,
        Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        var memBefore = GC.GetTotalMemory(forceFullCollection: true);

        var tagPool = new Dictionary<string, string>(StringComparer.Ordinal);
        var chainPool = new Dictionary<string, string[]>(StringComparer.Ordinal);

        await using var ctx = await contextFactory.CreateDbContextAsync();
        var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        long totalRows;
        await using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = @"SELECT COUNT(*) FROM jmdict.""ConjugatedForms""";
            totalRows = (long)(await countCmd.ExecuteScalarAsync() ?? 0L);
        }

        // ORDER BY Surface turns the read into a streaming group-by: all
        // hits for a given surface arrive contiguously, so we can emit
        // (offset, count) directly into the index and never allocate a
        // per-surface List<>. Postgres does an external sort here (no
        // IX_Surface exists anymore — the binary cache is the primary read
        // path, and this fallback is accepted-slow).
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT ""Surface"", ""WordId"", ""ConjugationChain"", ""FormIndex"" FROM jmdict.""ConjugatedForms"" ORDER BY ""Surface""";

        var flatHits = new ConjugatedFormHit[totalRows];
        var index = new Dictionary<string, ConjugationTable.HitRange>(StringComparer.Ordinal);

        string? currentSurface = null;
        int currentOffset = 0;
        int currentCount = 0;
        long rows = 0;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var surface = reader.GetString(0);
            var wordId = reader.GetInt32(1);
            var rawChain = reader.IsDBNull(2) ? Array.Empty<string>() : (string[])reader.GetValue(2);
            var formIndex = reader.GetInt16(3);

            string[]? chain;
            if (rawChain.Length == 0)
            {
                chain = null;
            }
            else
            {
                for (int i = 0; i < rawChain.Length; i++)
                {
                    var tag = rawChain[i];
                    if (tagPool.TryGetValue(tag, out var interned))
                        rawChain[i] = interned;
                    else
                        tagPool[tag] = tag;
                }

                var key = string.Join('\0', rawChain);
                if (chainPool.TryGetValue(key, out var shared))
                    chain = shared;
                else
                {
                    chain = rawChain;
                    chainPool[key] = rawChain;
                }
            }

            if (surface != currentSurface)
            {
                if (currentSurface != null)
                    index[currentSurface] = new ConjugationTable.HitRange(currentOffset, currentCount);
                currentSurface = surface;
                currentOffset = (int)rows;
                currentCount = 0;
            }

            flatHits[rows] = new ConjugatedFormHit(wordId, chain, (byte)formIndex);
            currentCount++;
            rows++;
        }

        if (currentSurface != null)
            index[currentSurface] = new ConjugationTable.HitRange(currentOffset, currentCount);

        var table = new ConjugationTable(index, flatHits);
        sw.Stop();

        var memAfter = GC.GetTotalMemory(forceFullCollection: true);
        var memMb = (memAfter - memBefore) / (1024.0 * 1024.0);

        (log ?? Console.Error.WriteLine)(
            $"ConjugationTable built from Postgres: {rows} rows → {table.SurfaceCount} unique surfaces, " +
            $"{tagPool.Count} distinct tags, {chainPool.Count} distinct chains; " +
            $"took {sw.ElapsedMilliseconds}ms, +{memMb:F0} MB RAM");

        return table;
    }

    /// <summary>
    /// For tests / tooling that needs to replace the cached singleton.
    /// Not exposed to parser runtime flows.
    /// </summary>
    public static void SetTableForTesting(ConjugationTable? table)
    {
        _table = table;
        _rowCount = table?.HitCount ?? 0;
    }
}

/// <summary>
/// A single entry in the conjugation table: which lemma (WordId) produced this
/// surface and via which chain (null if identity form).
/// </summary>
public readonly record struct ConjugatedFormHit(
    int WordId,
    IReadOnlyList<string>? Chain,
    byte FormIndex);
