using System.Collections.Concurrent;
using Jiten.Core;

namespace Jiten.Parser.Resegmentation;

// One candidate meaning for a substring surface. Rich fields (ReadingIndex,
// ConjugationChain) exist for the beam — DirectLookup populates them
// conservatively; DeconjugatorCandidateProvider and the future
// ConjugatedFormsCandidateProvider fill them in fully.
//
// LookupSurface carries the *proxy* surface Ichiran's def-abbr-suffix uses:
// colloquial abbreviations (行かなきゃ → 行かなければ) keep the original input
// substring as MatchedSurface (display) while the JMDict lookup ran against
// the rewritten form. When null, MatchedSurface was also the lookup key.
internal readonly record struct SurfaceCandidate(
    int WordId,
    byte ReadingIndex,
    IReadOnlyList<string>? ConjugationChain,
    string MatchedSurface,
    string? LookupSurface = null);

// Abstraction over "given a surface, what dictionary entries could it be?".
// Three planned implementations:
//   1. DirectLookupCandidateProvider — wraps _lookups, dict-form only (today).
//   2. DeconjugatorCandidateProvider — runs deconjugator + lookups (beam enabler).
//   3. ConjugatedFormsCandidateProvider — pre-materialised table (lesson #2, later).
// The beam is coded against this interface so #3 drops in without re-plumbing.
internal interface ICandidateProvider
{
    IReadOnlyList<SurfaceCandidate> GetCandidates(string surface);
}

// Sentence-scoped wrapper around an ICandidateProvider. Owns both:
//   (a) materialized sentence spans `(start,len) -> string`
//   (b) candidate lists `surface -> IReadOnlyList<SurfaceCandidate>`
// so hot paths can reuse a single surface object for repeated span reads and
// every unique surface string is resolved at most once per sentence.
internal sealed class SentenceSurfaceCache : ICandidateProvider
{
    private readonly string _text;
    private readonly ICandidateProvider _inner;
    private readonly Dictionary<(int Start, int Len), string> _surfaceCache = new();
    private readonly Dictionary<string, IReadOnlyList<SurfaceCandidate>> _cache
        = new(StringComparer.Ordinal);

    public SentenceSurfaceCache(string text, ICandidateProvider inner)
    {
        _text = text;
        _inner = inner;
    }

    public string GetSurface(int start, int len)
    {
        if (_surfaceCache.TryGetValue((start, len), out var hit)) return hit;
        var surface = _text.Substring(start, len);
        _surfaceCache[(start, len)] = surface;
        return surface;
    }

    public IReadOnlyList<SurfaceCandidate> GetCandidates(int start, int len)
        => GetCandidates(GetSurface(start, len));

    public IReadOnlyList<SurfaceCandidate> GetCandidates(string surface)
    {
        if (_cache.TryGetValue(surface, out var hit)) return hit;
        var v = _inner.GetCandidates(surface);
        _cache[surface] = v;
        return v;
    }
}

internal static class CandidateProviderHelpers
{
    // True when every char is in the basic katakana block (U+30A0–U+30FF).
    // Ichiran's find-word-as-hiragana (dict.lisp:1093) fires only when the
    // substring coincides with a katakana run. We use this to gate the
    // script-conversion part of the hiragana fallback; the long-vowel-mark
    // normalisation (ー → preceding vowel) still fires for any surface
    // because colloquial hiragana spellings depend on it.
    public static bool IsAllKatakana(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (char c in s)
            if (c < 0x30A0 || c > 0x30FF) return false;
        return true;
    }
}

internal sealed class DirectLookupCandidateProvider : ICandidateProvider
{
    private readonly Dictionary<string, List<int>> _lookups;

    public DirectLookupCandidateProvider(Dictionary<string, List<int>> lookups)
    {
        _lookups = lookups;
    }

    public IReadOnlyList<SurfaceCandidate> GetCandidates(string surface)
    {
        if (_lookups.TryGetValue(surface, out var direct) && direct.Count > 0)
            return ToCandidates(direct, surface);

        try
        {
            var hira = KanaNormalizer.Normalize(KanaConverter.ToHiragana(surface, convertLongVowelMark: false));
            if (hira != surface && _lookups.TryGetValue(hira, out var hiraIds) && hiraIds.Count > 0)
                return ToCandidates(hiraIds, surface);
        }
        catch { }

        return Array.Empty<SurfaceCandidate>();
    }

    private static SurfaceCandidate[] ToCandidates(List<int> ids, string surface)
    {
        var result = new SurfaceCandidate[ids.Count];
        for (int i = 0; i < ids.Count; i++)
            result[i] = new SurfaceCandidate(ids[i], 0, null, surface);
        return result;
    }
}

