using Jiten.Core.Data;
using Jiten.Parser;
using Jiten.Parser.Diagnostics;
using Jiten.Parser.Scoring;

namespace Jiten.Parser.Grammar;

internal static class TransitionRuleEngine
{
    private static readonly TransitionRule[] LeadingStripRules = Array.FindAll(
        TransitionRuleSets.HardRules,
        r => r.Id is "leading-aux-strip" or "particle-at-sentence-start");

    internal static void ApplyHardRules(
        List<(WordInfo word, int pos, int len)> words,
        Func<string, bool> hasLookup,
        ParserDiagnostics? diagnostics = null)
    {
        var rules = TransitionRuleSets.HardRules;

        // Pass 1 needs a while loop: removing index 0 exposes a new index 0 that also needs checking.
        var leadingStripRules = LeadingStripRules;
        bool leadingRemoved;
        do
        {
            leadingRemoved = false;
            if (words.Count == 0) break;
            var window = BuildWindow(words, 0);
            foreach (var rule in leadingStripRules)
            {
                if (!MatchesAll(window, rule.WhenToken)) continue;
                if (IsValidState(window, rule.ValidIf)) continue;
                diagnostics?.LogTransitionViolation(rule.Id, window);
                words.RemoveAt(0);
                leadingRemoved = true;
                break;
            }
        } while (leadingRemoved);

        // Pass 2: backwards pass for context-dependent rules (aux following wrong POS, orphaned counters)
        for (int i = words.Count - 1; i >= 0; i--)
        {
            var window = BuildWindow(words, i);
            foreach (var rule in rules)
            {
                if (rule.Id is "leading-aux-strip" or "particle-at-sentence-start") continue;
                if (!MatchesAll(window, rule.WhenToken)) continue;
                if (IsValidState(window, rule.ValidIf)) continue;

                diagnostics?.LogTransitionViolation(rule.Id, window);
                ApplyViolation(rule, words, i, hasLookup);
                break;
            }
        }
    }

    // ValidIf semantics: empty means "never valid" (always a violation when WhenToken matches).
    // Non-empty: valid only when ALL conditions match.
    private static bool IsValidState(TokenWindow w, MatchCondition[] validIf)
    {
        if (validIf.Length == 0) return false;
        return MatchesAll(w, validIf);
    }

