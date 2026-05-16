using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser;
using Jiten.Parser.Diagnostics;
using Jiten.Parser.Scoring;

namespace Jiten.Parser.Grammar;

internal static class TransitionRuleEngine
{
    // Two-pass approach
    //   Pass 1 — while-loop strips all leading verb-attaching auxiliaries
    //   Pass 2 — backwards loop validates aux context and counter placement
    internal static void ApplyHardRules(
        List<(WordInfo word, int pos, int len)> words,
        Func<string, bool> hasLookup,
        ParserDiagnostics? diagnostics = null)
    {
        var rules = TransitionRuleSets.HardRules;

        // Pass 1: strip all sentence-initial tokens that can never begin a clause (needs while loop:
        // removing index 0 exposes a new index 0 that also needs to be checked)
        var leadingStripRules = Array.FindAll(rules,
            r => r.Id is "leading-aux-strip" or "particle-at-sentence-start");
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

                MatchCondition.NextIsNotQuotative =>
                    w.Next == null || !w.Next.Text.StartsWith("という"),

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
                else if (prevWord.PartOfSpeech == PartOfSpeech.Noun &&
                         hasLookup(prevWord.Text + "る"))
                {
                    var auxLen = words[i].len;
                    var verbDictForm = prevWord.Text + "る";
                    words[i - 1] = (new WordInfo(prevWord)
                    {
                        Text = merged,
                        DictionaryForm = verbDictForm,
                        NormalizedForm = verbDictForm,
                        PartOfSpeech = PartOfSpeech.Verb
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
        text is { Length: 1 } && JapaneseTextHelper.IsKanji(text[0]);

    private static TokenWindow BuildWindow(List<(WordInfo word, int pos, int len)> words, int i)
    {
        var prev = i > 0 ? words[i - 1].word : null;
        var next = i + 1 < words.Count ? words[i + 1].word : null;
        return new TokenWindow(prev, words[i].word, next, i, words.Count);
    }

    internal static int EvaluateSoftRules(ScoringWindow window, List<string>? rulesMatched = null)
    {
        int bonus = 0;
        var ctx = ConditionContext.FromScoringWindow(window);

        foreach (var rule in TransitionRuleSets.SoftRules)
        {
            if (rule.RequiredCandidateMask != 0 && !PosMask.Has(ctx.CandidateMask, rule.RequiredCandidateMask))
                continue;
            if (!MatchesAll(ctx, rule.CandidateMatch)) continue;
            if (!MatchesAll(ctx, rule.ContextMatch)) continue;

            bonus += rule.Delta;
            rulesMatched?.Add(rule.Id);
        }

        return bonus;
    }

    internal static bool HasApplicableSoftRules(ScoringWindow window)
    {
        var ctx = ConditionContext.FromScoringWindow(window);
        foreach (var rule in TransitionRuleSets.SoftRules)
        {
            if (rule.RequiredCandidateMask != 0 && !PosMask.Has(ctx.CandidateMask, rule.RequiredCandidateMask))
                continue;
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

        var ctx = new ConditionContext(
            PosMask.FromList(currentPOS), currentText,
            prevPOS != null ? PosMask.FromList(prevPOS) : 0, prevPOS != null, prevText,
            nextPOS != null ? PosMask.FromList(nextPOS) : 0, nextPOS != null, nextText);
        foreach (var rule in TransitionRuleSets.SoftRules)
        {
            if (rule.RequiredCandidateMask != 0 && !PosMask.Has(ctx.CandidateMask, rule.RequiredCandidateMask))
                continue;
            if (!MatchesAll(ctx, rule.CandidateMatch)) continue;
            if (MatchesAll(ctx, rule.ContextMatch)) return true;
        }

        return false;
    }

    private readonly record struct ConditionContext(
        uint CandidateMask,
        string CandidateText,
        uint PrevMask,
        bool HasPrev,
        string? PrevText,
        uint NextMask,
        bool HasNext,
        string? NextText,
        bool CandidateIsSuruNounVal = false,
        bool CandidateHasHonorificRegister = false)
    {
        public static ConditionContext FromScoringWindow(ScoringWindow w) => new(
            w.Candidate.Word.CachedPOSMask,
            w.Candidate.Form.Text,
            w.PrevMask, w.HasPrev, w.PrevText,
            w.NextMask, w.HasNext, w.NextText,
            w.Candidate.Word.IsSuruVerb,
            TransitionRuleSets.HonorificSuffixes.Contains(w.Candidate.Form.Text) &&
            PosMask.Has(w.Candidate.Word.CachedPOSMask, PosMask.SuffixGroup) &&
            w.Candidate.Word.PartsOfSpeech.Exists(TransitionRuleSets.HonorificRegisterTags.Contains));
    }

    private static bool MatchesAll(ConditionContext ctx, ScoringCondition[] conditions)
    {
        foreach (var c in conditions)
        {
            bool ok = c switch
            {
                ScoringCondition.CandidateIsNounLike =>
                    PosMask.Has(ctx.CandidateMask, PosMask.NounLike),

                ScoringCondition.CandidateIsNaAdj =>
                    PosMask.Has(ctx.CandidateMask, PosMask.NaAdjective),

                ScoringCondition.CandidateIsAdverb =>
                    PosMask.Has(ctx.CandidateMask, PosMask.AdverbGroup),

                ScoringCondition.CandidateIsAuxiliary =>
                    PosMask.Has(ctx.CandidateMask, PosMask.Auxiliary),

                ScoringCondition.CandidateIsParticle =>
                    PosMask.Has(ctx.CandidateMask, PosMask.Particle),

                ScoringCondition.CandidateIsSingleKanaNonParticle =>
                    ctx.CandidateText.Length <= 1 && !PosMask.Has(ctx.CandidateMask, PosMask.Particle),

                ScoringCondition.NextIsCommonParticle =>
                    PosMask.Has(ctx.NextMask, PosMask.Particle)
                    && ctx.NextText != null && TransitionRuleSets.CommonParticles.Contains(ctx.NextText),

                ScoringCondition.NextIsCopula =>
                    ctx.NextText != null && TransitionRuleSets.CopulaForms.Contains(ctx.NextText),

                ScoringCondition.NextIsNaConnector =>
                    ctx.NextText is "な" or "に",

                ScoringCondition.NextIsVerbOrIAdj =>
                    ctx.HasNext && PosMask.Has(ctx.NextMask, PosMask.VerbOrIAdj),

                ScoringCondition.PrevIsVerbOrIAdj =>
                    ctx.HasPrev && PosMask.Has(ctx.PrevMask, PosMask.VerbOrIAdj),

                ScoringCondition.PrevIsParticle =>
                    ctx.HasPrev && PosMask.Has(ctx.PrevMask, PosMask.Particle),

                ScoringCondition.NextIsParticle =>
                    ctx.HasNext && PosMask.Has(ctx.NextMask, PosMask.Particle),

                ScoringCondition.PrevIsSingleKanaNonParticle =>
                    ctx.PrevText is { Length: 1 } && !PosMask.Has(ctx.PrevMask, PosMask.Particle),

                ScoringCondition.NextIsSingleKanaNonParticle =>
                    ctx.NextText is { Length: 1 } && !PosMask.Has(ctx.NextMask, PosMask.Particle),

                ScoringCondition.CandidateIsPredicateHost =>
                    PosMask.Has(ctx.CandidateMask, PosMask.PredicateHost),

                ScoringCondition.CandidateIsNoParticle =>
                    ctx.CandidateText == "の" && PosMask.Has(ctx.CandidateMask, PosMask.Particle),

                ScoringCondition.NextIsExplanatoryN =>
                    ctx.NextText != null
                    && TransitionRuleSets.ExplanatoryNForms.Contains(ctx.NextText),

                ScoringCondition.PrevIsVerbAuxOrIAdj =>
                    ctx.HasPrev && PosMask.Has(ctx.PrevMask, PosMask.VerbAuxOrIAdj),

                ScoringCondition.CandidateIsCounter =>
                    PosMask.Has(ctx.CandidateMask, PosMask.Counter),

                ScoringCondition.PrevIsNumeral =>
                    ctx.HasPrev && PosMask.Has(ctx.PrevMask, PosMask.Numeral),

                ScoringCondition.PrevIsNotNumericLike =>
                    !ctx.HasPrev || !PosMask.Has(ctx.PrevMask, PosMask.NumericLike),

                ScoringCondition.CandidateIsSingleKanji =>
                    IsSingleKanji(ctx.CandidateText),

                ScoringCondition.PrevIsSingleKanji =>
                    IsSingleKanji(ctx.PrevText),

                ScoringCondition.NextIsSingleKanji =>
                    IsSingleKanji(ctx.NextText),

                ScoringCondition.NextIsConditionalParticle =>
                    PosMask.Has(ctx.NextMask, PosMask.Particle)
                    && ctx.NextText != null && TransitionRuleSets.ConditionalParticles.Contains(ctx.NextText),

                ScoringCondition.CandidateIsAdvTo =>
                    PosMask.Has(ctx.CandidateMask, PosMask.AdverbTo),

                ScoringCondition.NextIsToParticle =>
                    (ctx.NextText == "と" && PosMask.Has(ctx.NextMask, PosMask.Particle))
                    || ctx.NextText == "という",

                ScoringCondition.CandidateIsVerb =>
                    PosMask.Has(ctx.CandidateMask, PosMask.Verb),

                ScoringCondition.NextIsTeFormAux =>
                    ctx.NextText != null && TransitionRuleSets.TeFormAuxiliaries.Contains(ctx.NextText),

                ScoringCondition.PrevIsNoParticle =>
                    ctx.PrevText == "の" && PosMask.Has(ctx.PrevMask, PosMask.Particle),

                ScoringCondition.NextIsNotNaAdjConnector =>
                    ctx.NextText != null
                    && ctx.NextText is not ("な" or "に" or "で" or "の")
                    && !TransitionRuleSets.CopulaForms.Contains(ctx.NextText)
                    && !PosMask.Has(ctx.NextMask, PosMask.Particle)
                    && !PosMask.Has(ctx.NextMask, PosMask.SuffixGroup),

                ScoringCondition.NextIsBaParticle =>
                    ctx.NextText == "ば" && PosMask.Has(ctx.NextMask, PosMask.Particle),

                ScoringCondition.CandidateIsPrenounAdjectival =>
                    PosMask.Has(ctx.CandidateMask, PosMask.PrenounAdjectival),

                ScoringCondition.NextIsNounLike =>
                    ctx.HasNext && PosMask.Has(ctx.NextMask, PosMask.NounLike),

                ScoringCondition.NextIsNotNounLike =>
                    !ctx.HasNext || !PosMask.Has(ctx.NextMask, PosMask.NounLike),

                ScoringCondition.CandidateIsConjunction =>
                    PosMask.Has(ctx.CandidateMask, PosMask.Conjunction),

                ScoringCondition.IsSentenceInitial =>
                    !ctx.HasPrev && ctx.PrevText == null,

                ScoringCondition.CandidateIsInterjection =>
                    PosMask.Has(ctx.CandidateMask, PosMask.Interjection)
                    && TransitionRuleSets.Interjections.Contains(ctx.CandidateText),

                ScoringCondition.CandidateIsSuruNoun =>
                    ctx.CandidateIsSuruNounVal,

                ScoringCondition.NextIsSuru =>
                    ctx.NextText != null && TransitionRuleSets.SuruForms.Contains(ctx.NextText),

                ScoringCondition.PrevIsCaseParticle =>
                    PosMask.Has(ctx.PrevMask, PosMask.Particle)
                    && ctx.PrevText != null && TransitionRuleSets.CaseMarkingParticles.Contains(ctx.PrevText),

                ScoringCondition.CandidateIsName =>
                    PosMask.Has(ctx.CandidateMask, PosMask.NameBit),

                ScoringCondition.NextIsHonorific =>
                    ctx.NextText != null && TransitionRuleSets.HonorificSuffixes.Contains(ctx.NextText),

                ScoringCondition.IsSentenceFinal =>
                    !ctx.HasNext && ctx.NextText == null,

                ScoringCondition.CandidateIsNounSuffix =>
                    PosMask.Has(ctx.CandidateMask, PosMask.SuffixGroup)
                    && TransitionRuleSets.NounSuffixes.Contains(ctx.CandidateText),

                ScoringCondition.PrevIsNounLike =>
                    ctx.HasPrev && PosMask.Has(ctx.PrevMask, PosMask.NounLike),

                ScoringCondition.CandidateIsNotNounLike =>
                    !PosMask.Has(ctx.CandidateMask, PosMask.NounLike),

                ScoringCondition.CandidateIsNotAdverb =>
                    !PosMask.Has(ctx.CandidateMask, PosMask.AdverbGroup),

                ScoringCondition.CandidateIsHonorific =>
                    TransitionRuleSets.HonorificSuffixes.Contains(ctx.CandidateText) &&
                    PosMask.Has(ctx.CandidateMask, PosMask.SuffixGroup) &&
                    ctx.CandidateHasHonorificRegister,

                ScoringCondition.CandidateIsNotHonorific =>
                    !TransitionRuleSets.HonorificSuffixes.Contains(ctx.CandidateText),

                ScoringCondition.PrevIsName =>
                    ctx.HasPrev && PosMask.Has(ctx.PrevMask, PosMask.NameBit),

                ScoringCondition.PrevIsAuxiliary =>
                    ctx.HasPrev && PosMask.Has(ctx.PrevMask, PosMask.Auxiliary),

                ScoringCondition.NextIsNaAdj =>
                    ctx.HasNext && PosMask.Has(ctx.NextMask, PosMask.NaAdjective),

                ScoringCondition.PrevIsToParticle =>
                    ctx.HasPrev && ctx.PrevText == "と"
                    && PosMask.Has(ctx.PrevMask, PosMask.Particle),

                _ => false
            };
            if (!ok) return false;
        }

        return true;
    }
}
