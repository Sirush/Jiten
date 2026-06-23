using System.Text;

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
}