    private static bool MatchesAll(TokenWindow w, MatchCondition[] conditions)
    {
        foreach (var c in conditions)
        {
            bool ok = c switch
            {
                MatchCondition.IsVerbOnlyAux =>
                    w.Current.PartOfSpeech == PartOfSpeech.Auxiliary &&
                    TransitionRuleSets.VerbOnlyAuxDictForms.Contains(w.Current.DictionaryForm),

                MatchCondition.IsVerbOrAdjAux =>
                    w.Current.PartOfSpeech == PartOfSpeech.Auxiliary &&
                    TransitionRuleSets.VerbOrAdjAuxDictForms.Contains(w.Current.DictionaryForm),

                MatchCondition.IsVerbAttachingAux =>
                    w.Current.PartOfSpeech == PartOfSpeech.Auxiliary &&
                    (TransitionRuleSets.VerbOnlyAuxDictForms.Contains(w.Current.DictionaryForm) ||
                     TransitionRuleSets.VerbOrAdjAuxDictForms.Contains(w.Current.DictionaryForm)),

                MatchCondition.IsAuxiliary =>
                    w.Current.PartOfSpeech == PartOfSpeech.Auxiliary,

                MatchCondition.IsCounter =>
                    w.Current.PartOfSpeech == PartOfSpeech.Counter ||
                    (w.Current.PartOfSpeech == PartOfSpeech.Suffix &&
                     w.Current.HasPartOfSpeechSection(PartOfSpeechSection.Counter)),

                MatchCondition.IsSentenceInitial =>
                    w.Index == 0,

                MatchCondition.PrevIsVerbOrAux =>
                    w.Prev?.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.Auxiliary,

                MatchCondition.PrevIsVerbAuxOrIAdj =>
                    w.Prev?.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.Auxiliary
                                         or PartOfSpeech.IAdjective,

                MatchCondition.PrevIsVerbAuxIAdjOrSfp =>
                    w.Prev?.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.Auxiliary
                                         or PartOfSpeech.IAdjective
                    || (w.Prev?.PartOfSpeech == PartOfSpeech.Particle &&
                        w.Prev?.HasPartOfSpeechSection(PartOfSpeechSection.SentenceEndingParticle) == true),

                MatchCondition.PrevIsNumericOrNoun =>
                    w.Prev?.PartOfSpeech is PartOfSpeech.Numeral or PartOfSpeech.Noun
                                         or PartOfSpeech.CommonNoun or PartOfSpeech.Pronoun
                                         or PartOfSpeech.Name,

                MatchCondition.PrevIsAuxiliary =>
                    w.Prev?.PartOfSpeech == PartOfSpeech.Auxiliary,

                MatchCondition.PrevIsAuxiliaryOrParticle =>
                    w.Prev?.PartOfSpeech is PartOfSpeech.Auxiliary or PartOfSpeech.Particle,

                MatchCondition.PrevExists =>
                    w.Prev != null,

                MatchCondition.IsSentenceEndingParticle =>
                    w.Current.PartOfSpeech == PartOfSpeech.Particle &&
                    w.Current.HasPartOfSpeechSection(PartOfSpeechSection.SentenceEndingParticle),

                MatchCondition.NextIsContentWord =>
                    w.Next?.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                        or PartOfSpeech.Name or PartOfSpeech.Pronoun or PartOfSpeech.Verb
                        or PartOfSpeech.IAdjective or PartOfSpeech.NaAdjective
                        or PartOfSpeech.NominalAdjective or PartOfSpeech.Adverb
                        or PartOfSpeech.AdverbTo or PartOfSpeech.Numeral
                        or PartOfSpeech.PrenounAdjectival or PartOfSpeech.Counter
                        or PartOfSpeech.Prefix or PartOfSpeech.Expression,

                MatchCondition.IsPrefix =>
                    w.Current.PartOfSpeech == PartOfSpeech.Prefix,

                MatchCondition.IsSentenceFinal =>
                    w.Index == w.Count - 1,

                MatchCondition.NextIsParticle =>
                    w.Next?.PartOfSpeech == PartOfSpeech.Particle,

                MatchCondition.IsSuffix =>
                    w.Current.PartOfSpeech == PartOfSpeech.Suffix,

                MatchCondition.IsStrictCaseMarkingParticle =>
                    w.Current.PartOfSpeech == PartOfSpeech.Particle &&
                    TransitionRuleSets.StrictCaseMarkingParticles.Contains(w.Current.DictionaryForm),

                _ => false
            };
            if (!ok) return false;
        }
        return true;
    }

    private static void ApplyViolation(
        TransitionRule rule,
        List<(WordInfo word, int pos, int len)> words,
        int i,
        Func<string, bool> hasLookup)
    {
        switch (rule.OnViolation)
        {
            case ViolationAction.RemoveCurrent:
                words.RemoveAt(i);
                break;

            case ViolationAction.MergeWithPrevious:
                if (i == 0)
                {
                    words.RemoveAt(i);
                    break;
                }
                var (prevWord, prevPos, prevLen) = words[i - 1];
                var merged = prevWord.Text + words[i].word.Text;
                if (hasLookup(merged))
                {
                    var auxLen = words[i].len;
                    words[i - 1] = (new WordInfo(prevWord)
                    {
                        Text = merged,
                        DictionaryForm = merged,
                        NormalizedForm = merged,
                        PartOfSpeech = prevWord.PartOfSpeech
                    }, prevPos, prevLen + auxLen);
                    words.RemoveAt(i);
                }
                else
                {
                    words.RemoveAt(i);
                }
                break;

            case ViolationAction.ReclassifyCurrentAsNoun:
                words[i].word.PartOfSpeech = PartOfSpeech.Noun;
                break;
        }
    }

    private static bool IsSingleKanji(string? text) =>
        text is { Length: 1 } && text[0] >= '\u4E00' && text[0] <= '\u9FFF';

    private static bool HasKanji(string text)
    {
        foreach (var c in text)
            if (c >= '\u4E00' && c <= '\u9FFF') return true;
        return false;
    }

    private static bool IsNounLikePOS(PartOfSpeech p) =>
        p is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
            or PartOfSpeech.NaAdjective or PartOfSpeech.Pronoun
            or PartOfSpeech.Name or PartOfSpeech.NominalAdjective;