// Enumerates candidates for a surface by (1) direct lookup, (2) hiragana fallback,
// and (3) deconjugating the surface and looking each base form up. Returns
// SurfaceCandidates with ReadingIndex = 0 as a placeholder — per Lesson #3 design,
// the beam picks segmentation based on WordId/POS only, and reading index is
// resolved downstream by ProcessWordsInBatches after the beam emits new tokens.
//
// Memoised per provider instance (ConcurrentDictionary) — the beam enumerates the
// same substring across many start offsets and across sentences within one parse
// run, so the ~67µs deconjugator cost amortises heavily.
internal sealed class DeconjugatorCandidateProvider : ICandidateProvider
{
    private readonly Dictionary<string, List<int>> _lookups;
    private readonly Deconjugator _deconjugator;
    private readonly ConcurrentDictionary<string, IReadOnlyList<SurfaceCandidate>> _cache = new(StringComparer.Ordinal);

    public DeconjugatorCandidateProvider(Dictionary<string, List<int>> lookups, Deconjugator deconjugator)
    {
        _lookups = lookups;
        _deconjugator = deconjugator;
    }

    public IReadOnlyList<SurfaceCandidate> GetCandidates(string surface)
    {
        if (_cache.TryGetValue(surface, out var hit)) return hit;
        return _cache.GetOrAdd(surface, Compute);
    }

    private IReadOnlyList<SurfaceCandidate> Compute(string surface)
    {
        // seen keyed on (wordId, baseForm) — a word matching via two different base
        // forms (e.g. direct + deconjugated) should appear once per base form so the
        // conjugation chain is preserved.
        var seen = new HashSet<(int WordId, string BaseForm)>();
        var result = new List<SurfaceCandidate>();

        AddHits(surface, surface, chain: null, result, seen);

        try
        {
            var hira = KanaNormalizer.Normalize(KanaConverter.ToHiragana(surface, convertLongVowelMark: false));
            if (hira != surface)
                AddHits(surface, hira, chain: null, result, seen);
        }
        catch { }

        var forms = _deconjugator.Deconjugate(surface);
        foreach (var form in forms)
        {
            // Skip trivial identity — already covered above.
            if (form.Process.Count == 0) continue;
            AddHits(surface, form.Text, form.Process, result, seen);
        }

        // Ichiran def-abbr-suffix: colloquial abbreviations (なきゃ→なければ,
        // ねえ→ない, ええ→いい, じゃない→ではない). Rewrite the surface, run
        // the full direct+deconjugate pipeline against the rewritten form, and
        // attach the resulting candidates with MatchedSurface = original so the
        // lattice carries display vs lookup correctly.
        foreach (var (rewritten, tag) in AbbreviationRewriter.Rewrites(surface))
        {
            if (rewritten == surface) continue;
            // Direct lookup on rewritten.
            AddAbbrHits(surface, rewritten, rewritten, new[] { tag }, result, seen);
            // Hiragana form of rewritten (kanji-kana mixed abbrs fall out naturally
            // via direct lookup; this guards against katakana-mixed cases).
            try
            {
                var rewrittenHira = KanaNormalizer.Normalize(KanaConverter.ToHiragana(rewritten, convertLongVowelMark: false));
                if (rewrittenHira != rewritten)
                    AddAbbrHits(surface, rewritten, rewrittenHira, new[] { tag }, result, seen);
            }
            catch { }
            // Deconjugate the rewritten form — this is what resolves 行かなければ
            // back to 行く (via negative + provisional conditional chain).
            var rewrittenForms = _deconjugator.Deconjugate(rewritten);
            foreach (var form in rewrittenForms)
            {
                if (form.Process.Count == 0) continue;
                var chain = new string[form.Process.Count + 1];
                chain[0] = tag;
                for (int i = 0; i < form.Process.Count; i++) chain[i + 1] = form.Process[i];
                AddAbbrHits(surface, rewritten, form.Text, chain, result, seen);
            }
        }

        return result.Count == 0 ? Array.Empty<SurfaceCandidate>() : result;
    }

    private void AddAbbrHits(
        string displaySurface,
        string lookupSurface,
        string baseForm,
        IReadOnlyList<string> chain,
        List<SurfaceCandidate> result,
        HashSet<(int WordId, string BaseForm)> seen)
    {
        if (!_lookups.TryGetValue(baseForm, out var ids) || ids.Count == 0) return;
        foreach (var id in ids)
        {
            if (!seen.Add((id, baseForm))) continue;
            result.Add(new SurfaceCandidate(id, 0, chain, displaySurface, lookupSurface));
        }
    }

    private void AddHits(
        string surface,
        string baseForm,
        IReadOnlyList<string>? chain,
        List<SurfaceCandidate> result,
        HashSet<(int WordId, string BaseForm)> seen)
    {
        if (!_lookups.TryGetValue(baseForm, out var ids) || ids.Count == 0) return;
        foreach (var id in ids)
        {
            if (!seen.Add((id, baseForm))) continue;
            result.Add(new SurfaceCandidate(id, 0, chain, surface));
        }
    }
}
