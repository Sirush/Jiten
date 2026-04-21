using System.Text.Json;
using Jiten.Core.Data.JMDict;

namespace Jiten.Parser;

// Per-word emitted record. The generator buffers per-word then emits; the CLI
// decides how to persist them (bulk insert batches).
public readonly record struct ConjugatedFormRecord(
    string Surface,
    int WordId,
    string[] Chain,
    short FormIndex);

// Forward-applies deconjugator rules from real conjugable lemmas to produce a
// (surface → [wordId, chain]) table for the beam. See PLAN_ConjugationTable.md.
//
// Initial state for a word form: tag = null; first applicable rule must have
// DecTag ∈ word.PartsOfSpeech. After applying a rule forward (strip DecEnd,
// append ConEnd) the state becomes rule.ConTag. Subsequent rules must have
// DecTag == current state.
//
// Emission policy per rule type:
//   Std / Rewrite : emit + continue chain
//   NeverFinal    : continue chain only (intermediate stems shouldn't match
//                   a user's surface standalone)
//   OnlyFinal     : emit as terminal — do not extend chain
//
// Constraints (kill the BFS explosion that the naive depth=5 approach hit):
//   1. MaxDepth = 3 (rule-file steps; matches ~1-2 linguistic conjugation
//      steps after stem-state intermediates collapse).
//   2. No-repeat detail: a given Detail tag appears at most once per chain
//      (kills passive-passive, causative-causative, etc.).
//   3. Per-word surface dedupe: one row per (surface, formIndex), shortest
//      chain wins. Collapses the many-paths-one-surface explosion.
//   4. Per-word cap (default 300): hard ceiling, caller logs overflows.
public class ConjugationTableGenerator : IConjugationGenerator
{
    private enum RuleType { Std, Rewrite, NeverFinal, OnlyFinal }

    private sealed record ExpandedRule(RuleType Type, DeconjugationVirtualRule Rule);

    private readonly ExpandedRule[] _rules;
    private readonly HashSet<string> _allDecTags;

    public ConjugationTableGenerator(IEnumerable<DeconjugationRule> rules)
    {
        var expanded = new List<ExpandedRule>();
        var decTags = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in rules)
        {
            RuleType? t = rule.Type switch
            {
                "stdrule" => RuleType.Std,
                "rewriterule" => RuleType.Rewrite,
                "neverfinalrule" => RuleType.NeverFinal,
                "onlyfinalrule" => RuleType.OnlyFinal,
                _ => null
            };
            if (t is null) continue;

            int n = rule.DecEnd.Length;
            for (int i = 0; i < n; i++)
            {
                var vr = new DeconjugationVirtualRule(
                    rule.DecEnd.ElementAtOrDefault(i) ?? rule.DecEnd[0],
                    rule.ConEnd.ElementAtOrDefault(i) ?? rule.ConEnd[0],
                    rule.DecTag?.ElementAtOrDefault(i) ?? rule.DecTag?[0],
                    rule.ConTag?.ElementAtOrDefault(i) ?? rule.ConTag?[0],
                    rule.Detail
                );
                expanded.Add(new ExpandedRule(t.Value, vr));
                if (vr.DecTag != null) decTags.Add(vr.DecTag);
            }
        }

