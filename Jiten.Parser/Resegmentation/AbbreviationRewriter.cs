namespace Jiten.Parser.Resegmentation;

// Ichiran def-abbr-suffix port (dict-grammar.lisp:547-663). Rewrites colloquial
// abbreviations to their JMDict-lookup form while preserving the original input
// surface for display. The beam and downstream writeback see the ORIGINAL surface
// (e.g. 行かなきゃ); lookup uses the REWRITTEN surface (行かなければ) to resolve to
// the underlying verb.
//
// Each rule strips the last N chars of the input surface and appends a replacement
// suffix. The stem-length N matches Ichiran's `:stem N` argument.
//
// Examples:
//   行かなきゃ   → 行かなければ  (abbr-nakereba, stem 2)  なきゃ→なければ
//   知らねえ     → 知らない      (abbr-nee, stem 2)        ねえ→ない
//   可愛ええ     → 可愛いい      (abbr-ii, stem 2)         ええ→いい
//   好きじゃない → 好きではない  (abbr-dewanai, stem 4)    じゃない→ではない
internal static class AbbreviationRewriter
{
    public readonly record struct AbbrRule(string From, string To, int Stem, string Tag);

    // Each rule: surface ends with `From`; rewrite replaces the last `Stem` chars
    // with `To`. Tag is added to the conjugation chain for diagnostics.
    private static readonly AbbrRule[] _rules =
    {
        new("なきゃ",   "なければ", 3, "abbr-nakereba"),
        new("なけりゃ", "なければ", 4, "abbr-nakereba"),
        new("じゃない", "ではない", 4, "abbr-dewanai"),
        new("じゃねえ", "ではない", 4, "abbr-dewanai"),
        new("ねえ",     "ない",     2, "abbr-nee"),
        new("ねー",     "ない",     2, "abbr-nee"),
        new("ええ",     "いい",     2, "abbr-ii"),
        new("えー",     "いい",     2, "abbr-ii"),
    };

    // Returns (rewrittenSurface, tag) pairs for each applicable rule, or empty.
    // Only rewrites when the surface is at least `Stem + 1` chars — i.e. there's
    // a non-empty root preceding the abbr.
    public static IEnumerable<(string Rewritten, string Tag)> Rewrites(string surface)
    {
        if (string.IsNullOrEmpty(surface)) yield break;
        foreach (var rule in _rules)
        {
            if (surface.Length <= rule.Stem) continue;
            if (!surface.EndsWith(rule.From, StringComparison.Ordinal)) continue;
            string root = surface.Substring(0, surface.Length - rule.Stem);
            yield return (root + rule.To, rule.Tag);
        }
    }
}
