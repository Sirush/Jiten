using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace Jiten.Parser.Resolution;

/// <summary>
/// Packed on-disk cache of the ConjugationTable.
///
/// Postgres loading the 27M-row table takes ~80 s cold (Npgsql per-row
/// allocations dominate). That's unacceptable for every CLI/API startup.
/// This file-backed cache is produced by <c>--generate-conjugations</c> and
/// read back by the parser on startup in &lt;5 s.
///
/// Layout (all little-endian):
///   Header:
///     u64  magic = "JITENCFT"
///     i32  version
///     i32  flags (reserved, 0)
///     i64  generated_at (unix seconds)
///     i32  tag_count
///     i32  chain_count
///     i32  surface_count
///     i64  hit_count
///
///   Tag pool (tag_count entries):
///     u16 len; len bytes UTF-8
///
///   Chain pool (chain_count entries):
///     u8 len; len × u16 tag_id
///
///   Hits (hit_count entries, matches in-memory layout of ConjugatedFormHit):
///     i32 word_id; i32 chain_id (-1 = null); u8 form_index
///
///   Surface index (surface_count entries):
///     u16 len; len bytes UTF-8; i32 hit_offset; i32 hit_count
///
/// Size estimate: ~320 MB on disk (vs. 4 GB in Postgres heap) — the
/// serialised form omits indexes, row headers, and text[] metadata.
/// </summary>
public static class ConjugationTableBinaryFile
{
    // "JITENCFT" little-endian.
    private const ulong Magic = 0x5446434E4554494AUL;
    private const int CurrentVersion = 1;

