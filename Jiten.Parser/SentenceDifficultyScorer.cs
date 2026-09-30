using System.Text;
using Jiten.Core;
using Jiten.Core.Data;

namespace Jiten.Parser;

public static class SentenceDifficultyScorer
{
    private const double LnR0 = 5.010635294; // ln(150)
    private const double DefaultDifficulty = 3.5; // ~rank 5K — conservative for unresolved words
    private const double Alpha = 0.5;
    private const double KanjiWeight = 4.0;
    private const double LengthWeight = 0.5;
    private const double GrammarWeight = 0.3;

    private static readonly HashSet<PartOfSpeech> SkipPos =
    [
        PartOfSpeech.Particle,
        PartOfSpeech.Auxiliary,
        PartOfSpeech.Symbol,
        PartOfSpeech.SupplementarySymbol,
        PartOfSpeech.Filler,
        PartOfSpeech.BlankSpace,
        PartOfSpeech.Name
    ];

    private static readonly HashSet<PartOfSpeechSection> SkipSections =
    [
        PartOfSpeechSection.ProperNoun,
        PartOfSpeechSection.PersonName,
        PartOfSpeechSection.FamilyName,
        PartOfSpeechSection.PlaceName,
        PartOfSpeechSection.Organization
    ];

    public static float Score(
        SentenceInfo sentence,
        IReadOnlyList<(int Position, int WordId, byte ReadingIndex)> matchedWords,
        Dictionary<(int WordId, byte ReadingIndex), int> formFreqRanks,
        Dictionary<int, int> wordFreqRanks)
    {
        var positionToMatch = new Dictionary<int, (int Position, int WordId, byte ReadingIndex)>();
        foreach (var mw in matchedWords)
            positionToMatch.TryAdd(mw.Position, mw);

        var contentDifficulties = new List<double>();
        int grammarDelta = 0;

        foreach (var (word, position, _) in sentence.Words)
        {
            int surfaceDictDelta = Math.Max(0, word.Text.Length - word.DictionaryForm.Length);
            grammarDelta += surfaceDictDelta;

            if (SkipPos.Contains(word.PartOfSpeech))
                continue;

            if (SkipSections.Contains(word.PartOfSpeechSection1))
                continue;

            double d;
            if (positionToMatch.TryGetValue(position, out var matched) &&
                formFreqRanks.TryGetValue((matched.WordId, matched.ReadingIndex), out int formRank))
            {
                d = RankToDifficulty(formRank);
            }
            else if (word.ResolvedWordId.HasValue &&
                     wordFreqRanks.TryGetValue(word.ResolvedWordId.Value, out int wordRank))
            {
                d = RankToDifficulty(wordRank);
            }
            else if (word.PreMatchedWordId.HasValue &&
                     wordFreqRanks.TryGetValue(word.PreMatchedWordId.Value, out int preRank))
            {
                d = RankToDifficulty(preRank);
            }
            else
            {
                d = DefaultDifficulty;
            }

            contentDifficulties.Add(d);
        }

        if (contentDifficulties.Count == 0)
            return 0f;

        contentDifficulties.Sort((a, b) => b.CompareTo(a));

        double V = 0, w = 1.0;
        for (int i = 0; i < contentDifficulties.Count; i++)
        {
            V += w * contentDifficulties[i];
            w *= Alpha;
        }

        double K = KanjiWeight * KanjiRatio(sentence.Text);

        double textLen = sentence.Text.Length;
        double L = textLen > 10 ? LengthWeight * Math.Log(textLen / 10.0) : 0;

        double G = GrammarWeight * grammarDelta;

        return (float)(V + K + L + G);
    }

    private static double RankToDifficulty(int rank)
    {
        double val = Math.Log(rank + 1) - LnR0;
        return val > 0 ? val : 0;
    }

    private static double KanjiRatio(string text)
    {
        if (text.Length == 0) return 0;
        int kanjiCount = 0;
        int total = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            total++;
            if (JapaneseTextHelper.IsKanji(rune))
                kanjiCount++;
        }
        return (double)kanjiCount / total;
    }
}
