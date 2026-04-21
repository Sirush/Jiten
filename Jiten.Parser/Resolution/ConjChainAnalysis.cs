namespace Jiten.Parser.Resolution;

// Structured view of a deconjugator chain for Ichiran-parity reasoning about
// weak/skip conjugation forms. Mirrors the data a conj-prop object carries in
// Ichiran (dict.lisp:262-270): conj-type code + neg + fml flags, plus the root
// POS (supplied externally at match time — it's the word's POS, not a chain
// property).
//
// Chain vocabulary notes:
//   • Tags map to Ichiran conj-type integers via IchiranConjType.
//   • Some tags represent intermediate stem transitions, not real conjugation
//     steps (e.g. "(unstressed infinitive)" prefixing an actual te-form). These
//     are excluded from MeaningfulStepCount so a plain te-form counts as one
//     step, not two.
//   • A handful of tags (mizenkei, ‘a’ stem, izenkei, ka/ke stem) don't map to
//     a conj-type code but are always weak forms in classical/paradigm usage.
//     They're tracked separately via UnmappedMeaningfulTags; helpers know to
//     treat them as weak.
//   • Neg flag: any meaningful tag contains "negative" (covers "negative",
//     "negative polite", "negative imperative", "formal negative past",
//     "adverbial negative", and the slang/colloquial/slurred variants).
//   • Fml flag: any meaningful tag contains "polite" or "formal".
public sealed class ConjChainAnalysis
{
    public bool HasNegative { get; }
    public bool HasFormal { get; }

    // Conj-type code for each meaningful step with a mapping (dedup-preserving order).
    public IReadOnlyList<int> ConjTypes { get; }

    // Meaningful tags with no conj-type mapping. Classical-weak stems live here.
    public IReadOnlyList<string> UnmappedMeaningfulTags { get; }

    public int MeaningfulStepCount => ConjTypes.Count + UnmappedMeaningfulTags.Count;

    // Real conjugation step count: type-mapped steps plus unmapped tags that are
    // NOT classical weak stems. Used by secondary-conj-p approximation — a plain
    // te-form over a classical-stem base should count as one real step.
    public int RealConjStepCount
    {
        get
        {
            int n = ConjTypes.Count;
            foreach (var t in UnmappedMeaningfulTags)
                if (!ClassicalWeakTags.Contains(t)) n++;
            return n;
        }
    }

    public int? LastConjType => ConjTypes.Count > 0 ? ConjTypes[^1] : null;

    private ConjChainAnalysis(bool hasNegative, bool hasFormal,
                              IReadOnlyList<int> conjTypes,
                              IReadOnlyList<string> unmapped)
    {
        HasNegative = hasNegative;
        HasFormal = hasFormal;
        ConjTypes = conjTypes;
        UnmappedMeaningfulTags = unmapped;
    }

    // Intermediate tags the deconjugator inserts between an actual stem transition
    // and the next real conjugation. Excluded from meaningful-step accounting.
    // Shared list with IchiranPropScorer.StemTransitionTags pre-port.
    private static readonly HashSet<string> StemTransitionTags = new()
    {
        "(unstressed infinitive)",
        "(stem)",
        "(mizenkei)",
        "('a' stem)",
        "(izenkei)",
        "(ka stem)",
        "(ke stem)",
        "(adverbial stem)",
        "(suru verb noun stem)",
        "(noun form)",
    };

    // Tags that don't map to an Ichiran conj-type but count as classical/weak
    // when they appear as the only meaningful content of a chain. These are the
    // exact tags WeakConjTags covered pre-port (mizenkei, 'a' stem, izenkei,
    // ka/ke stem) plus the explicitly-weak "short causative" which IS a conj
    // (causative) but is always weak per Ichiran rules.
    internal static readonly HashSet<string> ClassicalWeakTags = new()
    {
        "(mizenkei)",
        "('a' stem)",
        "(izenkei)",
        "(ka stem)",
        "(ke stem)",
    };

