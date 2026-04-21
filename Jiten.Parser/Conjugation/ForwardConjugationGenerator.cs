using Jiten.Core.Data.JMDict;

namespace Jiten.Parser.Conjugation;

// Generates the (surface → [wordId, chain, formIdx]) table using JMdictDB's
// conjo.csv paradigms — the same data Ichiran uses. Replaces the BFS-based
// ConjugationTableGenerator.
//
// Chain encoding: canonical BFS detail strings ("past", "(te form)",
// "(infinitive)", "negative", "passive", …) — chosen so beam-layer consumers
// (BeamResegmentationEngine, IchiranPropScorer, FormCandidateSelector,
// BuildSuffixCompoundEdges) that check for specific tags work unchanged.
// Each rule emits one tag per attribute: primary paradigm, plus "negative"
// if neg=t, plus "polite" if fml=t. Secondary (re-conjugated) outputs
// append the secondary paradigm's tags after the primary's.
//
// Secondary conjugation: matches Ichiran's load-secondary-conjugations. For
// output surfaces from types {potential=5, passive=6, causative=7,
// causative-passive=8} we re-apply a restricted rule set as if the result
// were v1 (all the resulting verbs are ichidan).
public class ForwardConjugationGenerator : IConjugationGenerator
{
    private static readonly HashSet<int> SecondaryFromConjIds = new() { 5, 6, 7, 8 };
    private static readonly HashSet<int> SecondaryTypes = new() { 2, 3, 4, 9, 10, 11, 12, 13 };

    private readonly JmdictConjRuleSet _ruleSet;
    private readonly List<JmdictConjRule> _v1Rules;

    public ForwardConjugationGenerator(JmdictConjRuleSet ruleSet)
    {
        _ruleSet = ruleSet;
        _ruleSet.TryGetRules("v1", out _v1Rules!);
        _v1Rules ??= new();
    }

    public static ForwardConjugationGenerator FromSharedResources() =>
        new(JmdictConjRuleSet.FromSharedResources());

    public bool IsConjugable(JmDictWord word)
    {
        foreach (var pos in word.PartsOfSpeech)
        {
            if (ForwardConjugator.IsPrimaryConjugable(pos)) return true;

            // Plain nouns with vs-* hit the suru-path, but the suru paradigm
            // lives on the vs-i/vs-s POS, not on n. So "IsConjugable" is just
            // "any conjugable POS present"; no special noun gate needed.
        }
        // Polite-aux ございます-family entries get extras via EmitGozaimasuExtras
        // even though their POS isn't in PrimaryConjugablePos.
        if (GozaimasuSeqs.Contains(word.WordId)) return true;
        return false;
    }

