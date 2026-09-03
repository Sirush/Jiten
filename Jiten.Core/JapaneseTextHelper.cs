using System.Text;
using WanaKanaShaapu;

namespace Jiten.Core;

public static class JapaneseTextHelper
{
    /// <summary>
    /// Determines whether the specified Unicode rune is a CJK kanji character.
    /// Covers main CJK Unified Ideographs block and extensions A-E, plus compatibility ranges.
    /// </summary>
    public static bool IsKana(char c) =>
        c is (>= '\u3040' and <= '\u309F') or (>= '\u30A0' and <= '\u30FF');

    public static bool IsHiragana(char c) =>
        c is >= '\u3040' and <= '\u309F';

    public static bool IsKatakana(char c) =>
        c is >= '\u30A0' and <= '\u30FF';

    /// <summary>Drop-in for WanaKana.IsKana (same character ranges, false on empty) without its allocations.</summary>
    public static bool IsAllKana(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty) return false;
        foreach (var c in s)
            if (!IsKana(c))
                return false;
        return true;
    }

    /// <summary>Drop-in for WanaKana.ToKatakana on kana-only input: the whole hiragana block shifts by 0x60, everything else stays.</summary>
    public static string HiraganaToKatakana(string s)
    {
        int first = -1;
        for (int i = 0; i < s.Length; i++)
            if (IsHiragana(s[i])) { first = i; break; }
        if (first < 0) return s;

        return string.Create(s.Length, s, static (span, src) =>
        {
            for (int i = 0; i < src.Length; i++)
                span[i] = IsHiragana(src[i]) ? (char)(src[i] + 0x60) : src[i];
        });
    }

    /// <summary>Non-empty string whose every char is hiragana. Allocation-free.</summary>
    public static bool IsAllHiragana(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty) return false;
        foreach (var c in s)
            if (!IsHiragana(c)) return false;
        return true;
    }

    /// <summary>Non-empty string whose every char is katakana. Allocation-free.</summary>
    public static bool IsAllKatakana(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty) return false;
        foreach (var c in s)
            if (!IsKatakana(c)) return false;
        return true;
    }

    /// <summary>Katakana letter (ァ–ヺ) or the long-vowel mark ー — the characters a katakana
    /// word is spelled with, excluding middle dots and iteration marks.</summary>
    public static bool IsKatakanaWordChar(char c) =>
        c is (>= 'ァ' and <= 'ヺ') or 'ー';

    /// <summary>Fullwidth (Ａ-Ｚ/ａ-ｚ) or halfwidth (A-Z/a-z) Latin letter.</summary>
    public static bool IsLatinLetter(char c) =>
        c is (>= '\uFF21' and <= '\uFF3A') or (>= '\uFF41' and <= '\uFF5A')  // fullwidth Ａ-Ｚ / ａ-ｚ
          or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');                     // halfwidth A-Z / a-z

    /// <summary>Non-empty string whose every char is a Latin letter (fullwidth or halfwidth). Allocation-free.</summary>
    public static bool IsAllLatin(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty) return false;
        foreach (var c in s)
            if (!IsLatinLetter(c)) return false;
        return true;
    }

    /// <summary>
    /// Converts katakana characters to their hiragana equivalents, leaving all other characters
    /// (kanji, ASCII, hiragana, the long-vowel mark, etc.) untouched. Allocation-free when the
    /// input contains no katakana.
    /// </summary>
    public static string KatakanaToHiragana(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;

        var hasKatakana = false;
        foreach (var c in s)
            if (c is >= 'ァ' and <= 'ヶ') { hasKatakana = true; break; }

        if (!hasKatakana) return s;

        var buffer = new StringBuilder(s.Length);
        foreach (var c in s)
            buffer.Append(c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c);
        return buffer.ToString();
    }

    /// <summary>
    /// Katakana-to-hiragana conversion through WanaKana, guarded against its chōonpu expansion:
    /// the vowel table it looks the preceding kana up in has no entry for ヵヶゎヮ ヷ-ヺ ヽヾヿ and
    /// no vowel to repeat after ッ, so a following ー throws instead of converting.
    /// </summary>
    public static string ToHiragana(string text, bool convertLongVowelMark = true)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var input = text.Replace("ヶ", "ケ").Replace("ヵ", "カ").Replace("ゎ", "わ").Replace("ヮ", "ワ");

        try
        {
            return WanaKana.ToHiragana(input, convertLongVowelMark ? LongVowelConversion : NoLongVowelConversion);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            return WanaKana.ToHiragana(input, NoLongVowelConversion);
        }
    }

    private static readonly DefaultOptions LongVowelConversion = new() { ConvertLongVowelMark = true };
    private static readonly DefaultOptions NoLongVowelConversion = new() { ConvertLongVowelMark = false };

    public static bool IsKanji(char c) =>
        c is (>= '一' and <= '鿿') or (>= '㐀' and <= '䶿') or (>= '豈' and <= '﫿');

    public static bool IsKanji(Rune r)
    {
        int value = r.Value;
        return value is
            (>= 0x4E00 and <= 0x9FFF) or   // Main block (Common)
            (>= 0x3400 and <= 0x4DBF) or   // Extension A
            (>= 0x20000 and <= 0x2A6DF) or // Extension B
            (>= 0x2A700 and <= 0x2B73F) or // Extension C
            (>= 0x2B740 and <= 0x2B81F) or // Extension D
            (>= 0x2B820 and <= 0x2CEAF) or // Extension E
            (>= 0xF900 and <= 0xFAFF) or   // Compatibility Ideographs
            (>= 0x2F800 and <= 0x2FA1F);   // Compatibility Supplement
    }

    /// <summary>
    /// A numeral character: an ASCII or full-width digit, a kanji digit (一〜九), a kanji place
    /// marker (十百千万億兆), or a kanji zero (〇零). Covers the counting/quantity vocabulary shared by
    /// the numeral-context guards; contexts that need a narrower kanji-only set test IsKanji separately.
    /// </summary>
    public static bool IsNumeralChar(char c) =>
        c is (>= '0' and <= '9') or (>= '０' and <= '９')
          or '一' or '二' or '三' or '四' or '五' or '六' or '七' or '八' or '九'
          or '十' or '百' or '千' or '万' or '億' or '兆' or '〇' or '零';
}
