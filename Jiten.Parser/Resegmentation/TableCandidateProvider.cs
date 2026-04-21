using System.Collections.Concurrent;
using Jiten.Parser.Resolution;

namespace Jiten.Parser.Resegmentation;

/// <summary>
/// ICandidateProvider backed by the pre-materialised jmdict.ConjugatedForms
/// table. Direct O(1) dict lookup — no runtime deconjugation, so no spurious
/// chains (e.g. particles "inflecting"). See PLAN_ConjugationTable.md.
///
/// Composes with DirectLookupCandidateProvider semantics: identity-form
/// surfaces are already in the table, but hiragana-normalisation fallback is
/// still needed for katakana/mixed surfaces that the table was built from
/// kana readings for.
/// </summary>
internal sealed class TableCandidateProvider : ICandidateProvider
{
    private readonly ConjugationTable _table;
    private readonly Dictionary<string, List<int>>? _lookupsForFallback;
    private readonly ConcurrentDictionary<string, IReadOnlyList<SurfaceCandidate>> _globalCache
        = new(StringComparer.Ordinal);

    public TableCandidateProvider(
        ConjugationTable table,
        Dictionary<string, List<int>>? lookupsForFallback = null)
    {
        _table = table;
        _lookupsForFallback = lookupsForFallback;
    }

    public IReadOnlyList<SurfaceCandidate> GetCandidates(string surface)
    {
        if (_globalCache.TryGetValue(surface, out var cached)) return cached;
        var result = GetCandidatesCore(surface);
        _globalCache.TryAdd(surface, result);
        return result;
    }

    private IReadOnlyList<SurfaceCandidate> GetCandidatesCore(string surface)
    {
        // Table-only hits cover conjugated forms of conjugable words, but the
        // table excludes particles / adverbs / aux / pn / cop / int (see
        // ConjugationTableGenerator.IsConjugable). A 1-char kana surface like
        // "に" that is simultaneously a conjugated form of a verb AND a
        // particle needs both — otherwise the verb-form hits shadow the
        // particle and no lattice edge for the particle exists. Merge both
        // sources rather than falling through.
        var hits = _table.GetHitsSpan(surface);
        bool suppressAdjIPolite = IsAdjIPoliteSurface(surface);
        List<SurfaceCandidate>? merged = null;
        if (hits.Length > 0)
        {
            merged = new List<SurfaceCandidate>(hits.Length + 2);
            foreach (var h in hits)
            {
                if (suppressAdjIPolite && HasPoliteChain(h.Chain)) continue;
                merged.Add(new SurfaceCandidate(h.WordId, h.FormIndex, h.Chain, surface));
            }
        }

        if (_lookupsForFallback != null && _lookupsForFallback.TryGetValue(surface, out var ids) && ids.Count > 0)
        {
            merged ??= new List<SurfaceCandidate>(ids.Count);
            foreach (var id in ids)
            {
                bool exists = false;
                for (int i = 0; i < merged.Count; i++)
                    if (merged[i].WordId == id) { exists = true; break; }
                if (!exists)
                    merged.Add(new SurfaceCandidate(id, 0, null, surface));
            }
        }

        if (merged != null) return merged;

        // Hiragana normalisation fallback — the table was built from raw
        // form.Text, so katakana or mixed surfaces may miss. NOTE: Ichiran
        // gates find-word-as-hiragana on pure-katakana substrings only
        // (dict.lisp:1093). Our gate is deliberately broader because tests
        // depend on mixed-script lookups (うわッ → うわっ, チクッた → ちくった,
        // ヤツら → やつら) that don't reliably exist as direct mixed-script
        // keys in JMDict. A strict gate regressed -8 tests; kept lax.
        try
        {
            var hira = KanaNormalizer.Normalize(KanaConverter.ToHiragana(surface, convertLongVowelMark: false));
            if (hira != surface)
            {
                var hiraHits = _table.GetHitsSpan(hira);
                if (hiraHits.Length > 0)
                    return ToCandidates(hiraHits, surface, suppressAdjIPolite);
                if (_lookupsForFallback != null && _lookupsForFallback.TryGetValue(hira, out var hiraIds) && hiraIds.Count > 0)
                    return FromIds(hiraIds, surface);
            }
        }
        catch { }

        return Array.Empty<SurfaceCandidate>();
    }

    // Ichiran (and the test suite) treats adj-i + です/でしょう as separate lattice
    // edges — the polite copula is its own token, not part of the adjective's
    // paradigm. The conjo.csv paradigm emits `いです` / `かったです` / `いでしょう`
    // rows; we suppress those bundled hits at read time so the beam is forced to
    // split (e.g. かわいい + です rather than a single かわいいです edge).
    private static bool IsAdjIPoliteSurface(string surface)
    {
        if (surface.Length < 3) return false;
        return surface.EndsWith("です", StringComparison.Ordinal)
            || surface.EndsWith("でしょう", StringComparison.Ordinal);
    }

    private static bool HasPoliteChain(IReadOnlyList<string>? chain)
    {
        if (chain == null) return false;
        foreach (var t in chain)
            if (t == "polite") return true;
        return false;
    }

    private static SurfaceCandidate[] ToCandidates(ReadOnlySpan<ConjugatedFormHit> hits, string surface, bool suppressAdjIPolite = false)
    {
        if (!suppressAdjIPolite)
        {
            var result = new SurfaceCandidate[hits.Length];
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                result[i] = new SurfaceCandidate(h.WordId, h.FormIndex, h.Chain, surface);
            }
            return result;
        }

        var filtered = new List<SurfaceCandidate>(hits.Length);
        for (int i = 0; i < hits.Length; i++)
        {
            var h = hits[i];
            if (HasPoliteChain(h.Chain)) continue;
            filtered.Add(new SurfaceCandidate(h.WordId, h.FormIndex, h.Chain, surface));
        }
        return filtered.ToArray();
    }

    private static SurfaceCandidate[] FromIds(List<int> ids, string surface)
    {
        var result = new SurfaceCandidate[ids.Count];
        for (int i = 0; i < ids.Count; i++)
            result[i] = new SurfaceCandidate(ids[i], 0, null, surface);
        return result;
    }
}
