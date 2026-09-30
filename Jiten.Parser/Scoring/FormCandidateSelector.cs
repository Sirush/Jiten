using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Diagnostics;

namespace Jiten.Parser.Scoring;

internal static class FormCandidateSelector
{
    public static FormCandidate? PickBestCandidate(
        List<FormCandidate> allCandidates,
        FormScoringContext context,
        IReadOnlySet<string> archaicPosTypes,
        ParserDiagnostics? diagnostics = null)
    {
        return PickTopCandidates(allCandidates, context, archaicPosTypes, diagnostics).Best;
    }

    public static CandidateSelectionResult PickTopCandidates(
        List<FormCandidate> allCandidates,
        FormScoringContext context,
        IReadOnlySet<string> archaicPosTypes,
        ParserDiagnostics? diagnostics = null)
    {
        if (context.IsKanaSurface)
            allCandidates.RemoveAll(c =>
                KanaScoringHelpers.IsKanaSurfaceWithNoMatchingReading(context, c.Word, c.Form.Text));

        // A katakana surface with a script-exact non-name candidate must not fold to kanji/hiragana-only words (フル, not 降る).
        if (KanaScoringHelpers.IsPureKatakanaToken(context.Surface)
            && allCandidates.Any(c => c.Form.Text == context.Surface
                && c.Word.CachedPOS.Any(p => p is not (PartOfSpeech.Name or PartOfSpeech.Unknown))))
        {
            allCandidates.RemoveAll(c =>
                c.Form.Text != context.Surface
                && !c.Word.Forms.Any(f => KanaScoringHelpers.ContainsKatakana(f.Text)));
        }

        if (allCandidates.Count == 0)
            return new CandidateSelectionResult(null, null);

        // Applies the -15 POS-incompatible penalty (noun 1197950 vs adj-na 2653620 when Sudachi says NaAdjective).
        static int EffectiveScore(FormCandidate c) => ScoringPolicy.EffectiveScore(c);

        FormCandidate? best = null;
        int bestScore = int.MinValue;

        foreach (var candidate in allCandidates)
        {
            var trace = FormCandidateScorer.Score(candidate, context, archaicPosTypes);
            candidate.SetScoreTrace(trace);

            int score = EffectiveScore(candidate);
            if (score > bestScore ||
                (score == bestScore && best != null && !candidate.IsPosIncompatibleDirectSurface && best.IsPosIncompatibleDirectSurface) ||
                (score == bestScore && best != null && candidate.IsPosIncompatibleDirectSurface == best.IsPosIncompatibleDirectSurface
                                   && candidate.Word.WordId < best.Word.WordId) ||
                (score == bestScore && best != null && candidate.Word.WordId == best.Word.WordId
                                   && HasPreferredConjugation(candidate, best)))
            {
                bestScore = score;
                best = candidate;
            }
        }

        best = RefineBest(best, allCandidates, context);

        // POS-incompatible runners-up are skipped (unless all are) so their -15 penalty can't fake a low margin.
        int? margin = null;
        if (best != null)
        {
            bool hasLegitimate = false;
            int bestLegitimateScore = int.MinValue;
            int bestAnyScore = int.MinValue;

            foreach (var c in allCandidates)
            {
                if (c.Word.WordId == best.Word.WordId) continue;
                int s = EffectiveScore(c);
                if (!c.IsPosIncompatibleDirectSurface)
                {
                    hasLegitimate = true;
                    if (s > bestLegitimateScore) bestLegitimateScore = s;
                }
                if (s > bestAnyScore) bestAnyScore = s;
            }

            int secondBest = hasLegitimate ? bestLegitimateScore : bestAnyScore;
            if (secondBest > int.MinValue)
                margin = EffectiveScore(best) - secondBest;
        }

        if (diagnostics != null && best != null)
        {
            var sorted = allCandidates.OrderByDescending(EffectiveScore).ToList();
            var topCandidates = sorted
                                .Take(10)
                                .Select(c =>
                                {
                                    var diag = new FormCandidateDiagnostic
                                    {
                                        WordId = c.Word.WordId,
                                        FormText = c.Form.Text,
                                        ReadingIndex = c.ReadingIndex,
                                        IsSelected = ReferenceEquals(c, best),
                                        TotalScore = c.TotalScore,
                                        WordScore = c.WordScore,
                                        EntryPriorityScore = c.EntryPriorityScore,
                                        FormPriorityScore = c.FormPriorityScore,
                                        FormFlagScore = c.FormFlagScore,
                                        SurfaceMatchScore = c.SurfaceMatchScore,
                                        ScriptScore = c.ScriptScore,
                                        ReadingMatchScore = c.ReadingMatchScore,
                                        PosAffinityScore = c.PosAffinityScore,
                                        RubyPriorsScore = c.RubyPriorsScore
                                    };
                                    if (c.RubyPriorsScore != 0)
                                    {
                                        var detail = RubyPriorsScorer.ScoreDetailed(c, context);
                                        diag.RubyPriorSupport = detail.Support;
                                        diag.RubyPriorLevel = detail.Level;
                                    }
                                    return diag;
                                })
                                .ToList();

            diagnostics.Results.Add(new WordResult
                                    {
                                        Text = context.Surface,
                                        DictionaryForm = context.DictionaryForm,
                                        Reading = context.SudachiReading,
                                        WordId = best.Word.WordId,
                                        ReadingIndex = best.ReadingIndex,
                                        Candidates = topCandidates,
                                        MarginToSecond = margin
                                    });
        }

        return new CandidateSelectionResult(best, margin);
    }

