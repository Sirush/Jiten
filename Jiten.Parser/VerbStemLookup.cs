using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Data;

namespace Jiten.Parser;

internal static class VerbStemLookup
{
    /// <summary>Most frequent verb whose bare 連用形 is <paramref name="surface"/> (回し → 回す, 答え → 答える); kana-only surfaces never match.</summary>
    public static (int WordId, string DictionaryForm)? Find(string surface, string reading,
                                                            Dictionary<string, List<int>> lookups,
                                                            Dictionary<int, JmDictWordMeta> wordMeta,
                                                            Dictionary<int, int> frequencyRanks)
    {
        if (surface.Length < 2 || !JapaneseTextHelper.IsHiragana(surface[^1])
                               || !surface.Any(JapaneseTextHelper.IsKanji))
            return null;

        var readingHira = string.IsNullOrEmpty(reading)
            ? null
            : KanaConverter.ToHiragana(reading, convertLongVowelMark: false);

        (int WordId, string DictionaryForm)? best = null;
        int bestRank = int.MaxValue;

        void Consider(string? dictForm, string? readingDictForm)
        {
            if (dictForm == null || !lookups.TryGetValue(dictForm, out var ids))
                return;

            List<int>? readingIds = null;
            // A kanji verb read differently from Sudachi's reading of the stem is a different word.
            if (readingHira != null && (readingDictForm == null || !lookups.TryGetValue(readingDictForm, out readingIds)))
                return;

            foreach (var id in ids)
            {
                if (readingIds != null && !readingIds.Contains(id))
                    continue;
                if (!wordMeta.TryGetValue(id, out var meta) || Array.IndexOf(meta.Pos, PartOfSpeech.Verb) < 0)
                    continue;

                var rank = frequencyRanks.TryGetValue(id, out var r) ? r : int.MaxValue - 1;
                if (rank < bestRank)
                {
                    bestRank = rank;
                    best = (id, dictForm);
                }
            }
        }

        Consider(MorphologicalAnalyser.TryGodanDictForm(surface),
                 readingHira == null ? null : MorphologicalAnalyser.TryGodanDictForm(readingHira));
        Consider(surface + "る", readingHira == null ? null : readingHira + "る");

        return best;
    }

    public static void Pin(WordInfo word, (int WordId, string DictionaryForm) verb)
    {
        word.PartOfSpeech = PartOfSpeech.Verb;
        word.DictionaryForm = verb.DictionaryForm;
        word.NormalizedForm = verb.DictionaryForm;
        word.PreMatchedWordId = verb.WordId;
        word.PreMatchedCandidateWordIds = null;
        word.PreMatchedConjugations = ["(infinitive)"];
    }
}
