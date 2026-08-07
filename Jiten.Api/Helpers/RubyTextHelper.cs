using System.Text;
using Jiten.Core.Data.JMDict;

namespace Jiten.Api.Helpers;

public static class RubyTextHelper
{
    private static bool IsKana(char ch)
        => ch is >= '぀' and <= 'ゟ' || ch is >= '゠' and <= 'ヿ';

    private static char ToHiragana(char ch)
        => ch is >= 'ァ' and <= 'ヶ' ? (char)(ch - 0x60) : ch;

    private static bool KanaEqual(char a, char b)
        => ToHiragana(a) == ToHiragana(b);

    public static string? GuessRubyText(string kanjiText, string kanaText)
    {
        var chars = kanjiText.ToCharArray();
        var kana = kanaText.ToCharArray();

        int prefixKana = 0;
        while (prefixKana < chars.Length && prefixKana < kana.Length
               && IsKana(chars[prefixKana]) && KanaEqual(chars[prefixKana], kana[prefixKana]))
            prefixKana++;

        int suffixKana = 0;
        while (suffixKana < chars.Length - prefixKana
               && suffixKana < kana.Length - prefixKana
               && IsKana(chars[chars.Length - 1 - suffixKana])
               && KanaEqual(chars[chars.Length - 1 - suffixKana], kana[kana.Length - 1 - suffixKana]))
            suffixKana++;

        var midKanji = chars.AsSpan(prefixKana, chars.Length - prefixKana - suffixKana);
        var midKana = kana.AsSpan(prefixKana, kana.Length - prefixKana - suffixKana);

        if (midKanji.Length == 0 || midKana.Length == 0) return null;

        var kanjiRuns = new List<(int Start, int End)>();
        int i = 0;
        while (i < midKanji.Length)
        {
            if (!IsKana(midKanji[i]))
            {
                int start = i;
                while (i < midKanji.Length && !IsKana(midKanji[i])) i++;
                kanjiRuns.Add((start, i));
            }
            else
            {
                i++;
            }
        }

        if (kanjiRuns.Count == 0) return null;

        if (kanjiRuns.Count == 1 && !midKanji.ToArray().Any(IsKana))
        {
            var prefix = new string(chars, 0, prefixKana);
            var suffix = suffixKana > 0 ? new string(chars, chars.Length - suffixKana, suffixKana) : "";
            var kanjiPart = new string(midKanji);
            var reading = new string(midKana);
            return $"{prefix}{kanjiPart}[{reading}]{suffix}";
        }

        var kanaSegments = new List<string>();
        for (int j = 0; j < midKanji.Length; j++)
        {
            if (IsKana(midKanji[j]))
            {
                if (kanaSegments.Count == 0 || !IsKana(midKanji[j - 1]))
                    kanaSegments.Add("");
                kanaSegments[^1] += midKanji[j];
            }
        }

        var midKanaStr = new string(midKana.ToArray().Select(ToHiragana).ToArray());
        var parts = new List<string>();
        var remaining = midKanaStr;
        bool valid = true;
        foreach (var sep in kanaSegments)
        {
            var normalizedSep = new string(sep.Select(ToHiragana).ToArray());
            int idx = remaining.IndexOf(normalizedSep, StringComparison.Ordinal);
            if (idx == -1) { valid = false; break; }
            parts.Add(remaining[..idx]);
            remaining = remaining[(idx + normalizedSep.Length)..];
        }
        if (valid) parts.Add(remaining);

        if (!valid || parts.Count != kanjiRuns.Count) return null;
        if (parts.Any(p => p.Length == 0)) return null;

        var sb = new StringBuilder();
        sb.Append(chars, 0, prefixKana);
        int runIdx = 0;
        for (int j = 0; j < midKanji.Length; j++)
        {
            if (!IsKana(midKanji[j]))
            {
                if (runIdx < kanjiRuns.Count && j == kanjiRuns[runIdx].Start)
                {
                    var kanjiPart = new string(midKanji.Slice(kanjiRuns[runIdx].Start, kanjiRuns[runIdx].End - kanjiRuns[runIdx].Start));
                    sb.Append($"{kanjiPart}[{parts[runIdx]}]");
                    j = kanjiRuns[runIdx].End - 1;
                    runIdx++;
                }
            }
            else
            {
                sb.Append(midKanji[j]);
            }
        }
        if (suffixKana > 0)
            sb.Append(chars, chars.Length - suffixKana, suffixKana);
        return sb.ToString();
    }

    /// <summary>Reading of a ruby-annotated surface: 食[た]べる gives たべる. Null when any base character lacks ruby.</summary>
    public static string? KanaFromRubyText(string? rubyText)
    {
        if (string.IsNullOrEmpty(rubyText)) return null;

        var sb = new StringBuilder();
        var i = 0;
        while (i < rubyText.Length)
        {
            if (IsKana(rubyText[i]))
            {
                sb.Append(rubyText[i++]);
                continue;
            }

            var runEnd = i;
            while (runEnd < rubyText.Length && !IsKana(rubyText[runEnd]) && rubyText[runEnd] != '[') runEnd++;
            if (runEnd == i || runEnd >= rubyText.Length || rubyText[runEnd] != '[') return null;

            var close = rubyText.IndexOf(']', runEnd + 1);
            if (close < 0) return null;

            sb.Append(rubyText, runEnd + 1, close - runEnd - 1);
            i = close + 1;
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static string? FindBestGuess(JmDictWordForm kanjiForm, IEnumerable<JmDictWordForm> allForms)
    {
        var kanaForms = allForms
            .Where(f => f.FormType == JmDictFormType.KanaForm)
            .ToList();
        if (kanaForms.Count == 0) return null;

        var sameIndex = kanaForms.FirstOrDefault(f => f.ReadingIndex == kanjiForm.ReadingIndex);
        if (sameIndex != null)
        {
            var guess = GuessRubyText(kanjiForm.Text, sameIndex.Text);
            if (guess != null) return guess;
        }

        foreach (var kf in kanaForms)
        {
            if (kf == sameIndex) continue;
            var guess = GuessRubyText(kanjiForm.Text, kf.Text);
            if (guess != null) return guess;
        }
        return null;
    }

    private static bool NeedsRubyInference(JmDictWordForm form)
        => form.FormType == JmDictFormType.KanjiForm && !form.RubyText.Contains('[');

    public static void EnrichForms(Dictionary<(int, short), JmDictWordForm> forms)
    {
        var byWord = forms.Values.GroupBy(f => f.WordId);
        foreach (var group in byWord)
        {
            var allForms = group.ToList();
            foreach (var form in allForms)
            {
                if (!NeedsRubyInference(form)) continue;
                var guess = FindBestGuess(form, allForms);
                if (guess != null) form.RubyText = guess;
            }
        }
    }

    public static void EnrichForms(List<JmDictWordForm> forms)
    {
        var byWord = forms.GroupBy(f => f.WordId);
        foreach (var group in byWord)
        {
            var allForms = group.ToList();
            foreach (var form in allForms)
            {
                if (!NeedsRubyInference(form)) continue;
                var guess = FindBestGuess(form, allForms);
                if (guess != null) form.RubyText = guess;
            }
        }
    }
}