    // Signature matches ConjugationTableGenerator for drop-in swap.
    // maxDepth is not used (paradigm count is constant; secondary adds one
    // extra slot). perWordCap still applies but defaults higher — JMdictDB
    // paradigms per v1 verb reach ~800 unique surfaces (60 primary × ~14
    // secondary variants across {potential, passive, causative,
    // causative-passive}), far above the BFS cap of 300.
    public IEnumerable<ConjugatedFormRecord> Generate(JmDictWord word, int maxDepth = 3, int perWordCap = 1500)
    {
        if (!IsConjugable(word)) yield break;

        for (short formIdx = 0; formIdx < word.Forms.Count; formIdx++)
        {
            var form = word.Forms[formIdx];
            var seed = form.Text;
            if (string.IsNullOrEmpty(seed)) continue;

            // Per-form dedupe: one row per surface, shortest chain wins. A
            // paradigm can produce the same surface via multiple POS (e.g. a
            // word carrying both v5r and v5r-i) — keep the first observation.
            var best = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var order = new List<string>();

            void Offer(string surface, string[] chain)
            {
                if (best.TryGetValue(surface, out var prev))
                {
                    if (chain.Length < prev.Length) best[surface] = chain;
                    return;
                }
                best[surface] = chain;
                order.Add(surface);
            }

            Offer(seed, Array.Empty<string>()); // identity

            // Jiten-forward extras: paradigm derivations not present in JMdictDB's
            // conjo.csv that beam-layer suffix compounds (sa/ge/garu/naru/sou)
            // depend on via StemMatches. Ichiran handles these as lexical/grammar
            // derivations in dict-grammar.lisp, not as conjugation rows. We seed
            // them into the table so the existing suffix-compound machinery
            // (BeamResegmentationEngine.StemMatches) recognises the stem.
            //
            //   adj-i 楽しい → 楽し  [(stem)]        — base for suffix-sa/ge/garu
            //   adj-i 楽しい → 楽しく [(adverbial stem)] — base for suffix-naru
            EmitAdjIExtras(word, seed, Offer);

            // Classical / continuative negative in -ず (行かず, 食べず, やらず).
            // Not in JMdictDB's conjo.csv — Ichiran handles via dict-grammar.lisp
            // suffix-adv. Derivation: wherever the non-past-negative okuri ends in
            // "ない", produce the same surface with "ない" → "ず". Tagging as
            // "negative" lets BuildStemStripCompoundEdges/HasConjTag see it.
            EmitZuNegative(word, seed, Offer);

            // Jiten-only colloquial/classical neg variants (Ichiran does not
            // cover these — they're in our deconjugator.json but absent from
            // JMdictDB/dict-grammar.lisp):
            //   ない → ねえ / ねぇ / ねー   (slang えー mega-rule)
            //   ない → ん                    (colloquial abbreviation)
            //   なければ → ねば              (classical provisional)
            // くない variants for adj-i covered by the same loop.
            EmitJitenNegativeVariants(word, seed, Offer);

            // Kuru (vk) and special verbs expose a bare negative-stem (こ from
            // 来ない, し/せ from しない) that beam-layer suffix-sou-nai attaches to
            // (こなさそう, しなさそう). Ichiran derives via dict-grammar's
            // conjugate-secondary-conjugations — for us, emit conj=1 neg=t's
            // euph-only result (drop "ない" from the non-past-negative okuri) with
            // an (infinitive) tag so StemMatches.MasuStem sees it.
            EmitBareNegStem(word, seed, Offer);

            // Polite-aux verbs ending in ます whose POS (exp/pol/aux-v) isn't in
            // PrimaryConjugablePos — the forward engine never emits their
            // ません/ました/まして/ましょう variants. Port of Ichiran's
            // add-gozaimasu-conjs (dict-errata.lisp:263).
            EmitGozaimasuExtras(word, seed, Offer);

            EmitCopulaContractions(word, seed, Offer);

            // Two-phase to keep the primary paradigm ahead of its secondary
            // explosion. Without this, Potential (conj=5) runs its ~16
            // secondary conjugations before the next primary slot (Passive
            // conj=6) even starts emitting — under a per-form cap this
            // crowds out Imperative/Volitional/Conditional entirely.
            var secondarySeeds = new List<(string surface, string[] primaryTags)>();

            foreach (var pos in word.PartsOfSpeech)
            {
                if (!_ruleSet.TryGetRules(pos, out var rules)) continue;
                if (!ForwardConjugator.IsPrimaryConjugable(pos)) continue;

                foreach (var rule in rules)
                {
                    var surface = ForwardConjugator.Apply(seed, rule);
                    if (surface == null || surface.Length == 0) continue;

                    var primaryTags = EncodeTags(rule);
                    if (primaryTags.Length > 0) Offer(surface, primaryTags);

                    if (SecondaryFromConjIds.Contains(rule.ConjId))
                        secondarySeeds.Add((surface, primaryTags));
                }
            }

            // Secondary pass: re-conjugate passive/causative/potential outputs
            // as v1. Ichiran's load-secondary-conjugations gates by conj-id
            // only, not by neg/fml — we do the same.
            foreach (var (surface, primaryTags) in secondarySeeds)
            {
                foreach (var sec in _v1Rules)
                {
                    if (!SecondaryTypes.Contains(sec.ConjId)) continue;
                    var sSurface = ForwardConjugator.Apply(surface, sec);
                    if (sSurface == null || sSurface.Length == 0) continue;
                    var secTags = EncodeTags(sec);
                    var combined = new string[primaryTags.Length + secTags.Length];
                    Array.Copy(primaryTags, combined, primaryTags.Length);
                    Array.Copy(secTags, 0, combined, primaryTags.Length, secTags.Length);
                    Offer(sSurface, combined);
                }
            }

            int emitted = 0;
            foreach (var surface in order)
            {
                if (emitted >= perWordCap) break;
                yield return new ConjugatedFormRecord(surface, word.WordId, best[surface], formIdx);
                emitted++;
            }
        }
    }