    public static string DefaultPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "conjugations.bin");

    /// <summary>
    /// Walks up from <c>AppDomain.CurrentDomain.BaseDirectory</c> looking for
    /// a sibling <c>Shared/resources/</c> directory. Returns null if not found
    /// (e.g. deployed install). Used so the CLI generator can drop the file
    /// into the canonical Shared location as well as its local bin copy, so
    /// sibling projects pick it up on next build.
    /// </summary>
    public static string? FindSharedResourcesPath()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Shared", "resources");
            if (Directory.Exists(candidate))
                return Path.Combine(candidate, "conjugations.bin");
            dir = dir.Parent;
        }
        return null;
    }

    public static void Write(ConjugationTable table, string path, Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        log ??= Console.Error.WriteLine;

        // Build tag + chain pools. Chain arrays are already reference-interned
        // by the loader, so reference equality dedupes them in a single pass.
        var tagToId = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var chainRefToId = new Dictionary<IReadOnlyList<string>, int>(ReferenceEqualityComparer.Instance);
        var chainsById = new List<ushort[]>();

        ushort GetTagId(string tag)
        {
            if (tagToId.TryGetValue(tag, out var id)) return id;
            if (tagToId.Count >= ushort.MaxValue)
                throw new InvalidOperationException("Tag pool exceeded 65k; file format needs u32 tag IDs.");
            id = (ushort)tagToId.Count;
            tagToId[tag] = id;
            return id;
        }

        int GetChainId(IReadOnlyList<string>? chain)
        {
            if (chain == null || chain.Count == 0) return -1;
            if (chainRefToId.TryGetValue(chain, out var id)) return id;
            if (chain.Count > byte.MaxValue)
                throw new InvalidOperationException("Chain length exceeded 255; file format needs u16 chain len.");
            var tagIds = new ushort[chain.Count];
            for (int i = 0; i < chain.Count; i++) tagIds[i] = GetTagId(chain[i]);
            id = chainsById.Count;
            chainsById.Add(tagIds);
            chainRefToId[chain] = id;
            return id;
        }

        var hits = table.HitsBuffer;
        var hitChainIds = new int[hits.Length];
        for (int i = 0; i < hits.Length; i++)
            hitChainIds[i] = GetChainId(hits[i].Chain);

        var tagsById = new string[tagToId.Count];
        foreach (var kv in tagToId) tagsById[kv.Value] = kv.Key;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan))
        using (var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false))
        {
            bw.Write(Magic);
            bw.Write(CurrentVersion);
            bw.Write(0);
            bw.Write(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            bw.Write(tagsById.Length);
            bw.Write(chainsById.Count);
            bw.Write(table.SurfaceCount);
            bw.Write((long)hits.Length);

            foreach (var tag in tagsById)
            {
                var bytes = Encoding.UTF8.GetBytes(tag);
                if (bytes.Length > ushort.MaxValue)
                    throw new InvalidOperationException($"Tag too long: {tag}");
                bw.Write((ushort)bytes.Length);
                bw.Write(bytes);
            }

            foreach (var chain in chainsById)
            {
                bw.Write((byte)chain.Length);
                for (int i = 0; i < chain.Length; i++) bw.Write(chain[i]);
            }

            for (int i = 0; i < hits.Length; i++)
            {
                bw.Write(hits[i].WordId);
                bw.Write(hitChainIds[i]);
                bw.Write(hits[i].FormIndex);
            }

            foreach (var kv in table.EnumerateSurfaces())
            {
                var bytes = Encoding.UTF8.GetBytes(kv.Key);
                if (bytes.Length > ushort.MaxValue)
                    throw new InvalidOperationException($"Surface too long: {kv.Key}");
                bw.Write((ushort)bytes.Length);
                bw.Write(bytes);
                bw.Write(kv.Value.Offset);
                bw.Write(kv.Value.Count);
            }
        }

        File.Move(tmp, path, overwrite: true);
        sw.Stop();

        var sizeMb = new FileInfo(path).Length / (1024.0 * 1024.0);
        log($"ConjugationTable binary written to {path} " +
            $"({tagsById.Length} tags, {chainsById.Count} chains, {hits.Length} hits, {table.SurfaceCount} surfaces) " +
            $"in {sw.ElapsedMilliseconds}ms, {sizeMb:F0} MB");
    }

    /// <summary>
    /// Reads the packed file into a ConjugationTable. Returns null if the file
    /// is missing, has a bad magic, or has a version mismatch — caller is
    /// expected to fall back to building from Postgres.
    ///
    /// Impl note: the file is slurped into a single byte[] and parsed via
    /// ReadOnlySpan + BinaryPrimitives. BinaryReader's virtual-dispatch-per-call
    /// plus <c>ReadBytes</c>-allocates-a-byte[]-per-surface dominated load time
    /// at 25M entries (~24s). Span-based parsing takes ~5-8s on the same file.
    /// The transient 1 GB byte[] allocation is released before the method
    /// returns, so peak RSS during load is ~file-size + ~table-size.
    /// </summary>
    public static ConjugationTable? TryRead(string path, Action<string>? log = null)
    {
        log ??= Console.Error.WriteLine;
        if (!File.Exists(path)) return null;

        var sw = Stopwatch.StartNew();

        try
        {
            var bytes = File.ReadAllBytes(path);
            var readMs = sw.ElapsedMilliseconds;

            var table = Parse(bytes, path, log);
            if (table == null) return null;

            sw.Stop();
            log($"ConjugationTable loaded from {path}: {table.SurfaceCount} surfaces, {table.HitCount} hits " +
                $"in {sw.ElapsedMilliseconds}ms (read {readMs}ms, parse {sw.ElapsedMilliseconds - readMs}ms)");
            return table;
        }
        catch (Exception ex)
        {
            log($"ConjugationTable binary at {path} failed to load ({ex.GetType().Name}: {ex.Message}); falling back to Postgres");
            return null;
        }
    }

    private static ConjugationTable? Parse(byte[] bytes, string path, Action<string> log)
    {
        var span = new ReadOnlySpan<byte>(bytes);
        int pos = 0;

        var magic = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(pos, 8)); pos += 8;
        if (magic != Magic)
        {
            log($"ConjugationTable binary at {path} has bad magic; ignoring");
            return null;
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos, 4)); pos += 4;
        if (version != CurrentVersion)
        {
            log($"ConjugationTable binary at {path} has version {version}, expected {CurrentVersion}; ignoring (regenerate via --generate-conjugations)");
            return null;
        }

        pos += 4;  // flags
        pos += 8;  // generated_at
        var tagCount = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos, 4)); pos += 4;
        var chainCount = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos, 4)); pos += 4;
        var surfaceCount = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos, 4)); pos += 4;
        var hitCount = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(pos, 8)); pos += 8;

        if (hitCount < 0 || hitCount > int.MaxValue)
            throw new InvalidDataException($"Unreasonable hit_count {hitCount}");

        var tags = new string[tagCount];
        for (int i = 0; i < tagCount; i++)
        {
            var len = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(pos, 2)); pos += 2;
            tags[i] = Encoding.UTF8.GetString(span.Slice(pos, len));
            pos += len;
        }

        var chains = new string[chainCount][];
        for (int i = 0; i < chainCount; i++)
        {
            int len = span[pos++];
            var chain = new string[len];
            for (int j = 0; j < len; j++)
            {
                var tagId = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(pos, 2)); pos += 2;
                chain[j] = tags[tagId];
            }
            chains[i] = chain;
        }

        var hits = new ConjugatedFormHit[hitCount];
        for (long i = 0; i < hitCount; i++)
        {
            var wordId = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos, 4)); pos += 4;
            var chainId = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos, 4)); pos += 4;
            var formIdx = span[pos++];
            hits[i] = new ConjugatedFormHit(wordId, chainId < 0 ? null : chains[chainId], formIdx);
        }

        // Pre-sizing eliminates rehashing during the 25M inserts. Surfaces are
        // known-unique (the writer enumerates a Dictionary keyset), so we can
        // use the indexer without a Contains check.
        // maxLenByFirstChar is computed inline — free since we already touch every surface.
        var index = new Dictionary<string, ConjugationTable.HitRange>(surfaceCount, StringComparer.Ordinal);
        var maxLen = new int[65536];
        for (int i = 0; i < surfaceCount; i++)
        {
            var len = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(pos, 2)); pos += 2;
            var surface = Encoding.UTF8.GetString(span.Slice(pos, len));
            pos += len;
            var offset = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos, 4)); pos += 4;
            var count = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos, 4)); pos += 4;
            index[surface] = new ConjugationTable.HitRange(offset, count);
            if (surface.Length > 0)
            {
                int capped = Math.Min(surface.Length, 16);
                char fc = surface[0];
                if (capped > maxLen[fc]) maxLen[fc] = capped;
            }
        }

        return new ConjugationTable(index, hits, maxLen);
    }
}
