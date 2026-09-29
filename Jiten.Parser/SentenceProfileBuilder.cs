using Jiten.Core.Data;

namespace Jiten.Parser;

public static class SentenceProfileBuilder
{
    /// <summary>
    /// Each sentence's distinct content words: tokens kept as deck words, minus particles, auxiliaries and names, which never block
    /// comprehension (<see cref="SentenceComprehension"/>). Sentences left with no content word ("ああ。") are dropped.
    /// <paramref name="lineBreaks"/> (ascending offsets in the flattened text) also end a sentence, for sources with one utterance per line.
    /// </summary>
    public static byte[] Build(List<SentenceInfo> sentences, IEnumerable<DeckWord> deckWords, IReadOnlyList<int>? lineBreaks = null)
    {
        var keptForms = deckWords.Select(w => (w.WordId, w.ReadingIndex)).ToHashSet();
        var result = new List<IReadOnlyCollection<int>>(sentences.Count);
        var keys = new HashSet<int>();
        int nextBreak = 0;

        foreach (var sentence in sentences)
        {
            foreach (var (wordInfo, _, _) in sentence.Words)
            {
                if (lineBreaks != null && wordInfo.StartOffset >= 0)
                {
                    bool crossed = false;
                    while (nextBreak < lineBreaks.Count && lineBreaks[nextBreak] <= wordInfo.StartOffset)
                    {
                        nextBreak++;
                        crossed = true;
                    }

                    if (crossed) Flush();
                }

                if (wordInfo.KeptForm is not { } form || !keptForms.Contains(form)) continue;
                if (wordInfo.PartOfSpeech is PartOfSpeech.Particle or PartOfSpeech.Auxiliary) continue;
                if (SentenceComprehension.IsNameEntry(form.WordId)) continue;
                keys.Add(ExampleSentenceTokens.WordKey(form.WordId, form.ReadingIndex));
            }

            Flush();
        }

        return SentenceProfileCodec.Encode(result);

        void Flush()
        {
            if (keys.Count == 0) return;
            result.Add(keys.ToArray());
            keys.Clear();
        }
    }
}
