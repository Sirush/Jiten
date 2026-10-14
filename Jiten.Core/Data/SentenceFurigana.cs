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

            var trailing = text.AsSpan(token.Position + token.Length);
            var aligned = forms.TryGetValue((token.WordId, token.ReadingIndex), out var own)
                ? Align(surface, own.RubyText, trailing)
                : [];

            if (aligned.Count == 0)
            {
                formsByWord ??= forms.Values.GroupBy(f => f.WordId).ToDictionary(g => g.Key, g => g.OrderBy(f => f.ReadingIndex).ToList());
                if (formsByWord.TryGetValue(token.WordId, out var siblings))
                {
                    foreach (var sibling in siblings)
                    {
                        if (sibling.ReadingIndex == token.ReadingIndex || sibling.FormType != JmDictFormType.KanjiForm) continue;
                        aligned = Align(surface, sibling.RubyText, trailing);
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

    /// <summary>Walks dictionary ruby (食[た]べる) along the inflected surface (食べた) and stops at the first differing kana, where inflection starts; trailing is the text after the surface, read only to pick 来 and 得's readings.</summary>
    public static List<(int Offset, int Length, string Reading)> Align(ReadOnlySpan<char> surface, string? rubyText, ReadOnlySpan<char> trailing = default)
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
                if (rubyText.AsSpan(close + 1) is "る")
                    reading = VerbFinalReading(rubyBase, reading, surface[(s + rubyBase.Length)..], trailing);

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

    // First prefix match wins, so longer prefixes come first.
    private static readonly (string Prefix, string Reading)[] KuruReadings =
    [
        ("ない", "こ"), ("なか", "こ"), ("なく", "こ"), ("なけ", "こ"), ("なきゃ", "こ"),
        ("れば", "く"), ("りゃ", "く"), ("る", "く"),
        ("な", "き"), ("た", "き"), ("て", "き"), ("ま", "き"), ("ち", "き"), ("そう", "き"),
        ("と", "き"), ("や", "き"), ("つ", "き"), ("っ", "き"),
        ("れ", "こ"), ("ら", "こ"), ("さ", "こ"), ("よ", "こ"), ("い", "こ"), ("ず", "こ"),
        ("ぬ", "こ"), ("ね", "こ"), ("ん", "こ"),
    ];

    // うる survives only in the plain and べき forms; every other conjugation of 得る reads え (あり得ない, あり得ます).
    private static readonly (string Prefix, string Reading)[] UruReadings = [("る", "う"), ("べ", "う")];

    private const int FollowingPrefixMaxLength = 3;

    /// <summary>The kanji's reading in a verb ending in ruby + る (やって来[く]る, あり得[う]る), where 来 and 得 change reading with the conjugation; empty when it can't be told.</summary>
    private static string VerbFinalReading(ReadOnlySpan<char> rubyBase, string reading, ReadOnlySpan<char> surfaceTail, ReadOnlySpan<char> trailing)
    {
        if (rubyBase is "来" or "來" && reading == "く")
            return MatchFollowing(KuruReadings, surfaceTail, trailing) ?? "";
        if (rubyBase is "得" && reading == "う")
            return MatchFollowing(UruReadings, surfaceTail, trailing) ?? "え";
        return reading;
    }

    private static string? MatchFollowing((string Prefix, string Reading)[] table, ReadOnlySpan<char> surfaceTail, ReadOnlySpan<char> trailing)
    {
        Span<char> following = stackalloc char[FollowingPrefixMaxLength];
        int n = 0;
        foreach (var c in surfaceTail)
        {
            if (n == following.Length) break;
            following[n++] = ToHiragana(c);
        }
        foreach (var c in trailing)
        {
            if (n == following.Length) break;
            following[n++] = ToHiragana(c);
        }

        ReadOnlySpan<char> next = following[..n];
        foreach (var (prefix, reading) in table)
            if (next.StartsWith(prefix, StringComparison.Ordinal)) return reading;
        return null;
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
