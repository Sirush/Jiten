namespace Jiten.Parser.Resolution;

internal static class LookupCandidateCollector
{
    /// Checks whether any lookup variant (raw, hiragana, normalized, long-vowel-stripped) has at least one match.
    public static bool HasAnyMatch(
        Dictionary<string, List<int>> lookups,
        string text,
        bool includeLongVowelStripped = false)
    {
        if (lookups.TryGetValue(text, out var ids) && ids.Count > 0)
            return true;

        var hiragana = KanaConverter.ToHiragana(text, convertLongVowelMark: false);
        if (hiragana != text && lookups.TryGetValue(hiragana, out ids) && ids.Count > 0)
            return true;

        var normalized = KanaNormalizer.Normalize(hiragana);
        if (normalized != hiragana && lookups.TryGetValue(normalized, out ids) && ids.Count > 0)
            return true;

        if (includeLongVowelStripped && text.Contains('ー'))
        {
            var stripped = text.Replace("ー", "");
            if (stripped.Length > 0)
            {
                var strippedHira = KanaConverter.ToNormalizedHiragana(stripped);
                if (lookups.TryGetValue(strippedHira, out ids) && ids.Count > 0)
                    return true;
            }
        }

        return false;
    }

    /// Collects distinct word IDs from lookups by raw text, hiragana, and optionally kana-normalized and long-vowel-stripped variants.
    /// `normalizedTierGate`, when set, filters ids found ONLY via the kana-normalized / long-vowel-stripped
    /// tiers: those rewrites invent readings the author never wrote (いえー → いえい), so a hit there must be
    /// a word plausibly written in kana, not a kanji word reached through its reading key (遺影).
    public static List<int> CollectIds(
        Dictionary<string, List<int>> lookups,
        string text,
        bool includeKanaNormalized = true,
        bool includeLongVowelStripped = false,
        Func<int, bool>? normalizedTierGate = null)
    {
        var seen = new HashSet<int>();
        var ids = new List<int>();

        void AddRange(List<int> source, bool gated = false)
        {
            foreach (var id in source)
            {
                if (gated && normalizedTierGate != null && !seen.Contains(id) && !normalizedTierGate(id))
                    continue;
                if (seen.Add(id)) ids.Add(id);
            }
        }

        if (lookups.TryGetValue(text, out var direct)) AddRange(direct);

        var hiragana = KanaConverter.ToHiragana(text, convertLongVowelMark: false);
        if (hiragana != text && lookups.TryGetValue(hiragana, out var hiraIds))
            AddRange(hiraIds);

        if (includeKanaNormalized)
        {
            var normalized = KanaNormalizer.Normalize(hiragana);
            if (normalized != hiragana && lookups.TryGetValue(normalized, out var normIds))
                AddRange(normIds, gated: true);
        }

        if (includeLongVowelStripped && text.Contains('ー'))
        {
            var textStripped = text.Replace("ー", "");
            bool endsInBar = text.EndsWith("ー");
            bool isShort = textStripped.Length <= 2;

            if (!endsInBar || !isShort)
            {
                var strippedHira = KanaConverter.ToHiragana(textStripped);
                if (lookups.TryGetValue(strippedHira, out var stripIds))
                    AddRange(stripIds, gated: true);
            }
        }

        return ids;
    }
}
