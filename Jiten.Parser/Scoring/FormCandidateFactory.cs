using Jiten.Core;
using Jiten.Core.Data.JMDict;

namespace Jiten.Parser.Scoring;

internal static class FormCandidateFactory
{
    public static List<FormCandidate> EnumerateCandidateForms(
        JmDictWord word,
        string targetHiragana,
        bool allowLooseLvmMatch,
        DeconjugationForm? deconjForm = null,
        string? surface = null)
    {
        var candidates = new List<FormCandidate>();
        var targetNormalized = KanaNormalizer.Normalize(targetHiragana);
        var targetLoose = allowLooseLvmMatch
            ? KanaNormalizer.Normalize(KanaConverter.ToHiragana(targetHiragana))
            : null;
        string? targetStripped = allowLooseLvmMatch && targetHiragana.Contains('ー')
            ? targetHiragana.Replace("ー", "")
            : null;

        foreach (var form in word.Forms)
        {
            if (form.ReadingIndex > 255)
                continue;

            var formHiragana = KanaConverter.ToHiragana(form.Text, convertLongVowelMark: false);
            var formNormalized = KanaNormalizer.Normalize(formHiragana);

            bool phoneticMatch = formNormalized == targetNormalized;

            if (!phoneticMatch && allowLooseLvmMatch)
            {
                var formLoose = KanaNormalizer.Normalize(KanaConverter.ToHiragana(form.Text));
                phoneticMatch = formLoose == targetLoose;

                if (!phoneticMatch && targetStripped != null)
                {
                    var formStripped = formHiragana.Replace("ー", "");
                    phoneticMatch = targetStripped.Length > 0 && targetStripped == formStripped;
                }
            }

            if (!phoneticMatch)
                continue;

            candidates.Add(new FormCandidate(word, form, (byte)form.ReadingIndex, targetHiragana, deconjForm));
        }

        // A Latin surface (ＭＡＯ, ＤＭＴ, ＢＬＴ) must not match a Japanese kanji word through a coincidental
        // kana reading (ＭＡＯ→まお→苧 "ramie"). Reject all candidates for any word that has a real CJK-kanji
        // form; with no other candidate the token then resolves to nothing and is dropped at lookup
        // (unresolved) — preferred over mis-tagging it as the kanji word. Genuine Latin loanwords (ＣＤ, ＴＶ)
        // survive because ContainsKanji tests for real CJK-ideograph codepoints: their headwords are Latin
        // letters plus katakana readings, and the Latin headword — though JMDict files it in a kanji-form slot —
        // holds no actual kanji, so Forms.Any(ContainsKanji) is false. The rare entry that pairs a Latin form
        // with a true kanji form (ｗ ↔ 笑/藁) does lose its kanji candidate here, but those surfaces are lone
        // Latin letters that fail lookup anyway, so no real loanword is dropped.
        if (candidates.Count > 0 && surface != null && JapaneseTextHelper.IsAllLatin(surface)
            && word.Forms.Any(f => KanaScoringHelpers.ContainsKanji(f.Text)))
            return [];

        // Per-word hard filters: drop search-only/obsolete forms only if non-search-only/non-obsolete alternatives exist
        if (candidates.Count <= 1)
            return candidates;

        var nonSearchOnly = candidates.Where(c => !c.Form.IsSearchOnly || c.Form.Text == surface).ToList();
        if (nonSearchOnly.Count > 0)
            candidates = nonSearchOnly;

        var nonObsolete = candidates.Where(c => !c.Form.IsObsolete || c.Form.Text == surface).ToList();
        if (nonObsolete.Count > 0)
            candidates = nonObsolete;

        return candidates;
    }

    public static bool HasKanaReadingMatch(JmDictWord word, string sudachiReading, bool allowStemMatch = false)
    {
        var readingHiragana = KanaConverter.ToHiragana(sudachiReading, convertLongVowelMark: false);
        return word.Forms
            .Where(f => f.FormType == JmDictFormType.KanaForm)
            .Any(f =>
            {
                var formHiragana = KanaConverter.ToHiragana(f.Text, convertLongVowelMark: false);
                return formHiragana == readingHiragana ||
                       (allowStemMatch && formHiragana.StartsWith(readingHiragana));
            });
    }

    public static bool IsSuruNounWithoutExpression(IList<string> posTags)
    {
        bool hasNoun = false, hasSuru = false, hasExpression = false;
        foreach (var tag in posTags)
        {
            if (tag is "n" or "n-adv" or "n-t") hasNoun = true;
            else if (tag is "vs" or "vs-s" or "vs-i" or "vs-c") hasSuru = true;
            else if (tag is "exp") hasExpression = true;
        }

        return hasNoun && hasSuru && !hasExpression;
    }
}