    /// <summary>Order matters: each pass sees the previous pass's pick, and null keeps it.</summary>
    public static FormCandidate? RefineBest(
        FormCandidate? best, List<FormCandidate> allCandidates, FormScoringContext context)
    {
        if (best == null) return null;
        best = WordFrequencyPriors.Apply(best, allCandidates, context) ?? best;
        best = ApplyKanjiHomographPriorityCap(best, allCandidates, context) ?? best;
        best = ApplyArchaicExactSurfaceRescue(best, allCandidates, context) ?? best;
        best = ApplySentenceFinalInterjection(best, allCandidates, context) ?? best;
        best = ApplyJitenMinorKanaReadingCap(best, allCandidates, context) ?? best;
        best = ApplyContractedCopulaExactSurface(best, allCandidates, context) ?? best;
        best = ApplyKanaSurfaceKanjiFormRemap(best, allCandidates, context) ?? best;
        return best;
    }

    /// <summary>A kana surface reached via a kanji NormalizedForm (ちょっとぉ → 一寸) gets its primary kana form.</summary>
    public static FormCandidate? ApplyKanaSurfaceKanjiFormRemap(
        FormCandidate best, List<FormCandidate> allCandidates, FormScoringContext context)
    {
        if (!context.IsKanaSurface) return null;
        if (best.Form.FormType != JmDictFormType.KanjiForm) return null;

        foreach (var c in allCandidates)
        {
            if (c.Word.WordId == best.Word.WordId && c.Form.FormType == JmDictFormType.KanaForm)
                return null;
        }

        JmDictWordForm? kanaForm = null;
        foreach (var f in best.Word.Forms)
        {
            if (f.FormType != JmDictFormType.KanaForm || f.ReadingIndex > 255) continue;
            if (f.IsSearchOnly || f.IsObsolete) continue;
            if (kanaForm == null || f.ReadingIndex < kanaForm.ReadingIndex) kanaForm = f;
        }

        if (kanaForm == null) return null;

        var remapped = new FormCandidate(best.Word, kanaForm, (byte)kanaForm.ReadingIndex, best.TargetHiragana,
                                         best.DeconjForm);
        remapped.SetScoreTrace(best.ScoreTrace);
        return remapped;
    }

