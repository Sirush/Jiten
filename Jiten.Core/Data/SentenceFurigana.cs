using Jiten.Core.Data.JMDict;

namespace Jiten.Core.Data;

/// <summary>One ruby group over a sentence: Position and Length index the plain Text; the word is the token it belongs to.</summary>
public readonly record struct FuriganaGroup(int Position, int Length, string Reading, int WordId, byte ReadingIndex, bool IsTarget);

public static class SentenceFurigana
{
    /// <summary>Ruby per token from its own form, falling back to the word's other kanji spellings for variant surfaces.</summary>
    public static List<FuriganaGroup> Build(string text, IReadOnlyList<SentenceToken> tokens,
                                            IReadOnlyDictionary<(int, short), JmDictWordForm> forms)
    {
        var groups = new List<FuriganaGroup>();
        Dictionary<int, List<JmDictWordForm>>? formsByWord = null;

        foreach (var token in tokens)
        {
            if (token.Position + token.Length > text.Length) continue;

            var surface = text.AsSpan(token.Position, token.Length);
            if (!ContainsKanji(surface)) continue;

            var aligned = forms.TryGetValue((token.WordId, token.ReadingIndex), out var own)
                ? Align(surface, own.RubyText)
                : [];

            if (aligned.Count == 0)
            {
                formsByWord ??= forms.Values.GroupBy(f => f.WordId).ToDictionary(g => g.Key, g => g.OrderBy(f => f.ReadingIndex).ToList());
                if (formsByWord.TryGetValue(token.WordId, out var siblings))
                {
                    foreach (var sibling in siblings)
                    {
                        if (sibling.ReadingIndex == token.ReadingIndex || sibling.FormType != JmDictFormType.KanjiForm) continue;
                        aligned = Align(surface, sibling.RubyText);
                        if (aligned.Count > 0) break;
                    }
                }
            }

            // JMdict ruby is per kanji (忘[ぼう]却[きゃく]); touching groups of one word become one ruby box, or each wide reading pushes its kanji apart
            int wordStart = groups.Count;
            foreach (var (offset, length, reading) in aligned)
            {
                var position = token.Position + offset;
                if (groups.Count > wordStart && groups[^1].Position + groups[^1].Length == position)
                    groups[^1] = groups[^1] with { Length = groups[^1].Length + length, Reading = groups[^1].Reading + reading };
                else
                    groups.Add(new FuriganaGroup(position, length, reading, token.WordId, token.ReadingIndex, token.IsTarget));
            }
        }

        return groups;
    }

    /// <summary>Walks dictionary ruby (食[た]べる) along the inflected surface (食べた) and stops at the first differing kana, where inflection starts.</summary>
    public static List<(int Offset, int Length, string Reading)> Align(ReadOnlySpan<char> surface, string? rubyText)
    {
        var result = new List<(int, int, string)>();
        if (string.IsNullOrEmpty(rubyText)) return result;

        int s = 0;
        int i = 0;
        while (i < rubyText.Length && s <= surface.Length)
        {
            int baseEnd = i;
            while (baseEnd < rubyText.Length && IsBaseChar(rubyText[baseEnd])) baseEnd++;

            if (baseEnd > i && baseEnd < rubyText.Length && rubyText[baseEnd] == '[')
            {
                int close = rubyText.IndexOf(']', baseEnd + 1);
                if (close < 0) break;

                var rubyBase = rubyText.AsSpan(i, baseEnd - i);
                if (!surface[s..].StartsWith(rubyBase, StringComparison.Ordinal)) break;

                var reading = rubyText.Substring(baseEnd + 1, close - baseEnd - 1);
                if (reading.Length > 0)
                    result.Add((s, rubyBase.Length, reading));

                s += rubyBase.Length;
                i = close + 1;
                continue;
            }

            if (s >= surface.Length || !KanaEqual(surface[s], rubyText[i])) break;
            s++;
            i++;
        }

        return result;
    }

    private static bool IsKana(char c) => c is >= '぀' and <= 'ゟ' or >= '゠' and <= 'ヿ';

    // ヵ and ヶ sit in the katakana block but act as counters inside kanji runs (一ヶ月), matching convertToRuby.ts.
    private static bool IsBaseChar(char c) => c != '[' && c != ']' && (!IsKana(c) || c is 'ヵ' or 'ヶ');

    private static bool ContainsKanji(ReadOnlySpan<char> surface)
    {
        foreach (var c in surface)
            if (JapaneseTextHelper.IsKanji(c)) return true;
        return false;
    }

    private static char ToHiragana(char c) => c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c;

    private static bool KanaEqual(char a, char b) => a == b || ToHiragana(a) == ToHiragana(b);
}
