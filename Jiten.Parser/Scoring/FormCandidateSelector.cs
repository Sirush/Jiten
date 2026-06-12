using Jiten.Core.Data;
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

        // A pure-katakana surface with a script-exact non-name
        // candidate must not fall through to hiragana-fold matches of words attested only in
        // kanji/hiragana (フル must stay フル, not 降る — even in rederivation pools).
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

        // POS-incompatible direct-surface candidates (e.g. noun 1197950 "artistry" competing with
        // adj-na 2653620 "serious" when Sudachi tags the token as NaAdjective) get a -15 penalty
        // so POS-compatible matches win the close races they should win.
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

        // Compute margin via linear scan for the second-best alternate (different WordId).
        // Filter out POS-incompatible runners-up so their -15 penalty doesn't produce a false low margin.
        // Fallback: if ALL candidates are POS-incompatible, use the full pool.
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

    /// Applies the post-selection refinement passes in order (frequency priors → kanji-homograph
    /// priority cap → archaic exact-surface rescue). Each pass returns null when it does not
    /// apply, so the previous best is kept. No-op when best is null.
    public static FormCandidate? RefineBest(
        FormCandidate? best, List<FormCandidate> allCandidates, FormScoringContext context)
    {
        if (best == null) return null;
        best = WordFrequencyPriors.Apply(best, allCandidates, context) ?? best;
        best = ApplyKanjiHomographPriorityCap(best, allCandidates, context) ?? best;
        best = ApplyArchaicExactSurfaceRescue(best, allCandidates, context) ?? best;
        return best;
    }

    /// An archaic word written exactly as its surface (人にあらざる) is self-evidently
    /// intended — but its −350 penalty buries it below junk fallbacks. Rescue it only when
    /// nothing else scores above the junk band, so real alternatives (聞ける → potential of
    /// 聞く) keep winning. Returns the rescued candidate or null when no rescue applies.
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

    /// Selects the best candidate using pre-scored candidates + a per-candidate bonus function.
    /// Candidates must already have scores set via FormCandidateScorer.Score before calling this.
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

    /// Sudachi's lexeme reading earns ReadingMatchScore (+70) for whichever homograph entry the
    /// lattice happened to carry. When that entry has no JMDict priority while a same-surface
    /// rival is ichi/news-prioritized and lost ONLY because of the reading bonus, prefer the
    /// prioritized entry — 歩兵 must be ほへい (news1), not the shogi pawn ふひょう, just because
    /// Sudachi's lexicon says フヒョウ; 間中 must be "during" (news1), not まなか "half a ken".
    /// Kana surfaces are owned by WordFrequencyPriors; both-prioritized homographs (一日, 方)
    /// stay with the reading evidence.
    internal static FormCandidate? ApplyKanjiHomographPriorityCap(
        FormCandidate best,
        List<FormCandidate> allCandidates,
        FormScoringContext context)
    {
        if (context.IsKanaSurface) return null;
        if (best.ReadingMatchScore <= 0) return null;
        if (best.EntryPriorityScore > 0) return null;
        if (context.Surface != best.Form.Text) return null;
        if (best.DeconjForm?.Process is { Count: > 0 }) return null;

        int bestEffective = ScoringPolicy.EffectiveScore(best);

        FormCandidate? rival = null;
        int rivalEffective = int.MinValue;
        foreach (var c in allCandidates)
        {
            if (c.Word.WordId == best.Word.WordId) continue;
            if (c.Form.Text != context.Surface) continue;
            if (c.EntryPriorityScore <= 0) continue;
            if (c.ReadingMatchScore > 0) continue;
            if (c.DeconjForm?.Process is { Count: > 0 }) continue;
            if (c.IsPosIncompatibleDirectSurface && !best.IsPosIncompatibleDirectSurface) continue;
            // JMDict entry priorities are form-level and often land on the wrong homograph
            // (里(り) carries ichi1, 汝(うぬ) carries news2). Only flip when the furigana corpus
            // doesn't side with Sudachi's choice (里=さと is heavily glossed; ほへい vs ふひょう isn't).
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

        if (candidateProcess is not { Count: > 0 } || currentProcess is not { Count: > 0 })
            return false;

        bool candidateIsInfinitive = candidateProcess[^1] is "(infinitive)" or "(unstressed infinitive)";
        bool currentIsInfinitive = currentProcess[^1] is "(infinitive)" or "(unstressed infinitive)";

        return candidateIsInfinitive && !currentIsInfinitive;
    }
}
