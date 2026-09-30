using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;

namespace Jiten.Parser.Scoring;

/// <summary>A gate, not a reranker: deck-derived frequencies flip a kana homograph only at ≥10× (カイロ: 懐炉 over Cairo).</summary>
internal static class WordFrequencyPriors
{
    public static Dictionary<int, double>? Current { get; set; }

    private const double MinRatio = 10.0;
    private const double MinChallengerFrequency = 1e-6;
    private const int MaxScoreGap = 20;

    public static FormCandidate? Apply(
        FormCandidate best,
        List<FormCandidate> allCandidates,
        FormScoringContext context)
    {
        var frequencies = Current;
        if (frequencies == null)
            return null;

        // Kanji homographs (主 ぬし/しゅ) are reading ambiguities owned by the ruby priors, not word frequency.
        if (!context.IsKanaSurface)
            return null;

        // Conjugated or fold-matched picks carry morphological evidence a word-level prior can't outweigh.
        if (context.Surface != best.Form.Text || best.DeconjForm?.Process is { Length: > 0 })
            return null;

        // An unobserved best is often a rare but correct mimetic, not evidence of a misparse.
        if (!frequencies.TryGetValue(best.Word.WordId, out double bestFrequency))
            return null;

        if (KanaScoringHelpers.IsInflectableVerbOrAdj(best.Word.PartsOfSpeech))
            return null;

        int bestEffective = ScoringPolicy.EffectiveScore(best);

        FormCandidate? challenger = null;
        double challengerFrequency = 0;

        foreach (var candidate in allCandidates)
        {
            if (candidate.Word.WordId == best.Word.WordId) continue;
            if (context.Surface != candidate.Form.Text) continue;
            if (candidate.DeconjForm?.Process is { Length: > 0 }) continue;
            if (candidate.IsPosIncompatibleDirectSurface && !best.IsPosIncompatibleDirectSurface) continue;

            if (!frequencies.TryGetValue(candidate.Word.WordId, out double frequency)) continue;
            if (frequency < MinChallengerFrequency || frequency < bestFrequency * MinRatio) continue;

            // A clear scorer preference stands (封 must not steal ふう from 風 across a 27-point gap).
            if (bestEffective - ScoringPolicy.EffectiveScore(candidate) > MaxScoreGap) continue;

            if (KanaScoringHelpers.IsInflectableVerbOrAdj(candidate.Word.PartsOfSpeech)) continue;

            // Bound morphemes (がち, counters, particles) owe their frequency to attached usage, not standalone tokens.
            if (HasBoundMorphemeTag(candidate.Word)) continue;

            // The prior only transfers via the word's primary kana reading (風 かぜ must not steal ふう).
            if (!MatchesPrimaryKanaForm(candidate)) continue;

            if (challenger == null
                || frequency > challengerFrequency
                || (frequency == challengerFrequency && candidate.TotalScore > challenger.TotalScore))
            {
                challenger = candidate;
                challengerFrequency = frequency;
            }
        }

        return challenger;
    }

    private static bool HasBoundMorphemeTag(JmDictWord word)
    {
        foreach (var p in word.PartsOfSpeech)
        {
            if (p is "suf" or "n-suf" or "pref" or "n-pref" or "aux" or "aux-v" or "aux-adj" or "prt" or "ctr")
                return true;
        }

        return false;
    }

    private static bool MatchesPrimaryKanaForm(FormCandidate candidate)
    {
        foreach (var form in candidate.Word.Forms)
        {
            if (form.FormType != JmDictFormType.KanaForm) continue;
            return KanaScoringHelpers.ToNormalizedHiragana(form.Text, convertLongVowelMark: false)
                   == candidate.FormTextHiragana;
        }

        return false;
    }
}