    // Approximation of Ichiran's filter-is-conjugation :negative gate: surface
    // ends in a negative-form tail (ない / ねえ / ぬ / ん) or is one of these.
    // Misses nuanced cases (nakute, zu, nai-continuation) but matches the common
    // negative predicates that trigger shika-negative binding.
    private static bool IsNegativeSurface(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (text.EndsWith("ない", StringComparison.Ordinal)) return true;
        if (text.EndsWith("なかった", StringComparison.Ordinal)) return true;
        if (text.EndsWith("ねえ", StringComparison.Ordinal)) return true;
        if (text.EndsWith("ねぇ", StringComparison.Ordinal)) return true;
        if (text.EndsWith("ぬ", StringComparison.Ordinal)) return true;
        if (text.Length == 1 && text[0] == 'ん') return true;
        if (text.EndsWith("ません", StringComparison.Ordinal)) return true;
        if (text.EndsWith("ませんでした", StringComparison.Ordinal)) return true;
        return false;
    }

    // Ichiran penalty-short predicate: 1-char kana (hiragana/katakana), not と.
    // Hiragana 0x3040-0x309F, Katakana 0x30A0-0x30FF. と / ト explicitly excluded.
    private static bool IsShortKanaNotTo(string? text)
    {
        if (text is not { Length: 1 }) return false;
        char c = text[0];
        bool isKana = (c >= '\u3040' && c <= '\u309F') || (c >= '\u30A0' && c <= '\u30FF');
        if (!isKana) return false;
        return c != 'と' && c != 'ト';
    }

    private static TokenWindow BuildWindow(List<(WordInfo word, int pos, int len)> words, int i)
    {
        var prev = i > 0 ? words[i - 1].word : null;
        var next = i + 1 < words.Count ? words[i + 1].word : null;
        return new TokenWindow(prev, words[i].word, next, i, words.Count);
    }

    internal static (int bonus, List<string> rulesMatched) EvaluateSoftRules(ScoringWindow window)
    {
        int bonus = 0;
        var rulesMatched = new List<string>();
        var ctx = ConditionContext.FromScoringWindow(window);

        foreach (var rule in TransitionRuleSets.SoftRules)
        {
            if (!MatchesAll(ctx, rule.CandidateMatch)) continue;
            if (!MatchesAll(ctx, rule.ContextMatch)) continue;

            bonus += rule.Delta;
            rulesMatched.Add(rule.Id);
        }

        return (bonus, rulesMatched);
    }

    // Ichiran-mode synergies — evaluated ONLY on the pure-Ichiran beam path. Uses raw
    // Ichiran values (no halving), additive on top of multiplicative prop×coeff node
    // scores. Kept separate from EvaluateSoftRules so the two rule sets evolve
    // independently — Sudachi-mode tiebreakers and Ichiran-native synergies have
    // different calibration requirements.
    internal static int EvaluateIchiranSynergies(ScoringWindow window)
    {
        int bonus = 0;
        var ctx = ConditionContext.FromScoringWindow(window);

        foreach (var rule in TransitionRuleSets.IchiranSynergies)
        {
            if (!MatchesAll(ctx, rule.CandidateMatch)) continue;
            if (!MatchesAll(ctx, rule.ContextMatch)) continue;
            bonus += rule.Delta;
        }

        // §13.1 length-dependent synergy formulas. Ichiran's noun-particle and
        // to-adverbs synergies scale with surface length; we can't express that
        // as a single integer delta, so they live here as a post-loop block.
        bonus += EvaluateLengthFormulas(ctx);

        return bonus;
    }

