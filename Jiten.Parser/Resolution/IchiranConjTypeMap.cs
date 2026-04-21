namespace Jiten.Parser.Resolution;

// Maps Jiten deconjugator "detail" strings to Ichiran's integer conj-type ids
// (conj.csv). Used by rules that need to reason about conjugation class at the
// lattice level — segfilters (e.g. sukiyoki), *weak-conj-forms* prop reduction,
// *skip-conj-forms*.
//
// Standard JMdictDB types (1–13, from Ichiran's conj.csv):
//   1  non-past
//   2  past
//   3  -te (continuative participle)
//   4  provisional -eba
//   5  potential
//   6  passive
//   7  causative
//   8  volitional (-ou/-you)
//   9  tentative / presumptive (-ou)  — when combined with neg=t: negative volitional (〜まい)
//   10 imperative
//   11 conditional -tara
//   12 -tari
//   13 continuative (連用形 / masu-stem)
//
// Ichiran extensions (dict-errata.lisp:1236+):
//   50 adverbial (く-form of adj-i)
//   51 adjective stem (plain stem)
//   52 negative stem (for さ-suffix from 〜ない)
//   53 causative-su (〜す)
//   54 adjective literary (き-form, archaic)
public static class IchiranConjType
{
    public const int NonPast             = 1;
    public const int Past                = 2;
    public const int Te                  = 3;
    public const int Provisional         = 4;
    public const int Potential           = 5;
    public const int Passive             = 6;
    public const int Causative           = 7;
    public const int Volitional          = 8;
    public const int Tentative           = 9;   // +neg: negative volitional (mai)
    public const int Imperative          = 10;
    public const int Conditional         = 11;  // -tara
    public const int Tari                = 12;
    public const int Continuative        = 13;  // masu-stem / ren'youkei
    public const int Adverbial           = 50;
    public const int AdjectiveStem       = 51;
    public const int NegativeStem        = 52;
    public const int CausativeSu         = 53;
    public const int AdjectiveLiterary   = 54;

    // Jiten deconjugator detail → Ichiran conj-type. Multiple details may map to
    // the same conj-type (e.g. past + past polite + formal negative past all → 2).
    // Details not present here don't correspond to a core Ichiran conj-type (e.g.
    // "seemingness", "excess" — those are suffix-derived forms).
    private static readonly Dictionary<string, int> _map = new(StringComparer.Ordinal)
    {
        // 2 past
        ["past"] = Past,
        ["past polite"] = Past,
        ["formal negative past"] = Past,
        ["past negative polite"] = Past,

        // 3 te-form
        ["(te form)"] = Te,
        ["te polite"] = Te,

        // 4 provisional -eba
        ["provisional conditional"] = Provisional,
        ["contracted conditional (te-ireba)"] = Provisional,

        // 5 potential
        ["potential"] = Potential,

        // 6 passive
        ["passive"] = Passive,
        ["passive/potential"] = Passive,

        // 7 causative
        ["causative"] = Causative,
        ["short causative"] = Causative,

        // 8 volitional
        ["volitional"] = Volitional,
        ["shortened volitional"] = Volitional,
        ["polite volitional"] = Volitional,

        // 9 tentative / negative volitional
        ["negative volition/conjecture"] = Tentative,
        ["mai"] = Tentative,
        ["presumptive"] = Tentative,

        // 10 imperative
        ["imperative"] = Imperative,
        ["polite command"] = Imperative,
        ["negative imperative"] = Imperative,  // 〜るな — combined with HasNegative flag → (10 t :any) skip

        // 11 conditional -tara
        ["conditional"] = Conditional,
        ["formal conditional"] = Conditional,
        ["negative conditional"] = Conditional,
        ["colloquial negative conditional"] = Conditional,
        ["slurred negative conditional"] = Conditional,
        ["slang negative conditional"] = Conditional,
        ["classical hypothetical conditional"] = Conditional,

        // 12 tari
        ["tari"] = Tari,

        // 13 continuative / masu-stem
        ["(infinitive)"] = Continuative,
        ["(unstressed infinitive)"] = Continuative,
        ["polite"] = Continuative,
        ["negative polite"] = Continuative,
        ["polite (childish)"] = Continuative,

        // 50 adverbial (く-form of adj-i)
        ["adverbial"] = Adverbial,
        ["(adverbial stem)"] = Adverbial,
        ["adverbial negative"] = Adverbial,

        // 51 adjective stem
        ["(stem)"] = AdjectiveStem,

        // 54 adjective literary (き-form)
        ["classical attributive"] = AdjectiveLiterary,
        ["attributive"] = AdjectiveLiterary,
    };

    // Returns the Ichiran conj-type for a detail string, or null if no mapping.
    public static int? TryMap(string? detail) =>
        detail != null && _map.TryGetValue(detail, out var id) ? id : null;

    // Walks a conjugation chain and returns the deepest (last) conj-type mapping,
    // which mirrors Ichiran's "last-applied rule dominates" semantics for lattice
    // filters. Returns null if no tag in the chain maps.
    public static int? LastConjType(IReadOnlyList<string>? chain)
    {
        if (chain == null || chain.Count == 0) return null;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            var id = TryMap(chain[i]);
            if (id.HasValue) return id;
        }
        return null;
    }

    // True if any tag in the chain maps to the given conj-type.
    public static bool ChainContains(IReadOnlyList<string>? chain, int conjType)
    {
        if (chain == null) return false;
        foreach (var tag in chain)
            if (TryMap(tag) == conjType) return true;
        return false;
    }

    // Ichiran *weak-conj-forms* membership test. True when the chain contains a
    // form that Ichiran considers "weak" (not enough on its own to treat as a
    // conjugated root). Currently covers the chain-level slice of the list;
    // adj-stem / neg-stem / causative-su / adj-literary are typically carried
    // by POS-derived tags (not deconjugator chain), so we special-case 9+neg.
    //
    // (9 t :any) = tentative + negative — the まい form.
    public static bool IsWeakConjChain(IReadOnlyList<string>? chain)
    {
        if (chain == null) return false;
        foreach (var tag in chain)
        {
            if (tag == "negative volition/conjecture" || tag == "mai") return true;
            var id = TryMap(tag);
            if (id == AdjectiveStem || id == NegativeStem || id == CausativeSu || id == AdjectiveLiterary)
                return true;
        }
        return false;
    }
}