    // Tags that contribute ONLY a neg/fml flag and are not themselves a conjugation
    // step. Ichiran's conj-prop carries neg/fml on each conj-prop object; our
    // deconjugator emits them as separate tag entries. Without this set, a chain
    // like ["imperative", "negative"] would leave "negative" as an unmapped
    // meaningful tag, breaking the "every step matches" invariant in
    // AllMatchSkip for the (10 t :any) pattern.
    //
    // Any tag here is ignored for meaningful-step accounting; its neg/fml
    // contribution is captured via the tag-substring scan at parse time.
    private static readonly HashSet<string> FlagOnlyTags = new()
    {
        "negative",
        "formal negative",           // polite negative auxiliary (sets HasNegative + HasFormal)
        "negative (kansaiben)",
        "slurred negative",
        "slang negative",
        "archaic negative",
        "negative appearance",       // 〜なそう — fires as a flag-style annotation in chain
    };

    private static readonly ConjChainAnalysis EmptyInstance =
        new(false, false, System.Array.Empty<int>(), System.Array.Empty<string>());

    public static ConjChainAnalysis Empty => EmptyInstance;

    public static ConjChainAnalysis From(IReadOnlyList<string>? chain)
    {
        if (chain == null || chain.Count == 0) return EmptyInstance;

        bool hasNeg = false;
        bool hasFml = false;
        List<int>? conjTypes = null;
        List<string>? unmapped = null;

        foreach (var tag in chain)
        {
            if (string.IsNullOrEmpty(tag)) continue;

            if (tag.Contains("negative", System.StringComparison.OrdinalIgnoreCase))
                hasNeg = true;
            if (tag.Contains("polite", System.StringComparison.OrdinalIgnoreCase)
                || tag.Contains("formal", System.StringComparison.OrdinalIgnoreCase))
                hasFml = true;

            // Flag-only tags (bare "negative", "formal negative", dialectal negatives)
            // contribute neg/fml above via substring — they are NOT meaningful steps.
            // Must run before stem-transition / classical-weak checks so "negative"
            // doesn't fall through into UnmappedMeaningfulTags.
            if (FlagOnlyTags.Contains(tag)) continue;

            // Meaningful vs stem-transition:
            //   • StemTransitionTags are never "meaningful" by themselves.
            //   • Classical weak stems (mizenkei etc.) overlap with StemTransitionTags
            //     — when they're the ONLY thing in a chain they still need to count
            //     as a meaningful step so weak-form detection can fire. The split
            //     is handled below.
            bool isStemTransition = StemTransitionTags.Contains(tag);
            bool isClassicalWeak = ClassicalWeakTags.Contains(tag);

            var mapped = IchiranConjType.TryMap(tag);
            if (mapped.HasValue)
            {
                // Mapped tag: it's always meaningful.
                (conjTypes ??= new List<int>()).Add(mapped.Value);
                continue;
            }

            if (isClassicalWeak)
            {
                // Record it so MeaningfulStepCount reflects the presence of a weak stem
                // when nothing else is in the chain.
                (unmapped ??= new List<string>()).Add(tag);
                continue;
            }

            if (isStemTransition) continue;

            // Unmapped, non-stem-transition, non-classical-weak, non-flag tag
            // (e.g. "seemingness", "excess", "-sou", abbreviation rewrite tags).
            // Treat as meaningful but unmapped — it won't match skip/weak patterns.
            (unmapped ??= new List<string>()).Add(tag);
        }

        // If every tag we saw was a stem-transition with no classical-weak member,
        // nothing is meaningful.
        if ((conjTypes == null || conjTypes.Count == 0)
            && (unmapped == null || unmapped.Count == 0))
            return new ConjChainAnalysis(hasNeg, hasFml,
                System.Array.Empty<int>(), System.Array.Empty<string>());

        return new ConjChainAnalysis(
            hasNeg,
            hasFml,
            conjTypes ?? (IReadOnlyList<int>)System.Array.Empty<int>(),
            unmapped ?? (IReadOnlyList<string>)System.Array.Empty<string>());
    }
}

// Port of Ichiran's *skip-conj-forms* / *weak-conj-forms* (dict-errata.lisp:1310-1321)
// and test-conj-prop (dict-errata.lisp:1323-1330). Each entry is a pattern over
// (pos, type, neg, fml) with null = :any. Matching runs per-step against the chain.
public static class ConjFormMatcher
{
    // (pos, type, neg, fml) — null = :any
    public readonly record struct Pattern(string? Pos, int? Type, bool? Neg, bool? Fml);

