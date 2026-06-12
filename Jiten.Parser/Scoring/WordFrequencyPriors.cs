using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;

namespace Jiten.Parser.Scoring;

/// Lopsided word-frequency override (S-E): when the scorer picks a surface-exact kana homograph
/// whose corpus frequency is dwarfed (≥10×) by another surface-exact candidate, defer to the
/// frequency evidence. This is a gate, not a reranker — frequencies are self-derived from
/// parsed decks, so it only fires between non-inflectable direct surface matches with
/// overwhelming ratios (カイロ: 懐炉 225× over Cairo; むこう: 向こう 12× over 無効).
/// Verbs/adjectives are excluded (よる, つく — context decides, not priors), and the
/// challenger must win on its PRIMARY reading (風(かぜ) must not steal ふう via a minor reading).
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

        // Kana-surface WSD only: kanji homographs (主 ぬし/しゅ) are reading ambiguities owned
        // by the ruby priors and reading scorers, not by word-level frequency.
        if (!context.IsKanaSurface)
            return null;

        // Only override picks that are themselves plain surface-exact matches — conjugated or
        // fold-matched picks carry morphological evidence the word-level prior can't outweigh.
        if (context.Surface != best.Form.Text || best.DeconjForm?.Process is { Count: > 0 })
            return null;

        // Both sides need observed data: an unobserved best is not evidence of a misparse
        // (often a rare-but-correct mimetic the frequency table simply hasn't seen).
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
            if (candidate.DeconjForm?.Process is { Count: > 0 }) continue;
            if (candidate.IsPosIncompatibleDirectSurface && !best.IsPosIncompatibleDirectSurface) continue;

            if (!frequencies.TryGetValue(candidate.Word.WordId, out double frequency)) continue;
            if (frequency < MinChallengerFrequency || frequency < bestFrequency * MinRatio) continue;

            // Frequency only decides near-ties; a clear scorer preference stands (封 must not
            // steal ふう from 風 across a 27-point gap).
            if (bestEffective - ScoringPolicy.EffectiveScore(candidate) > MaxScoreGap) continue;

            if (KanaScoringHelpers.IsInflectableVerbOrAdj(candidate.Word.PartsOfSpeech)) continue;

            // Bound morphemes (がち=勝ち suf, counters, particles…) owe their corpus frequency
            // to attached usage; it says nothing about a standalone token.
            if (HasBoundMorphemeTag(candidate.Word)) continue;

            // The word-level prior only transfers to this surface when it's the word's primary
            // kana reading — a frequent word must not win via one of its minor readings
            // (風(かぜ) must not steal ふう).
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