    // §13.1 Ichiran length-dependent synergies (applied on the IchiranSynergies
    // channel, raw additive). Ported formulas from dict-grammar.lisp:
    //   synergy-noun-particle   : 10 + 4 * len(r)  (r = next particle)
    //   synergy-to-adverbs      : 10 + 10 * len(l) (l = left adv-to)
    private static int EvaluateLengthFormulas(ConditionContext ctx)
    {
        int bonus = 0;

        // synergy-to-adverbs
        bool leftAdvTo = ctx.CandidatePOS.Contains(PartOfSpeech.AdverbTo);
        if (leftAdvTo && ctx.NextText == "と"
            && ctx.NextPOS?.Contains(PartOfSpeech.Particle) == true)
        {
            bonus += 10 + 10 * ctx.CandidateText.Length;
        }

        // synergy-noun-particle: filter-is-noun + *noun-particles* set,
        // score = 10 + 4*len(r). Ichiran's filter-is-noun gate is (or k l (and p c))
        // where k=kanji-p, l=long-p. Adverb-primary words (e.g. まだ which has adj-na
        // secondary) need kanji or length ≥ 4 to qualify — mirrors Ichiran's long-p gate.
        bool leftIsSubstantiveNoun =
            ctx.CandidatePOS.Any(p => p is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                                       or PartOfSpeech.NaAdjective or PartOfSpeech.Pronoun
                                       or PartOfSpeech.Name or PartOfSpeech.NominalAdjective)
            && (HasKanji(ctx.CandidateText) || ctx.CandidateText.Length >= 2);
        if (leftIsSubstantiveNoun && ctx.NextText != null
            && TransitionRuleSets.IchiranCompoundNounParticles.Contains(ctx.NextText))
        {
            bonus += 10 + 4 * ctx.NextText.Length;
        }

        return bonus;
    }


    internal static bool HasApplicableSoftRules(ScoringWindow window)
    {
        var ctx = ConditionContext.FromScoringWindow(window);
        foreach (var rule in TransitionRuleSets.SoftRules)
        {
            if (!MatchesAll(ctx, rule.CandidateMatch)) continue;
            if (MatchesAll(ctx, rule.ContextMatch)) return true;
        }

        return false;
    }

    internal static bool CouldAnySoftRuleApply(
        List<PartOfSpeech> currentPOS, string currentText,
        List<PartOfSpeech>? prevPOS, string? prevText,
        List<PartOfSpeech>? nextPOS, string? nextText)
    {
        if (currentPOS.Count == 0) return false;

        var ctx = new ConditionContext(currentPOS, currentText, prevPOS, prevText, nextPOS, nextText);
        foreach (var rule in TransitionRuleSets.SoftRules)
        {
            if (!MatchesAll(ctx, rule.CandidateMatch)) continue;
            if (MatchesAll(ctx, rule.ContextMatch)) return true;
        }

        return false;
    }

    private readonly record struct ConditionContext(
        List<PartOfSpeech> CandidatePOS,
        string CandidateText,
        List<PartOfSpeech>? PrevPOS,
        string? PrevText,
        List<PartOfSpeech>? NextPOS,
        string? NextText,
        bool CandidateIsSuruNounVal = false,
        List<string>? CandidateJmDictPos = null,
        IReadOnlyList<string>? NextConjChain = null,
        int CandidateWordId = 0)
    {
        public static ConditionContext FromScoringWindow(ScoringWindow w) => new(
            w.Candidate.Word.CachedPOS,
            w.Candidate.Form.Text,
            w.PrevResolvedPOS,
            w.PrevText,
            w.NextResolvedPOS,
            w.NextText,
            w.Candidate.Word.PartsOfSpeech.Any(p => p is "vs" or "vs-i" or "vs-s"),
            w.Candidate.Word.PartsOfSpeech,
            w.NextConjChain,
            w.Candidate.Word.WordId);
    }

