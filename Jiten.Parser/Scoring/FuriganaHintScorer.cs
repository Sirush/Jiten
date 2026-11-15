using Jiten.Core.Data.JMDict;

namespace Jiten.Parser.Scoring;

internal static class FuriganaHintScorer
{
    private const int MatchBonus = 500;
    private const string SmallKana = "ぁぃぅぇぉっゃゅょゎ";
    private const string LargeKana = "あいうえおつやゆよわ";

    public static int Score(FormCandidate candidate, string? hintHiragana)
    {
        if (hintHiragana is null) return 0;

        foreach (var form in candidate.Word.Forms)
        {
            if (form.FormType != JmDictFormType.KanaForm) continue;

            var formHiragana = KanaScoringHelpers.ToNormalizedHiragana(form.Text, convertLongVowelMark: false);
            if (formHiragana == hintHiragana || MatchesIgnoringKanaSize(formHiragana, hintHiragana))
                return MatchBonus;
        }

        return 0;
    }

    /// <summary>Ruby is often typeset without small kana (いつしよ for いっしょ), so a hint with none also matches the small-kana reading.</summary>
    internal static bool MatchesIgnoringKanaSize(string formHiragana, string hintHiragana)
    {
        if (formHiragana.Length != hintHiragana.Length) return false;

        for (int i = 0; i < formHiragana.Length; i++)
        {
            char hint = hintHiragana[i];
            if (SmallKana.Contains(hint)) return false;

            char c = formHiragana[i];
            if (c == hint) continue;

            int small = SmallKana.IndexOf(c);
            if (small < 0 || LargeKana[small] != hint) return false;
        }

        return true;
    }
}
