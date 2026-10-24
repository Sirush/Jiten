using Jiten.Core;
using System.Text;

namespace Jiten.Parser.Data;

/// <summary>SafeAcrossTokens: keeps the final kanji and every kana part spans two or more morae (むか|い風); short kana tails read as grammar (出し手 → 出して).</summary>
internal readonly record struct PartialKanaForm(int WordId, byte ReadingIndex, bool SafeAcrossTokens);

/// <summary>Spellings with some kanji written in their furigana (むかい風 for 向かい風), keyed to the one word they can mean.</summary>
internal static class PartialKanaSpellings
{
    private const int MaxRubySegments = 4;

    /// <summary>A key two different words can produce resolves to null, so collisions never guess; within one word the kana-richer form wins (向かい風 over 向い風).</summary>
    public static Dictionary<string, PartialKanaForm?> Build(IEnumerable<(int WordId, short ReadingIndex, string RubyText)> forms)
    {
        var result = new Dictionary<string, PartialKanaForm?>();
        var sourceKana = new Dictionary<string, int>();
        foreach (var (wordId, readingIndex, rubyText) in forms)
        {
            var segments = ParseRuby(rubyText);
            if (segments == null) continue;

            int kanaInForm = segments.Where(s => s.Ruby == null).Sum(s => s.Text.Length);
            int rubyCount = segments.Count(s => s.Ruby != null);
            if (rubyCount is < 2 or > MaxRubySegments) continue;

            int allKana = (1 << rubyCount) - 1;
            for (int mask = 1; mask < allKana; mask++)
            {
                var sb = new StringBuilder();
                bool safe = true;
                int bit = 0;
                for (int i = 0; i < segments.Count; i++)
                {
                    var (text, ruby) = segments[i];
                    if (ruby == null)
                    {
                        sb.Append(text);
                        continue;
                    }

                    if ((mask & (1 << bit)) != 0)
                    {
                        sb.Append(ruby);
                        if (ruby.Length < 2 || i == segments.Count - 1)
                            safe = false;
                    }
                    else
                    {
                        sb.Append(text);
                    }

                    bit++;
                }

                var key = sb.ToString();
                var form = new PartialKanaForm(wordId, (byte)readingIndex, safe);
                if (!result.TryGetValue(key, out var existing))
                {
                    result[key] = form;
                    sourceKana[key] = kanaInForm;
                }
                else if (existing is { } e)
                {
                    if (e.WordId != wordId)
                    {
                        result[key] = null;
                        continue;
                    }

                    bool richer = kanaInForm > sourceKana[key];
                    result[key] = (richer ? form : e) with { SafeAcrossTokens = safe || e.SafeAcrossTokens };
                    if (richer) sourceKana[key] = kanaInForm;
                }
            }
        }

        return result;
    }

    /// <summary>向[む]かい風[かぜ] → (向, む), (かい, null), (風, かぜ); the ruby base is the non-kana run before '['.</summary>
    private static List<(string Text, string? Ruby)>? ParseRuby(string rubyText)
    {
        var segments = new List<(string Text, string? Ruby)>();
        var pending = new StringBuilder();
        int i = 0;
        while (i < rubyText.Length)
        {
            char c = rubyText[i];
            if (c != '[')
            {
                pending.Append(c);
                i++;
                continue;
            }

            int close = rubyText.IndexOf(']', i + 1);
            if (close < 0) return null;

            int baseStart = pending.Length;
            while (baseStart > 0 && !JapaneseTextHelper.IsKana(pending[baseStart - 1]))
                baseStart--;
            if (baseStart == pending.Length) return null;

            if (baseStart > 0)
                segments.Add((pending.ToString(0, baseStart), null));
            segments.Add((pending.ToString(baseStart, pending.Length - baseStart), rubyText[(i + 1)..close]));
            pending.Clear();
            i = close + 1;
        }

        if (pending.Length > 0)
            segments.Add((pending.ToString(), null));
        return segments;
    }
}
