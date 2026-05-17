using System.Text;
using Jiten.Core.Data;
using WanaKanaShaapu;

namespace Jiten.Core;

public static partial class MetadataProviderHelper
{
    public static List<DeckDictionaryEntry> BuildDictionaryEntriesFromNames(
        List<(string? native, string? firstHint, string? lastHint)> names)
    {
        var seen = new HashSet<string>();
        var entries = new List<DeckDictionaryEntry>();

        void Add(string surface)
        {
            var trimmed = surface.Trim();
            if (trimmed.Length >= 2 && seen.Add(trimmed))
                entries.Add(new DeckDictionaryEntry { Surface = trimmed, EntryType = DeckDictionaryEntryType.Name });
        }

        foreach (var (native, firstHint, lastHint) in names)
        {
            if (string.IsNullOrWhiteSpace(native)) continue;

            var trimmed = native.Replace(" ", "").Replace("　", "").Trim('・', ' ');
            if (trimmed.Length < 2) continue;

            var concatenated = trimmed.Replace("・", "");
            if (concatenated.Length >= 2)
                Add(concatenated);

            if (trimmed.Contains('・'))
            {
                Add(trimmed);
                foreach (var part in trimmed.Split('・', StringSplitOptions.RemoveEmptyEntries))
                {
                    var cleaned = part.Trim();
                    if (cleaned.Length >= 2)
                        Add(cleaned);
                }
            }
            else
            {
                // Only try heuristic splitting when ・ didn't already provide the split
                var parts = SplitName(concatenated, firstHint, lastHint);
                if (parts != null)
                {
                    Add(parts.Value.family);
                    Add(parts.Value.given);
                }
            }
        }

        return entries;
    }

    private static (string family, string given)? SplitName(string native, string? firstHint, string? lastHint)
    {
        var cleaned = native.Replace(" ", "").Replace("　", "");

        // 1. Space-separated — trust the source
        if (native.Contains(' ') || native.Contains('　'))
        {
            var spaceParts = native.Split([' ', '　'], StringSplitOptions.RemoveEmptyEntries);
            if (spaceParts.Length >= 2)
                return (spaceParts[0], string.Join("", spaceParts[1..]));
        }

        // 2. Unique kanji↔kana script boundary — most reliable signal
        var scriptSplit = FindUniqueScriptBoundary(cleaned);
        if (scriptSplit != null) return scriptSplit;

        // 3. Romaji hints for same-script names (all-kanji, all-kana)
        if (!string.IsNullOrWhiteSpace(firstHint) && !string.IsNullOrWhiteSpace(lastHint))
        {
            var result = SplitWithRomajiHints(native, firstHint, lastHint);
            if (result != null) return result;
        }

        // 4. All-kanji heuristic fallback
        return SplitByHeuristic(native);
    }

    private static (string family, string given)? FindUniqueScriptBoundary(string cleaned)
    {
        if (cleaned.Length < 2) return null;

        int boundaryCount = 0;
        int boundaryPos = -1;

        for (int i = 1; i < cleaned.Length; i++)
        {
            bool prevIsKanji = IsKanjiChar(cleaned[i - 1]);
            bool currIsKanji = IsKanjiChar(cleaned[i]);
            bool prevIsKana = JapaneseTextHelper.IsKana(cleaned[i - 1]);
            bool currIsKana = JapaneseTextHelper.IsKana(cleaned[i]);

            if ((prevIsKanji && currIsKana) || (prevIsKana && currIsKanji))
            {
                boundaryCount++;
                boundaryPos = i;
            }
        }

        if (boundaryCount == 1 && boundaryPos > 0)
            return (cleaned[..boundaryPos], cleaned[boundaryPos..]);

        return null;
    }

    private static (string family, string given)? SplitWithRomajiHints(
        string native, string firstRomaji, string lastRomaji)
    {
        var cleaned = native.Replace(" ", "").Replace("　", "");
        if (cleaned.Length < 2) return null;

        int givenKanaLen;
        int familyKanaLen;
        try
        {
            givenKanaLen = WanaKana.ToHiragana(firstRomaji.Trim()).Length;
            familyKanaLen = WanaKana.ToHiragana(lastRomaji.Trim()).Length;
        }
        catch
        {
            return null;
        }

        if (givenKanaLen == 0 || familyKanaLen == 0) return null;

        int bestSplit = -1;
        double bestScore = double.MaxValue;

        for (int i = 1; i < cleaned.Length; i++)
        {
            int leftKanaEst = EstimateKanaLength(cleaned.AsSpan(0, i));
            int rightKanaEst = EstimateKanaLength(cleaned.AsSpan(i));
            double score = Math.Abs(leftKanaEst - familyKanaLen) + Math.Abs(rightKanaEst - givenKanaLen);

            if (score < bestScore)
            {
                bestScore = score;
                bestSplit = i;
            }
        }

        if (bestSplit <= 0 || bestSplit >= cleaned.Length) return null;

        return (cleaned[..bestSplit], cleaned[bestSplit..]);
    }

    private static int EstimateKanaLength(ReadOnlySpan<char> text)
    {
        int len = 0;
        foreach (var c in text)
            len += IsKanjiChar(c) ? 2 : 1;
        return len;
    }

    private static (string family, string given)? SplitByHeuristic(string name)
    {
        var cleaned = name.Replace(" ", "").Replace("　", "");
        if (cleaned.Length < 2) return null;

        if (cleaned.All(IsKanjiChar))
        {
            if (cleaned.Length >= 3)
                return (cleaned[..2], cleaned[2..]);
        }

        return null;
    }

    private static bool IsKanjiChar(char c) => JapaneseTextHelper.IsKanji(c);
}