    /// <summary>Contracted copula じゃ reached via だ (2089020) prefers a direct-surface copula entry (じゃ 2851029).</summary>
    public static FormCandidate? ApplyContractedCopulaExactSurface(
        FormCandidate best, List<FormCandidate> allCandidates, FormScoringContext context)
    {
        if (!context.IsKanaSurface) return null;
        if (context.Surface == best.Form.Text) return null;
        if (!best.Word.PartsOfSpeech.Any(p => p is "cop")) return null;

        foreach (var c in allCandidates)
        {
            if (c.Word.WordId == best.Word.WordId) continue;
            if (context.Surface != c.Form.Text) continue;
            if (c.DeconjForm?.Process is { Length: > 0 }) continue;
            if (c.Word.PartsOfSpeech.Any(p => p is "cop"))
                return c;
        }

        return null;
    }

    /// <summary>The jiten boost is a kanji word's weight; on its minor kana reading (遂 つい) a frequent kana entry wins.</summary>
    public static FormCandidate? ApplyJitenMinorKanaReadingCap(
        FormCandidate best, List<FormCandidate> allCandidates, FormScoringContext context)
    {
        if (!context.IsKanaSurface) return null;
        if (best.DeconjForm?.Process is { Length: > 0 } || context.Surface != best.Form.Text) return null;
        if (best.Word.Priorities?.Contains("jiten") != true) return null;
        if (best.Form.FormType != JmDictFormType.KanaForm) return null;
        if (best.Word.PartsOfSpeech.Any(p => p is "uk")) return null;
        if (!best.Word.Forms.Any(f => f.FormType == JmDictFormType.KanjiForm)) return null;

        foreach (var c in allCandidates)
        {
            if (c.Word.WordId == best.Word.WordId) continue;
            if (c.DeconjForm?.Process is { Length: > 0 } || context.Surface != c.Form.Text) continue;
            if (c.Word.Forms.Any(f => f.FormType == JmDictFormType.KanjiForm)) continue;
            if (c.Word.CachedPOS.Contains(PartOfSpeech.Name)) continue;
            if (!KanaScoringHelpers.HasFrequencyMarker(c.Word.Priorities, includeJiten: false)) continue;
            return c;
        }

        return null;
    }

    /// <summary>Sentence-final 来い is the interjection, not 来る's imperative; mixed entries (出来た exp/int) don't qualify.</summary>
    public static FormCandidate? ApplySentenceFinalInterjection(
        FormCandidate best, List<FormCandidate> allCandidates, FormScoringContext context)
    {
        if (!context.IsSentenceFinal)
            return null;
        if (best.DeconjForm?.Process is not { Length: > 0 }
            || !KanaScoringHelpers.IsInflectableVerbOrAdj(best.Word.PartsOfSpeech))
            return null;

        foreach (var candidate in allCandidates)
        {
            if (candidate.Word.WordId == best.Word.WordId) continue;
            if (candidate.DeconjForm?.Process is { Length: > 0 }) continue;
            if (context.Surface != candidate.Form.Text) continue;
            if (candidate.Word.CachedPOS.Any(p => p is not (PartOfSpeech.Interjection or PartOfSpeech.Unknown)))
                continue;
            if (IsUnattestedInterjectionOverPastForm(candidate.Word, best.DeconjForm.Process))
                continue;
            if (candidate.Word.PartsOfSpeech.Any(p => p is "int"))
                return candidate;
        }

        return null;
    }

    private static readonly string[] PublicFrequencyMarkers =
        ["ichi1", "ichi2", "news1", "news2", "spec1", "spec2", "gai1", "gai2"];

    /// <summary>A past form states a proposition, so an int homograph needs frequency evidence (やった spec1 yes, 来た no).</summary>
    public static bool IsUnattestedInterjectionOverPastForm(JmDictWord word, IReadOnlyList<string> deconjProcess)
    {
        if (!deconjProcess.Contains("past")) return false;
        if (word.CachedPOS.Any(p => p is not (PartOfSpeech.Interjection or PartOfSpeech.Unknown))) return false;
        if (!word.PartsOfSpeech.Any(p => p is "int")) return false;

        return word.Priorities == null
               || !word.Priorities.Any(p => PublicFrequencyMarkers.Contains(p)
                                            || p.StartsWith("nf", StringComparison.Ordinal));
    }

