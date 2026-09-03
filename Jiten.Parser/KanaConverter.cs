using System.Collections.Concurrent;
using Jiten.Core;

namespace Jiten.Parser;

internal static class KanaConverter
{
    private const int MaxGen0Entries = 50_000;

    private static volatile ConcurrentDictionary<(string Text, bool ConvertLongVowelMark), string> _gen0 = new();
    private static volatile ConcurrentDictionary<(string Text, bool ConvertLongVowelMark), string>? _gen1;
    private static int _gen0Count;
    private static int _rotating;

    public static string ToNormalizedHiragana(string text) =>
        KanaNormalizer.Normalize(ToHiragana(text, convertLongVowelMark: false));

    public static string ToHiragana(string text) => ToHiragana(text, convertLongVowelMark: true);

    public static string ToHiragana(string text, bool convertLongVowelMark)
    {
        if (IsAlreadyHiragana(text))
            return text;

        if (TryFoldKatakana(text, convertLongVowelMark, out var folded))
            return folded;

        var key = (text, convertLongVowelMark);

        if (_gen0.TryGetValue(key, out var result))
            return result;

        var gen1 = _gen1;
        if (gen1 != null && gen1.TryGetValue(key, out result))
        {
            _gen0.TryAdd(key, result);
            return result;
        }

        result = JapaneseTextHelper.ToHiragana(text, convertLongVowelMark);

        if (_gen0.TryAdd(key, result))
        {
            if (Interlocked.Increment(ref _gen0Count) > MaxGen0Entries)
                RotateGenerations();
        }

        return result;
    }

    // Katakana letters shift to hiragana one-to-one; ヵヶヮゎ take JapaneseTextHelper's spellings and
    // ー stays only when long-vowel conversion is off. Any other character defers to WanaKana.
    internal static bool TryFoldKatakana(string text, bool convertLongVowelMark, out string folded)
    {
        foreach (char c in text)
        {
            if (c is >= 'ぁ' and <= 'ゔ' || JapaneseTextHelper.IsKanji(c) || c is >= 'ァ' and <= 'ヶ')
                continue;
            if (c == 'ー' && !convertLongVowelMark)
                continue;
            folded = null!;
            return false;
        }

        folded = string.Create(text.Length, text, static (span, src) =>
        {
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                span[i] = c switch
                {
                    'ヵ' => 'か',
                    'ヶ' => 'け',
                    'ヮ' or 'ゎ' => 'わ',
                    >= 'ァ' and <= 'ヴ' => (char)(c - 0x60),
                    _ => c,
                };
            }
        });
        return true;
    }

    // Plain hiragana + kanji passes through WanaKana unchanged, so the cache probe is pure overhead.
    // ゎ (U+308E) is excluded: JapaneseTextHelper rewrites it to わ before converting.
    private static bool IsAlreadyHiragana(string text)
    {
        foreach (char c in text)
        {
            if (c is >= 'ぁ' and <= 'ゔ' and not 'ゎ') continue;
            if (JapaneseTextHelper.IsKanji(c)) continue;
            return false;
        }

        return true;
    }

    private static void RotateGenerations()
    {
        if (Interlocked.CompareExchange(ref _rotating, 1, 0) != 0)
            return;

        try
        {
            _gen1 = _gen0;
            _gen0 = new ConcurrentDictionary<(string Text, bool ConvertLongVowelMark), string>();
            Interlocked.Exchange(ref _gen0Count, 0);
        }
        finally
        {
            Interlocked.Exchange(ref _rotating, 0);
        }
    }
}