    private static readonly string[] AdjStemTag = { "(stem)" };
    private static readonly string[] AdjAdverbialTag = { "(adverbial stem)" };
    private static readonly string[] NegativeTag = { "negative" };

    // Slang / classical negative rewrites on top of any non-past-negative
    // okuri ending in "ない". Cheaper and more predictable than re-running
    // the full paradigm with a mega-rule table. Applied to both verb
    // negatives (行かない → 行かねえ) and adj-i negatives (楽しくない → 楽しくねえ).
    //
    // Note: we gate by `Okuri.EndsWith("ない")` — this catches BOTH verb and
    // adj-i rules since adj-i conj=1 neg=t's okuri is "くない" (ends in ない).
    private static readonly (string FromSuffix, string ToSuffix)[] NaiRewrites =
    {
        ("ない", "ねえ"),
        ("ない", "ねぇ"),
        ("ない", "ねー"),
        ("ない", "ん"),
        ("ない", "ぬ"),        // classical negative 行かぬ
    };

    // -nakereba → -neba (classical provisional). Applied to conj=4 neg=t onum=1.
    private static readonly (string FromSuffix, string ToSuffix)[] NakerebaRewrites =
    {
        ("なければ", "ねば"),
        ("なければ", "ねーば"),
        ("なければ", "なきゃ"),  // BFS-compatible colloquial contraction
        ("なければ", "なくちゃ"),
    };

    // Ichiran-style ultra-colloquial provisional contraction:
    //   -eba → -ya (row 3 of 50 swaps to row 2 of Y-row).
    //   For our purposes the commonly-encountered endings are:
    //     れば → りゃ (v1, v5r and secondary passives)
    //     ければ → きゃ / けりゃ (adj-i provisional)
    private static readonly (string FromSuffix, string ToSuffix)[] ProvisionalRewrites =
    {
        ("れば", "りゃ"),
        ("ければ", "きゃ"),
        ("ければ", "けりゃ"),
    };

    private static readonly string[] ProvTag = { "provisional conditional" };

    private static readonly string[] NegProvTag = { "negative", "provisional conditional" };

    private void EmitJitenNegativeVariants(JmDictWord word, string seed, Action<string, string[]> offer)
    {
        foreach (var pos in word.PartsOfSpeech)
        {
            if (!_ruleSet.TryGetRules(pos, out var rules)) continue;
            if (!ForwardConjugator.IsPrimaryConjugable(pos)) continue;

            foreach (var rule in rules)
            {
                // Non-past negative plain (conj=1, neg=t, fml=f, onum=1) → slang / -n
                if (rule.ConjId == 1 && rule.Negative && !rule.Formal && rule.OrderNum == 1 &&
                    rule.Okuri.EndsWith("ない", StringComparison.Ordinal))
                {
                    RewriteEmit(seed, rule, NaiRewrites, NegativeTag, offer);
                }

                // Provisional negative (conj=4, neg=t, fml=f, onum=1) → classical -neba / colloquial -nakya
                if (rule.ConjId == 4 && rule.Negative && !rule.Formal && rule.OrderNum == 1 &&
                    rule.Okuri.EndsWith("なければ", StringComparison.Ordinal))
                {
                    RewriteEmit(seed, rule, NakerebaRewrites, NegProvTag, offer);
                }

                // Affirmative provisional (conj=4, neg=f, fml=f, onum=1) → colloquial contraction.
                if (rule.ConjId == 4 && !rule.Negative && !rule.Formal && rule.OrderNum == 1)
                {
                    RewriteEmit(seed, rule, ProvisionalRewrites, ProvTag, offer);
                }
            }
        }

        // Words whose reading itself ends in ない (e.g. しょうがない, つまらない, くだらない)
        // need colloquial variants generated by direct suffix replacement rather than
        // the rule-based path above (which produces くない → くねぅ, not ない → ねぅ).
        // Gate to adj-i only: compound nouns like 案内/以内 also end in ない but must not get fake conjugations.
        if (seed.EndsWith("ない", StringComparison.Ordinal) && seed.Length > 2
            && word.PartsOfSpeech.Any(p => p is "adj-i" or "adj-ix"))
        {
            string stemBase = seed[..^2];
            foreach (var (_, to) in NaiRewrites)
                offer(stemBase + to, NegativeTag);
        }
    }