    /// <summary>Rescues an exact-surface archaic word (あらざる) from its -350 only when all else is junk (聞ける stays 聞く).</summary>
    public static FormCandidate? ApplyArchaicExactSurfaceRescue(
        FormCandidate best, List<FormCandidate> allCandidates, FormScoringContext context)
    {
        if (best.Word.IsFullyArchaic || ScoringPolicy.EffectiveScore(best) >= 100
            || context.Surface.Length < 3)
            return null;

        FormCandidate? archExact = null;
        foreach (var c in allCandidates)
        {
            if (c.Word.IsFullyArchaic && c.SurfaceMatchScore >= 300
                && (archExact == null || c.TotalScore > archExact.TotalScore))
                archExact = c;
        }

        return archExact != null && archExact.TotalScore + 300 > ScoringPolicy.EffectiveScore(best)
            ? archExact
            : null;
    }

    /// <summary>Candidates must already be scored via FormCandidateScorer.Score.</summary>
    public static FormCandidate? PickTopCandidatesWithBonus(
        List<FormCandidate> allCandidates,
        Func<FormCandidate, int> bonusFunc)
    {
        if (allCandidates.Count == 0) return null;

        FormCandidate? best = null;
        int bestAdjusted = int.MinValue;

        foreach (var candidate in allCandidates)
        {
            int bonus = bonusFunc(candidate);
            if (candidate.IsPosIncompatibleDirectSurface && bonus > 0)
                bonus = 0;
            int adjusted = ScoringPolicy.EffectiveScore(candidate) + bonus;
            if (adjusted > bestAdjusted ||
                (adjusted == bestAdjusted && best != null && candidate.Word.WordId < best.Word.WordId) ||
                (adjusted == bestAdjusted && best != null && candidate.Word.WordId == best.Word.WordId
                                           && HasPreferredConjugation(candidate, best)))
            {
                bestAdjusted = adjusted;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>An unprioritized pick that won only via Sudachi's +70 reading yields to a prioritized rival (歩兵 ほへい).</summary>
    internal static FormCandidate? ApplyKanjiHomographPriorityCap(
        FormCandidate best,
        List<FormCandidate> allCandidates,
        FormScoringContext context)
    {
        if (context.IsKanaSurface) return null;
        if (best.ReadingMatchScore <= 0) return null;
        if (best.EntryPriorityScore > 0) return null;
        if (context.Surface != best.Form.Text) return null;
        if (best.DeconjForm?.Process is { Length: > 0 }) return null;

        int bestEffective = ScoringPolicy.EffectiveScore(best);

        FormCandidate? rival = null;
        int rivalEffective = int.MinValue;
        foreach (var c in allCandidates)
        {
            if (c.Word.WordId == best.Word.WordId) continue;
            if (c.Form.Text != context.Surface) continue;
            if (c.EntryPriorityScore <= 0) continue;
            if (c.ReadingMatchScore > 0) continue;
            if (c.DeconjForm?.Process is { Length: > 0 }) continue;
            if (c.IsPosIncompatibleDirectSurface && !best.IsPosIncompatibleDirectSurface) continue;
            // JMDict priorities often land on the wrong homograph (里 り has ichi1), so ruby priors backing Sudachi veto the flip.
            if (c.RubyPriorsScore < best.RubyPriorsScore) continue;

            int effective = ScoringPolicy.EffectiveScore(c);
            if (effective + best.ReadingMatchScore >= bestEffective && effective > rivalEffective)
            {
                rival = c;
                rivalEffective = effective;
            }
        }

        return rival;
    }

    private static bool HasPreferredConjugation(FormCandidate candidate, FormCandidate current)
    {
        var candidateProcess = candidate.DeconjForm?.Process;
        var currentProcess = current.DeconjForm?.Process;

        if (candidateProcess is not { Length: > 0 } || currentProcess is not { Length: > 0 })
            return false;

        bool candidateIsInfinitive = candidateProcess[^1] is "(infinitive)" or "(unstressed infinitive)";
        bool currentIsInfinitive = currentProcess[^1] is "(infinitive)" or "(unstressed infinitive)";

        return candidateIsInfinitive && !currentIsInfinitive;
    }
}
