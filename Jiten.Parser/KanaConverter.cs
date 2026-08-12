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
