namespace Jiten.Parser.Conjugation;

// Applies a single JMdictDB conjugation rule to a lemma surface.
// Direct port of Ichiran's construct-conjugation (dict-load.lisp:287).
public static class ForwardConjugator
{
    // Ichiran's *do-not-conjugate* — POS names that appear in the rule table but
    // aren't themselves conjugable paradigms (n → bare noun, vs → untyped suru,
    // adj-na → treated as cop-da).
    private static readonly HashSet<string> DoNotConjugate = new(StringComparer.Ordinal)
    {
        "n", "vs", "adj-na",
    };

    // Ichiran's *pos-with-conj-rules* — the canonical "conjugable" whitelist.
    // We still emit for anything conjo.csv has rules for, but checking this set
    // lets callers skip lookups for non-paradigm POS.
    public static readonly HashSet<string> PrimaryConjugablePos = new(StringComparer.Ordinal)
    {
        "adj-i", "adj-ix", "cop", "cop-da", "v1", "v1-s", "v5aru",
        "v5b", "v5g", "v5k", "v5k-s", "v5m", "v5n", "v5r", "v5r-i", "v5s",
        "v5t", "v5u", "v5u-s", "vk", "vs-s", "vs-i",
    };

    public static bool IsPrimaryConjugable(string posName) =>
        PrimaryConjugablePos.Contains(posName) && !DoNotConjugate.Contains(posName);

    // Apply one rule to a lemma. Returns the conjugated surface, or null if the
    // lemma is too short to support the rule's stem-strip.
    public static string? Apply(string lemma, JmdictConjRule rule)
    {
        bool iskana = EndsInKana(lemma);
        string euph = iskana ? rule.Euphr : rule.Euphk;
        int extra = euph.Length > 0 ? 1 : 0;
        int totalStem = rule.Stem + extra;

        if (totalStem > lemma.Length) return null;

        int keep = lemma.Length - totalStem;
        return string.Concat(lemma.AsSpan(0, keep), euph.AsSpan(), rule.Okuri.AsSpan());
    }

    private static bool EndsInKana(string s)
    {
        // Ichiran: (test-word (subseq word (max 0 (- (length word) 2))) :kana)
        // "ends in kana" = the last up-to-2 chars are all kana. Used to pick
        // euphr vs euphk (the kana/kanji-ending branch for 〜いい-class adjs).
        if (s.Length == 0) return true;
        int start = Math.Max(0, s.Length - 2);
        for (int i = start; i < s.Length; i++)
        {
            if (!IsKana(s[i])) return false;
        }
        return true;
    }

    private static bool IsKana(char c) =>
        (c >= '\u3040' && c <= '\u309F') || // hiragana
        (c >= '\u30A0' && c <= '\u30FF') || // katakana
        c == '\u30FC';                      // long-vowel mark (prolonged sound)
}