        _rules = expanded.ToArray();
        _allDecTags = decTags;
    }

    public static ConjugationTableGenerator FromSharedResources()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "deconjugator.json");
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            Converters = { new StringArrayConverter() }
        };
        var rules = JsonSerializer.Deserialize<List<DeconjugationRule>>(File.ReadAllText(path), options) ?? new();
        return new ConjugationTableGenerator(rules);
    }

    // A word is conjugable iff at least one of its POS tags appears in any
    // rule's dec_tag. Avoids hardcoding a POS whitelist.
    //
    // Plain nouns (only n/pn/etc., no vs-*) don't actually conjugate — they
    // only reach the rule system via the suru-stem rule (dec_tag=n). That
    // rule is gated separately in Generate(), so a pure noun here would
    // emit only the identity form — wasted work. Skip them at the entry
    // point.
    public bool IsConjugable(JmDictWord word)
    {
        bool hasN = false;
        foreach (var pos in word.PartsOfSpeech)
        {
            if (!_allDecTags.Contains(pos)) continue;
            if (pos == "n") { hasN = true; continue; }
            return true;
        }
        return hasN && HasVsPos(word.PartsOfSpeech);
    }

    private static bool HasVsPos(IEnumerable<string> pos)
    {
        foreach (var p in pos)
        {
            if (p == "vs" || p == "vs-i" || p == "vs-s" || p == "vs-c") return true;
        }
        return false;
    }

    private static bool HasVsPos(HashSet<string> posSet) =>
        posSet.Contains("vs") || posSet.Contains("vs-i") ||
        posSet.Contains("vs-s") || posSet.Contains("vs-c");

    public IEnumerable<ConjugatedFormRecord> Generate(JmDictWord word, int maxDepth = 3, int perWordCap = 300)
    {
        if (!IsConjugable(word)) yield break;

        var posSet = new HashSet<string>(word.PartsOfSpeech, StringComparer.Ordinal);

        // Per-form: best (shortest) chain for each surface, with discovery order
        // preserved. Resetting per-form ensures every form gets its full share of
        // the emission budget — without this, the BFS for early forms (typically
        // EF-loaded in non-deterministic order, often kana variants first) can
        // exhaust the budget before kanji forms emit their conjugations,
        // leaving common surfaces like 決まって/決まっている absent from the table.
        // Total per-word emissions ≤ perWordCap × Forms.Count.
        for (short formIdx = 0; formIdx < word.Forms.Count; formIdx++)
        {
            var form = word.Forms[formIdx];
            var seed = form.Text;
            if (string.IsNullOrEmpty(seed)) continue;

            var bestChain = new Dictionary<string, string[]>();
            var discoveryOrder = new List<string>();

            void Offer(string surface, string[] chain)
            {
                if (bestChain.TryGetValue(surface, out var existing))
                {
                    if (chain.Length < existing.Length)
                        bestChain[surface] = chain;
                    return;
                }
                bestChain[surface] = chain;
                discoveryOrder.Add(surface);
            }

            // Identity form (direct-lookup support in the table).
            Offer(seed, Array.Empty<string>());

            // BFS forward application.
            //   Queue: (surface, chain, stateTag, usedDetails, meaningfulSteps).
            //   stateTag == null means initial state (word's base form).
            //   usedDetails prevents the same Detail from reappearing in the chain.
            //   meaningfulSteps counts only rules with non-empty Detail — pure
            //   state transitions (empty-detail rules like stem-te-verbal→stem-te)
            //   don't consume budget, since they're not linguistically meaningful
            //   conjugations and don't multiplicatively explode (only 2 such rules
            //   in the entire ruleset). Without this exemption, te-iru chains for
            //   v5* verbs (4 rule steps total: stem-ren-less, stem-te-verbal,
            //   stem-te, teiru) couldn't be generated within maxDepth=3, leaving
            //   surfaces like 決まっている unreachable from 決まる.
            //   seen dedupes (surface, chain, stateTag) triples across the search.
            var seen = new HashSet<(string Surface, string ChainKey, string? StateTag)>();
            seen.Add((seed, "", null));

            var frontier = new Queue<(string Surface, string[] Chain, string? StateTag, HashSet<string> UsedDetails, int MeaningfulSteps)>();
            frontier.Enqueue((seed, Array.Empty<string>(), null, new HashSet<string>(StringComparer.Ordinal), 0));

            while (frontier.Count > 0)
            {
                int batchCount = frontier.Count;
                for (int b = 0; b < batchCount; b++)
                {
                    var (surface, chain, stateTag, usedDetails, meaningfulSteps) = frontier.Dequeue();

                    for (int ri = 0; ri < _rules.Length; ri++)
                    {
                        var er = _rules[ri];
                        var rule = er.Rule;

                        // POS / state match.
                        if (stateTag == null)
                        {
                            if (rule.DecTag == null || !posSet.Contains(rule.DecTag)) continue;

                            // Suru-stem gate: the rule dec_tag=n → con_tag=vs-i/vs-s
                            // fires on *every* noun, which exploded the table because
                            // most JMDict nouns are not suru-compatible (e.g. 彼処,
                            // お仕舞). Require the word to also carry one of the vs-*
                            // POS tags — matches how Ichiran's pre-materialisation
                            // restricts suru compounding to the declared vs set.
                            if (rule.DecTag == "n" && !HasVsPos(posSet)) continue;
                        }
                        else
                        {
                            if (rule.DecTag != stateTag) continue;
                        }

                        // No-repeat detail: a tag can appear at most once per chain.
                        if (!string.IsNullOrEmpty(rule.Detail) && usedDetails.Contains(rule.Detail))
                            continue;

                        // Forward rule application (strip DecEnd, append ConEnd).
                        string newSurface;
                        if (er.Type == RuleType.Rewrite)
                        {
                            if (!surface.Equals(rule.DecEnd, StringComparison.Ordinal)) continue;
                            newSurface = rule.ConEnd;
                        }
                        else
                        {
                            if (rule.DecEnd.Length == 0)
                            {
                                newSurface = surface + rule.ConEnd;
                            }
                            else
                            {
                                if (!surface.EndsWith(rule.DecEnd, StringComparison.Ordinal)) continue;
                                var prefixLen = surface.Length - rule.DecEnd.Length;
                                newSurface = string.Concat(surface.AsSpan(0, prefixLen), rule.ConEnd.AsSpan());
                            }
                        }

                        if (string.IsNullOrEmpty(newSurface)) continue;

                        // Build new chain and used-detail set.
                        string[] newChain;
                        HashSet<string> newUsedDetails;
                        if (string.IsNullOrEmpty(rule.Detail))
                        {
                            newChain = chain;
                            newUsedDetails = usedDetails;
                        }
                        else
                        {
                            newChain = new string[chain.Length + 1];
                            Array.Copy(chain, newChain, chain.Length);
                            newChain[chain.Length] = rule.Detail;
                            newUsedDetails = new HashSet<string>(usedDetails, StringComparer.Ordinal) { rule.Detail };
                        }

                        var chainKey = newChain.Length == 0 ? "" : string.Join("|", newChain);
                        if (!seen.Add((newSurface, chainKey, rule.ConTag))) continue;

                        // Ichiran handles te-form + aux verbs (teiru, teoru, tearu, temiru,
                        // teiku, tekuru etc.) via BEAM-LEVEL suffix synthesis, not conjugation
                        // chains. The problematic case is NOT teiru itself but the
                        // MASU-STEM OF teiru: e.g. 間違える → 間違えて → 間違えている →
                        // `(infinitive)` drops る → 間違えてい. This 5-char kana surface scores
                        // as a long compound edge in our lattice and steals boundaries from
                        // the correct 間違えて/いらっしゃる split. Block `(infinitive)` emission
                        // when a prior chain step was a te-form aux attachment — the surface
                        // 〜てい is effectively never the intended parse; 〜て + いる|いく|etc.
                        // is what users wrote. Only infinitive (masu-stem) is blocked; past,
                        // negative, conditional off teiru stay because they are real spoken
                        // forms (食べていた, 食べていない, etc.).
                        bool shouldEmitChain = true;
                        if (!string.IsNullOrEmpty(rule.Detail) && rule.Detail == "(infinitive)")
                        {
                            foreach (var tag in chain) // prior chain, not yet including this rule
                            {
                                if (tag is "teiru" or "temiru" or "teru (teiru)" or "teoru"
                                    or "toru (teoru)" or "tearu" or "teiku" or "teku (teiku)"
                                    or "tekuru" or "toku (for now)" or "for now")
                                { shouldEmitChain = false; break; }
                            }
                        }

                        bool shouldEmit = er.Type != RuleType.NeverFinal && shouldEmitChain;
                        bool shouldEnqueue = er.Type != RuleType.OnlyFinal;

                        int newSteps = string.IsNullOrEmpty(rule.Detail) ? meaningfulSteps : meaningfulSteps + 1;
                        if (newSteps > maxDepth) continue;

                        if (shouldEnqueue)
                            frontier.Enqueue((newSurface, newChain, rule.ConTag, newUsedDetails, newSteps));

                        if (shouldEmit)
                            Offer(newSurface, newChain);
                    }
                }
            }

            // Emit this form's surfaces in discovery order; respect per-form cap.
            int emitted = 0;
            foreach (var surface in discoveryOrder)
            {
                if (emitted >= perWordCap) break;
                yield return new ConjugatedFormRecord(
                    surface, word.WordId, bestChain[surface], formIdx);
                emitted++;
            }
        }
    }
}
