using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Data;
using Jiten.Parser.Resolution;

namespace Jiten.Parser.Resegmentation;

internal static class UncertaintyDetector
{
    internal static readonly PartOfSpeech[] SkipPos =
    [
        PartOfSpeech.Particle, PartOfSpeech.Auxiliary, PartOfSpeech.Verb, PartOfSpeech.IAdjective,
        PartOfSpeech.SupplementarySymbol, PartOfSpeech.Symbol, PartOfSpeech.Conjunction,
        PartOfSpeech.Adnominal, PartOfSpeech.Prefix, PartOfSpeech.BlankSpace,
        PartOfSpeech.Suffix, PartOfSpeech.NounSuffix,
        PartOfSpeech.Counter, PartOfSpeech.Numeral, PartOfSpeech.Filler,
        PartOfSpeech.Expression
    ];

    public static List<UncertainSpan> FindSpans(SentenceInfo sentence, Dictionary<string, List<int>> lookups,
        Dictionary<int, JmDictWordMeta>? wordMeta = null,
        HashSet<string>? protectedSurfaces = null)
    {
        var result = new List<UncertainSpan>();

        for (int i = 0; i < sentence.Words.Count; i++)
        {
            var (word, position, length) = sentence.Words[i];

            if (word.Text.Length < 3 || word.Text.Length > 14)
                continue;
            if (word.PreMatchedWordId != null)
                continue;
            if (Array.IndexOf(SkipPos, word.PartOfSpeech) >= 0)
                continue;
            if (protectedSurfaces != null && protectedSurfaces.Contains(word.Text))
                continue;

            // Multi-character kanji numerals (五十七, 六十一) are OOV in Sudachi and often
            // only match name entries in JMDict. Flag them for resegmentation so the scorer
            // can evaluate splits like 五十+七 which resolve to real numeral entries.
            bool nameOnly = false;
            bool isCompoundNumeral = word.PartOfSpeechSection1 == PartOfSpeechSection.Numeral && word.Text.Length > 1;
            if (!isCompoundNumeral)
            {
                bool textMatch = HasMatch(word.Text, lookups);
                bool dictMatch = word.DictionaryForm != word.Text && !string.IsNullOrEmpty(word.DictionaryForm) &&
                                 HasMatch(word.DictionaryForm, lookups);
                if (textMatch || dictMatch)
                {
                    // Resolved by lookup → normally skip. Exception: an all-hiragana token whose every
                    // lookup match is a pure JMnedict name entry (一ッ岳/ひとつだけ) is almost always a
                    // misparsed common-word run; keep it eligible so the scorer can try 一つ+だけ. The
                    // acceptance-path guards (HasShortPureNameSegment / negative score) still reject
                    // genuine name fragmentation.
                    bool pureNameOnly = wordMeta != null
                        && JapaneseTextHelper.IsAllHiragana(word.Text)
                        && IsPureNameOnlyMatch(word.Text, lookups, wordMeta)
                        && (!dictMatch || IsPureNameOnlyMatch(word.DictionaryForm, lookups, wordMeta));
                    if (!pureNameOnly)
                        continue;
                    nameOnly = true;
                }
            }

            result.Add(new UncertainSpan
            {
                WordIndex = i,
                Text      = word.Text,
                Position  = position,
                Length    = length,
                NameOnly  = nameOnly
            });
        }

        return result;
    }

    internal static bool HasMatch(string text, Dictionary<string, List<int>> lookups)
    {
        if (LookupCandidateCollector.HasAnyMatch(lookups, text, includeLongVowelStripped: true))
            return true;

        try
        {
            var hira = KanaConverter.ToNormalizedHiragana(text);

            if (HasGodanDictFormMatch(text, lookups) || (hira != text && HasGodanDictFormMatch(hira, lookups)))
                return true;

            if (HasIchidanDictFormMatch(text, lookups) || (hira != text && HasIchidanDictFormMatch(hira, lookups)))
                return true;

            if (HasAdjSaNominalizationMatch(text, lookups) || (hira != text && HasAdjSaNominalizationMatch(hira, lookups)))
                return true;
        }
        catch { }
        return false;
    }

    // True when every lookup match for `text` is a pure JMnedict name entry and there is no
    // verb/adjective-derived match — i.e. the only thing keeping the token "resolved" is a name.
    private static bool IsPureNameOnlyMatch(string text, Dictionary<string, List<int>> lookups,
        Dictionary<int, JmDictWordMeta> wordMeta)
    {
        string hira;
        try { hira = KanaConverter.ToNormalizedHiragana(text); }
        catch { hira = text; }

        if (HasGodanDictFormMatch(text, lookups) || (hira != text && HasGodanDictFormMatch(hira, lookups)))
            return false;
        if (HasIchidanDictFormMatch(text, lookups) || (hira != text && HasIchidanDictFormMatch(hira, lookups)))
            return false;
        if (HasAdjSaNominalizationMatch(text, lookups) || (hira != text && HasAdjSaNominalizationMatch(hira, lookups)))
            return false;

        bool any = false;
        foreach (var key in hira != text ? new[] { text, hira } : new[] { text })
        {
            if (!lookups.TryGetValue(key, out var ids)) continue;
            foreach (var id in ids)
            {
                any = true;
                if (!wordMeta.TryGetValue(id, out var meta)) return false;
                if (!meta.IsTrueName) return false;
                if (meta.Pos.Any(p => p is not (PartOfSpeech.Name or PartOfSpeech.Unknown))) return false;
            }
        }

        return any;
    }

    private static bool HasGodanDictFormMatch(string text, Dictionary<string, List<int>> lookups)
    {
        var dictForm = MorphologicalAnalyser.TryGodanDictForm(text);
        return dictForm != null && lookups.TryGetValue(dictForm, out var ids) && ids.Count > 0;
    }

    private static bool HasIchidanDictFormMatch(string text, Dictionary<string, List<int>> lookups)
    {
        if (text.Length < 2) return false;
        var dictForm = text + "る";
        return lookups.TryGetValue(dictForm, out var ids) && ids.Count > 0;
    }

    private static bool HasAdjSaNominalizationMatch(string text, Dictionary<string, List<int>> lookups)
    {
        if (text.Length < 3 || text[^1] != 'さ') return false;
        var adjForm = text[..^1] + "い";
        return lookups.TryGetValue(adjForm, out var ids) && ids.Count > 0;
    }
}