    private static void RewriteEmit(
        string seed,
        JmdictConjRule rule,
        (string From, string To)[] rewrites,
        string[] tags,
        Action<string, string[]> offer)
    {
        foreach (var (from, to) in rewrites)
        {
            if (!rule.Okuri.EndsWith(from, StringComparison.Ordinal)) continue;
            var newOkuri = rule.Okuri.Substring(0, rule.Okuri.Length - from.Length) + to;
            var synth = new JmdictConjRule(rule.PosId, rule.ConjId, rule.Negative, rule.Formal,
                                           rule.OrderNum, rule.Stem, newOkuri, rule.Euphr, rule.Euphk);
            var surface = ForwardConjugator.Apply(seed, synth);
            if (surface == null || surface.Length == 0) continue;
            offer(surface, tags);
        }
    }

    // Bare negative-stem emission uses a dedicated tag rather than (infinitive).
    // Historical hack tagged the 'a'-stem (こが from こぐ via ぐ→がない strip ない)
    // as "(infinitive)" so StemMatches.MasuStem would see it for suffix-sou-nai —
    // but that collision promoted bare a-stems to standalone lattice edges with
    // full masu-stem scoring, causing e.g. 俺のどこが悪い → 俺|のど|こが悪い (漕ぐ).
    // Now tagged as Ichiran's canonical "(a stem)" (ClassicalWeakTag in ConjChainAnalysis)
    // so AllMatchWeak classifies it as weak. StemMatches.MasuStem is updated to
    // accept it too so the downstream sou-nai/adv attachment still fires.
    private static readonly string[] BareNegStemTag = { "('a' stem)" };

    private void EmitBareNegStem(JmDictWord word, string seed, Action<string, string[]> offer)
    {
        foreach (var pos in word.PartsOfSpeech)
        {
            if (!_ruleSet.TryGetRules(pos, out var rules)) continue;
            if (!ForwardConjugator.IsPrimaryConjugable(pos)) continue;

            foreach (var rule in rules)
            {
                if (rule.ConjId != 1 || !rule.Negative || rule.Formal || rule.OrderNum != 1) continue;
                if (!rule.Okuri.EndsWith("ない", StringComparison.Ordinal)) continue;

                var stemOkuri = rule.Okuri.Substring(0, rule.Okuri.Length - 2);
                // Skip if the remainder is empty AND euphr/euphk is empty — that
                // would emit a stripped-only surface with no distinguishing suffix,
                // duplicating conj=13 which we already emit.
                if (stemOkuri.Length == 0 &&
                    string.IsNullOrEmpty(rule.Euphr) &&
                    string.IsNullOrEmpty(rule.Euphk)) continue;

                var synth = new JmdictConjRule(rule.PosId, rule.ConjId, rule.Negative, rule.Formal,
                                               rule.OrderNum, rule.Stem, stemOkuri, rule.Euphr, rule.Euphk);
                var surface = ForwardConjugator.Apply(seed, synth);
                if (surface == null || surface.Length == 0) continue;
                offer(surface, BareNegStemTag);
            }
        }
    }

    private void EmitZuNegative(JmDictWord word, string seed, Action<string, string[]> offer)
    {
        foreach (var pos in word.PartsOfSpeech)
        {
            if (!_ruleSet.TryGetRules(pos, out var rules)) continue;
            if (!ForwardConjugator.IsPrimaryConjugable(pos)) continue;

            foreach (var rule in rules)
            {
                if (rule.ConjId != 1 || !rule.Negative || rule.Formal || rule.OrderNum != 1) continue;
                if (!rule.Okuri.EndsWith("ない", StringComparison.Ordinal)) continue;

                // Suru verbs: "しない" → "せず", not "しず". Ichiran's dict-grammar
                // suffix-adv handles the vowel shift; the plain rewrite would
                // emit wrong surfaces here. Skip and let a dedicated rule or
                // beam-layer suffix cover せず/しず if needed.
                if (pos == "vs-i" || pos == "vs-s") continue;

                var zuOkuri = rule.Okuri.Substring(0, rule.Okuri.Length - 2) + "ず";
                var synth = new JmdictConjRule(rule.PosId, rule.ConjId, rule.Negative, rule.Formal, rule.OrderNum,
                                               rule.Stem, zuOkuri, rule.Euphr, rule.Euphk);

                var surface = ForwardConjugator.Apply(seed, synth);
                if (surface == null || surface.Length == 0) continue;
                offer(surface, NegativeTag);
            }
        }
    }