    // *skip-conj-forms* — verbatim port of dict-errata.lisp:1310-1314.
    //   (10 t :any)          imperative, negative
    //   (3 t t)              te-form, negative, formal  (〜て + negative polite chain)
    //   ("vs-s" 5 :any :any) vs-s verbs, potential (any neg/fml)
    public static readonly Pattern[] Skip = new[]
    {
        new Pattern(Pos: null,   Type: IchiranConjType.Imperative, Neg: true, Fml: null),
        new Pattern(Pos: null,   Type: IchiranConjType.Te,         Neg: true, Fml: true),
        new Pattern(Pos: "vs-s", Type: IchiranConjType.Potential,  Neg: null, Fml: null),
    };

    // *weak-conj-forms* — verbatim port of dict-errata.lisp:1316-1321.
    //   51 :any :any   adjective-stem
    //   52 :any :any   negative-stem
    //   53 :any :any   causative-su
    //   54 :any :any   adjective-literary
    //   9  t    :any   tentative + negative  (〜まい)
    public static readonly Pattern[] Weak = new[]
    {
        new Pattern(Pos: null, Type: IchiranConjType.AdjectiveStem,     Neg: null, Fml: null),
        new Pattern(Pos: null, Type: IchiranConjType.NegativeStem,      Neg: null, Fml: null),
        new Pattern(Pos: null, Type: IchiranConjType.CausativeSu,       Neg: null, Fml: null),
        new Pattern(Pos: null, Type: IchiranConjType.AdjectiveLiterary, Neg: null, Fml: null),
        new Pattern(Pos: null, Type: IchiranConjType.Tentative,         Neg: true, Fml: null),
    };

    // True when the step type + chain-level flags + word POS list match at least one
    // entry in the pattern table. Null fields in the pattern act as :any wildcards.
    private static bool MatchesAny(int stepType, ConjChainAnalysis analysis,
                                   IReadOnlyList<string> wordPos, Pattern[] patterns)
    {
        foreach (var p in patterns)
        {
            if (p.Pos != null)
            {
                bool posOk = false;
                foreach (var pos in wordPos)
                    if (pos == p.Pos) { posOk = true; break; }
                if (!posOk) continue;
            }
            if (p.Type.HasValue && p.Type.Value != stepType) continue;
            if (p.Neg.HasValue && p.Neg.Value != analysis.HasNegative) continue;
            if (p.Fml.HasValue && p.Fml.Value != analysis.HasFormal) continue;
            return true;
        }
        return false;
    }

    // Ichiran skip-by-conj-data (dict-errata.lisp:1332-1335): every conj-data matches
    // some skip pattern → score 0. Jiten approximation: every meaningful step in the
    // chain maps to a conj-type AND matches some *skip-conj-forms* entry.
    // Chains with no meaningful steps return false (can't decide).
    public static bool AllMatchSkip(ConjChainAnalysis analysis, IReadOnlyList<string> wordPos)
    {
        if (analysis.MeaningfulStepCount == 0) return false;
        // Any unmapped meaningful tag breaks the "every step matches" invariant —
        // we can't match what we can't classify.
        if (analysis.UnmappedMeaningfulTags.Count > 0) return false;
        foreach (var t in analysis.ConjTypes)
            if (!MatchesAny(t, analysis, wordPos, Skip)) return false;
        return true;
    }

    // Ichiran conj-types-p / prop weak gate (dict.lisp:816-819): when every conj-prop
    // is weak, conj-types-p becomes false, disabling primary-p branches and the common
    // bonus. Classical-weak stem tags (mizenkei etc.) count as weak even though they
    // don't carry a conj-type code.
    public static bool AllMatchWeak(ConjChainAnalysis analysis, IReadOnlyList<string> wordPos)
    {
        if (analysis.MeaningfulStepCount == 0) return false;
        foreach (var t in analysis.ConjTypes)
            if (!MatchesAny(t, analysis, wordPos, Weak)) return false;
        // Unmapped classical-weak stem tags (mizenkei, 'a' stem, izenkei, ka/ke stem)
        // are all weak by construction — ClassicalWeakTags is their membership test.
        foreach (var tag in analysis.UnmappedMeaningfulTags)
            if (!ConjChainAnalysis.ClassicalWeakTags.Contains(tag)) return false;
        return true;
    }
}