    private static bool HasNegativeTag(IReadOnlyList<string>? chain)
    {
        if (chain == null) return false;
        foreach (var t in chain)
            if (t != null && t.Contains("negative", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool MatchesAll(ConditionContext ctx, ScoringCondition[] conditions)
    {
        foreach (var c in conditions)
        {
            bool ok = c switch
            {
                ScoringCondition.CandidateIsNounLike =>
                    ctx.CandidatePOS.Any(p => p is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                                                   or PartOfSpeech.NaAdjective or PartOfSpeech.Pronoun
                                                   or PartOfSpeech.Name or PartOfSpeech.NominalAdjective),

                ScoringCondition.CandidateIsNaAdj =>
                    ctx.CandidatePOS.Contains(PartOfSpeech.NaAdjective),

                ScoringCondition.CandidateIsAdverb =>
                    ctx.CandidatePOS.Any(p => p is PartOfSpeech.Adverb or PartOfSpeech.AdverbTo),

                ScoringCondition.CandidateIsAuxiliary =>
                    ctx.CandidatePOS.Contains(PartOfSpeech.Auxiliary),

                ScoringCondition.CandidateIsParticle =>
                    ctx.CandidatePOS.Contains(PartOfSpeech.Particle),

                ScoringCondition.CandidateIsSingleKanaNonParticle =>
                    ctx.CandidateText.Length <= 1 && !ctx.CandidatePOS.Contains(PartOfSpeech.Particle),

                ScoringCondition.NextIsCommonParticle =>
                    ctx.NextPOS?.Contains(PartOfSpeech.Particle) == true
                    && ctx.NextText != null && TransitionRuleSets.CommonParticles.Contains(ctx.NextText),

                ScoringCondition.NextIsCopula =>
                    ctx.NextText != null && TransitionRuleSets.CopulaForms.Contains(ctx.NextText),

                ScoringCondition.NextIsNaConnector =>
                    ctx.NextText is "な" or "に",

                ScoringCondition.NextIsVerbOrIAdj =>
                    ctx.NextPOS != null
                    && (ctx.NextPOS.Contains(PartOfSpeech.Verb) || ctx.NextPOS.Contains(PartOfSpeech.IAdjective)),

                ScoringCondition.PrevIsVerbOrIAdj =>
                    ctx.PrevPOS != null
                    && (ctx.PrevPOS.Contains(PartOfSpeech.Verb) || ctx.PrevPOS.Contains(PartOfSpeech.IAdjective)),

                ScoringCondition.PrevIsParticle =>
                    ctx.PrevPOS?.Contains(PartOfSpeech.Particle) == true,

                ScoringCondition.NextIsParticle =>
                    ctx.NextPOS?.Contains(PartOfSpeech.Particle) == true,

                ScoringCondition.PrevIsSingleKanaNonParticle =>
                    ctx.PrevText is { Length: 1 } && ctx.PrevPOS?.Contains(PartOfSpeech.Particle) != true,

                ScoringCondition.NextIsSingleKanaNonParticle =>
                    ctx.NextText is { Length: 1 } && ctx.NextPOS?.Contains(PartOfSpeech.Particle) != true,

                ScoringCondition.CandidateIsPredicateHost =>
                    ctx.CandidatePOS.Any(p => p is PartOfSpeech.Verb
                                                 or PartOfSpeech.IAdjective
                                                 or PartOfSpeech.Auxiliary),

                ScoringCondition.CandidateIsNoParticle =>
                    ctx.CandidateText == "の" && ctx.CandidatePOS.Contains(PartOfSpeech.Particle),

                ScoringCondition.NextIsExplanatoryN =>
                    ctx.NextText != null
                    && TransitionRuleSets.ExplanatoryNForms.Contains(ctx.NextText),

                ScoringCondition.PrevIsVerbAuxOrIAdj =>
                    ctx.PrevPOS != null
                    && (ctx.PrevPOS.Contains(PartOfSpeech.Verb)
                        || ctx.PrevPOS.Contains(PartOfSpeech.Auxiliary)
                        || ctx.PrevPOS.Contains(PartOfSpeech.IAdjective)),

                ScoringCondition.CandidateIsCounter =>
                    ctx.CandidatePOS.Contains(PartOfSpeech.Counter),

                ScoringCondition.PrevIsNumeral =>
                    ctx.PrevPOS?.Contains(PartOfSpeech.Numeral) == true,

                ScoringCondition.PrevIsNotNumericLike =>
                    ctx.PrevPOS == null
                    || !ctx.PrevPOS.Any(p => p is PartOfSpeech.Numeral or PartOfSpeech.Noun
                                                or PartOfSpeech.CommonNoun or PartOfSpeech.Pronoun
                                                or PartOfSpeech.Name),

                ScoringCondition.CandidateIsSingleKanji =>
                    IsSingleKanji(ctx.CandidateText),

                ScoringCondition.PrevIsSingleKanji =>
                    IsSingleKanji(ctx.PrevText),

                ScoringCondition.NextIsSingleKanji =>
                    IsSingleKanji(ctx.NextText),

                ScoringCondition.NextIsConditionalParticle =>
                    ctx.NextPOS?.Contains(PartOfSpeech.Particle) == true
                    && ctx.NextText != null && TransitionRuleSets.ConditionalParticles.Contains(ctx.NextText),

                ScoringCondition.CandidateIsAdvTo =>
                    ctx.CandidatePOS.Contains(PartOfSpeech.AdverbTo),

                ScoringCondition.NextIsToParticle =>
                    (ctx.NextText == "と" && ctx.NextPOS?.Contains(PartOfSpeech.Particle) == true)
                    || ctx.NextText == "という",

                ScoringCondition.CandidateIsVerb =>
                    ctx.CandidatePOS.Contains(PartOfSpeech.Verb),

                ScoringCondition.NextIsTeFormAux =>
                    ctx.NextText != null && TransitionRuleSets.TeFormAuxiliaries.Contains(ctx.NextText),

                ScoringCondition.PrevIsNoParticle =>
                    ctx.PrevText == "の" && ctx.PrevPOS?.Contains(PartOfSpeech.Particle) == true,

                ScoringCondition.NextIsNotNaAdjConnector =>
                    ctx.NextText != null
                    && ctx.NextText is not ("な" or "に" or "で" or "の")
                    && !TransitionRuleSets.CopulaForms.Contains(ctx.NextText)
                    && ctx.NextPOS?.Contains(PartOfSpeech.Particle) != true
                    && ctx.NextPOS?.Any(p => p is PartOfSpeech.Suffix or PartOfSpeech.NounSuffix) != true,

                ScoringCondition.NextIsBaParticle =>
                    ctx.NextText == "ば" && ctx.NextPOS?.Contains(PartOfSpeech.Particle) == true,

                ScoringCondition.CandidateIsPrenounAdjectival =>
                    ctx.CandidatePOS.Contains(PartOfSpeech.PrenounAdjectival),

                ScoringCondition.NextIsNounLike =>
                    ctx.NextPOS?.Any(p => p is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                                            or PartOfSpeech.NaAdjective or PartOfSpeech.Pronoun
                                            or PartOfSpeech.Name or PartOfSpeech.NominalAdjective) == true,

                ScoringCondition.NextIsNotNounLike =>
                    ctx.NextPOS == null
                    || !ctx.NextPOS.Any(p => p is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                                               or PartOfSpeech.NaAdjective or PartOfSpeech.Pronoun
                                               or PartOfSpeech.Name or PartOfSpeech.NominalAdjective),

                ScoringCondition.CandidateIsConjunction =>
                    ctx.CandidatePOS.Contains(PartOfSpeech.Conjunction),

                ScoringCondition.IsSentenceInitial =>
                    ctx.PrevPOS == null && ctx.PrevText == null,

                ScoringCondition.CandidateIsInterjection =>
                    ctx.CandidatePOS.Contains(PartOfSpeech.Interjection)
                    && TransitionRuleSets.Interjections.Contains(ctx.CandidateText),

                ScoringCondition.CandidateIsSuruNoun =>
                    ctx.CandidateIsSuruNounVal,

                ScoringCondition.NextIsSuru =>
                    ctx.NextText != null && TransitionRuleSets.SuruForms.Contains(ctx.NextText),

                ScoringCondition.PrevIsCaseParticle =>
                    ctx.PrevPOS?.Contains(PartOfSpeech.Particle) == true
                    && ctx.PrevText != null && TransitionRuleSets.CaseMarkingParticles.Contains(ctx.PrevText),

                ScoringCondition.CandidateIsName =>
                    ctx.CandidatePOS.Contains(PartOfSpeech.Name),

                ScoringCondition.NextIsHonorific =>
                    ctx.NextText != null && TransitionRuleSets.HonorificSuffixes.Contains(ctx.NextText),

                ScoringCondition.IsSentenceFinal =>
                    ctx.NextPOS == null && ctx.NextText == null,

                ScoringCondition.CandidateIsNounSuffix =>
                    ctx.CandidatePOS.Any(p => p is PartOfSpeech.Suffix or PartOfSpeech.NounSuffix)
                    && TransitionRuleSets.NounSuffixes.Contains(ctx.CandidateText),

                ScoringCondition.PrevIsNounLike =>
                    ctx.PrevPOS?.Any(p => p is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                                            or PartOfSpeech.NaAdjective or PartOfSpeech.Pronoun
                                            or PartOfSpeech.Name or PartOfSpeech.NominalAdjective) == true,

                ScoringCondition.CandidateIsNotNounLike =>
                    !ctx.CandidatePOS.Any(p => p is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                                                   or PartOfSpeech.NaAdjective or PartOfSpeech.Pronoun
                                                   or PartOfSpeech.Name or PartOfSpeech.NominalAdjective),

                ScoringCondition.CandidateIsNotAdverb =>
                    !ctx.CandidatePOS.Any(p => p is PartOfSpeech.Adverb or PartOfSpeech.AdverbTo),

                ScoringCondition.CandidateIsHonorific =>
                    TransitionRuleSets.HonorificSuffixes.Contains(ctx.CandidateText) &&
                    ctx.CandidatePOS.Any(p => p is PartOfSpeech.Suffix or PartOfSpeech.NounSuffix),

                ScoringCondition.PrevIsName =>
                    ctx.PrevPOS?.Contains(PartOfSpeech.Name) == true,

                ScoringCondition.PrevIsAuxiliary =>
                    ctx.PrevPOS?.Contains(PartOfSpeech.Auxiliary) == true,

                ScoringCondition.PrevIsShikaParticle =>
                    ctx.PrevText == "しか" && ctx.PrevPOS?.Contains(PartOfSpeech.Particle) == true,

                ScoringCondition.NextIsObligationStart =>
                    ctx.NextText != null && TransitionRuleSets.ObligationStarts.Contains(ctx.NextText),

                ScoringCondition.CandidateIsCopulaForm =>
                    TransitionRuleSets.CopulaForms.Contains(ctx.CandidateText),

                ScoringCondition.PrevIsCopulaForm =>
                    ctx.PrevText != null && TransitionRuleSets.CopulaForms.Contains(ctx.PrevText),

                ScoringCondition.CandidateIsOPrefix =>
                    ctx.CandidatePOS.Contains(PartOfSpeech.Prefix) &&
                    TransitionRuleSets.OPrefixes.Contains(ctx.CandidateText),

                ScoringCondition.CandidateIsNegationKanjiPrefix =>
                    ctx.CandidatePOS.Contains(PartOfSpeech.Prefix) &&
                    TransitionRuleSets.NegationKanjiPrefixes.Contains(ctx.CandidateText),

                ScoringCondition.CandidateIsBuriSuffix =>
                    ctx.CandidateText == TransitionRuleSets.BuriSuffix &&
                    ctx.CandidatePOS.Any(p => p is PartOfSpeech.Suffix or PartOfSpeech.NounSuffix),

                ScoringCondition.CandidateIsToori =>
                    ctx.CandidateText == "通り",

                ScoringCondition.PrevIsCounter =>
                    ctx.PrevPOS?.Contains(PartOfSpeech.Counter) == true,

                ScoringCondition.CandidateIsOki =>
                    ctx.CandidateText is "おき" or "置き",

                ScoringCondition.NextIsOPrefixEligibleNoun =>
                    ctx.NextPOS?.Contains(PartOfSpeech.Noun) == true &&
                    ctx.NextText is { Length: > 0 } next &&
                    (HasKanji(next) || next.Length >= 4),

                ScoringCondition.NextIsIchiranCompoundNounParticle =>
                    ctx.NextPOS != null &&
                    (ctx.NextPOS.Contains(PartOfSpeech.Particle)
                     || ctx.NextPOS.Contains(PartOfSpeech.Auxiliary)
                     || ctx.NextPOS.Contains(PartOfSpeech.Expression)) &&
                    ctx.NextText != null &&
                    TransitionRuleSets.IchiranCompoundNounParticles.Contains(ctx.NextText),

                ScoringCondition.CandidateIsSubstantiveNoun =>
                    ctx.CandidatePOS.Any(IsNounLikePOS) &&
                    ctx.CandidateText is { Length: > 0 } t &&
                    (HasKanji(t) || t.Length >= 2),

                ScoringCondition.PrevIsSubstantiveNoun =>
                    ctx.PrevPOS?.Any(IsNounLikePOS) == true &&
                    ctx.PrevText is { Length: > 0 } pt &&
                    (HasKanji(pt) || pt.Length >= 2),

                ScoringCondition.CandidateIsShortKanaNotTo =>
                    IsShortKanaNotTo(ctx.CandidateText),

                ScoringCondition.PrevIsShortKanaNotTo =>
                    IsShortKanaNotTo(ctx.PrevText),

                ScoringCondition.NextIsShortKanaNotTo =>
                    IsShortKanaNotTo(ctx.NextText),

                ScoringCondition.CandidateIsTachiSuffix =>
                    (ctx.CandidateText == "たち" || ctx.CandidateText == "達")
                    && ctx.CandidatePOS.Any(p => p is PartOfSpeech.Suffix or PartOfSpeech.NounSuffix),

                ScoringCondition.CandidateIsChuSuffix =>
                    (ctx.CandidateText == "中" || ctx.CandidateText == "ちゅう")
                    && ctx.CandidatePOS.Any(p => p is PartOfSpeech.Suffix or PartOfSpeech.NounSuffix),

                ScoringCondition.CandidateIsSeiSuffix =>
                    ctx.CandidateText == "性"
                    && ctx.CandidatePOS.Any(p => p is PartOfSpeech.Suffix or PartOfSpeech.NounSuffix),

                ScoringCondition.CandidateIsSou =>
                    ctx.CandidateText == "そう",

                ScoringCondition.NextIsNanda =>
                    ctx.NextText == "なんだ",

                ScoringCondition.CandidateIsNoOrNnoParticle =>
                    (ctx.CandidateText == "の" || ctx.CandidateText == "ん")
                    && ctx.CandidatePOS.Contains(PartOfSpeech.Particle),

                ScoringCondition.NextIsDaDesuDaroo =>
                    ctx.NextText != null && TransitionRuleSets.NoDaCopulas.Contains(ctx.NextText),

                ScoringCondition.CandidateIsAdjNo =>
                    ctx.CandidateJmDictPos?.Contains("adj-no") == true,

                ScoringCondition.NextIsNoParticle =>
                    ctx.NextText == "の"
                    && ctx.NextPOS?.Contains(PartOfSpeech.Particle) == true,

                ScoringCondition.CandidateIsNaAdjForIchiran =>
                    ctx.CandidateJmDictPos?.Contains("adj-na") == true
                    || ctx.CandidatePOS.Contains(PartOfSpeech.NaAdjective),

                ScoringCondition.NextIsNaAdjConnector =>
                    ctx.NextText != null && TransitionRuleSets.NaAdjConnectors.Contains(ctx.NextText),

                ScoringCondition.NextIsToParticleExact =>
                    ctx.NextText == "と"
                    && ctx.NextPOS?.Contains(PartOfSpeech.Particle) == true,

                ScoringCondition.CandidateIsShikaParticle =>
                    ctx.CandidateText == "しか"
                    && ctx.CandidatePOS.Contains(PartOfSpeech.Particle),

                ScoringCondition.NextIsNegativeConjugation =>
                    HasNegativeTag(ctx.NextConjChain)
                    || (ctx.NextText != null && IsNegativeSurface(ctx.NextText)),

                ScoringCondition.PrevEndsWithHa =>
                    ctx.PrevText != null && ctx.PrevText.Length >= 1
                    && ctx.PrevText[^1] == 'は',

                ScoringCondition.NextIsShichaIkenai =>
                    ctx.NextText != null && TransitionRuleSets.ShichaIkenaiRightTexts.Contains(ctx.NextText),

                ScoringCondition.CandidateIsSemiFinalParticle =>
                    TransitionRuleSets.SemiFinalPrtSeqs.Contains(ctx.CandidateWordId)
                    || (Jiten.Parser.Resolution.Splits.CompoundSeqSets.TryGetValue(ctx.CandidateWordId, out var seqs)
                        && seqs.Overlaps(TransitionRuleSets.SemiFinalPrtSeqs)),

                ScoringCondition.NextExists =>
                    ctx.NextText != null || ctx.NextPOS != null,

                ScoringCondition.NextIsDaCopula =>
                    ctx.NextText == "だ"
                    && ctx.NextPOS?.Contains(PartOfSpeech.Auxiliary) == true,

                ScoringCondition.PairNotBothShortKanaNotTo =>
                    !(IsShortKanaNotTo(ctx.CandidateText) && IsShortKanaNotTo(ctx.NextText)),

                ScoringCondition.CandidateIsSubstantiveNounKanjiOrLong =>
                    ctx.CandidatePOS.Any(IsNounLikePOS) &&
                    ctx.CandidateText is { Length: > 0 } tk &&
                    (HasKanji(tk) || tk.Length >= 3),

                _ => false
            };
            if (!ok) return false;
        }

        return true;
    }
}