    // Ichiran add-gozaimasu-conjs target seqs (dict-errata.lisp:263):
    // 1612690 ございます, 2253080 でございます.
    private static readonly HashSet<int> GozaimasuSeqs = new() { 1612690, 2253080 };

    private static void EmitGozaimasuExtras(JmDictWord word, string seed, Action<string, string[]> offer)
    {
        if (!GozaimasuSeqs.Contains(word.WordId)) return;
        if (seed.Length < 2 || !seed.EndsWith("ます")) return;
        var root = seed.Substring(0, seed.Length - 2);
        offer(root + "ません",   new[] { "negative", "polite" });
        offer(root + "ました",   new[] { "past", "polite" });
        offer(root + "まして",   new[] { "(te form)", "polite" });
        offer(root + "ましょう", new[] { "polite volitional" });
        offer(root + "ましたら", new[] { "conditional", "polite" });
        offer(root + "ましたり", new[] { "tari", "polite" });
    }

    private void EmitCopulaContractions(JmDictWord word, string seed, Action<string, string[]> offer)
    {
        if (!word.PartsOfSpeech.Contains("cop")) return;
        if (!_ruleSet.TryGetRules("cop", out var rules)) return;

        foreach (var rule in rules)
        {
            if (!rule.Okuri.Contains("では")) continue;
            var surface = ForwardConjugator.Apply(seed, rule);
            if (surface == null || surface.Length == 0) continue;
            var contracted = surface.Replace("では", "じゃ");
            if (contracted == surface) continue;
            var tags = EncodeTags(rule);
            if (tags.Length > 0) offer(contracted, tags);
        }
    }

    private static void EmitAdjIExtras(JmDictWord word, string seed, Action<string, string[]> offer)
    {
        bool isAdjI = false;
        foreach (var p in word.PartsOfSpeech)
        {
            if (p == "adj-i" || p == "adj-ix") { isAdjI = true; break; }
        }
        if (!isAdjI) return;

        // Gate on a trailing い — drops archaic/variant forms that don't fit
        // the paradigm (e.g. adj-ix いい has its own stem via conjo euphr).
        if (string.IsNullOrEmpty(seed) || seed[^1] != 'い') return;
        if (seed.Length < 2) return;

        var stem = seed.Substring(0, seed.Length - 1);
        offer(stem, AdjStemTag);
        offer(stem + "く", AdjAdverbialTag);
    }

    // Map JMdictDB conj-id → canonical BFS detail string. Empty string means
    // "don't emit a primary tag" (non-past identity — neg/fml alone carry it).
    // These strings match the exact tags produced by the deconjugator.json
    // BFS path so beam-layer consumers work unchanged.
    private static readonly Dictionary<int, string> PrimaryTagForConjId = new()
    {
        [1] = "",                        // non-past
        [2] = "past",
        [3] = "(te form)",
        [4] = "provisional conditional",
        [5] = "potential",
        [6] = "passive",
        [7] = "causative",
        [8] = "causative-passive",
        [9] = "volitional",
        [10] = "imperative",
        [11] = "conditional",            // -tara
        [12] = "tari",
        [13] = "(infinitive)",           // masu-stem / ren'youkei
    };

    private static string[] EncodeTags(JmdictConjRule rule)
    {
        var primary = PrimaryTagForConjId.TryGetValue(rule.ConjId, out var p) ? p : string.Empty;

        // Volitional flips: fml=t onum=1 → "polite volitional"; neg=t → "mai" /
        // presumptive. Mirror the BFS synonyms so IchiranPropScorer's
        // HasConjTag("volitional") still hits on all volitional variants via
        // the primary tag, and "mai" is recognised distinctly when present.
        if (rule.ConjId == 9)
        {
            if (rule.Negative) primary = "mai";
            else if (rule.Formal) primary = "polite volitional";
            else primary = "volitional";
        }

        int count = 0;
        if (!string.IsNullOrEmpty(primary)) count++;
        if (rule.Negative && rule.ConjId != 9) count++;
        if (rule.Formal) count++;

        if (count == 0) return Array.Empty<string>();
        var tags = new string[count];
        int i = 0;
        if (!string.IsNullOrEmpty(primary)) tags[i++] = primary;
        if (rule.Negative && rule.ConjId != 9) tags[i++] = "negative";
        if (rule.Formal) tags[i++] = "polite";
        return tags;
    }
}
