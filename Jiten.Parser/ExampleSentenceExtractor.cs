using System.Diagnostics;
using System.Text.RegularExpressions;
using Jiten.Core.Data;

namespace Jiten.Parser;

public static partial class ExampleSentenceExtractor
{
    private readonly record struct Pass(int MinLength, int MaxLength, float Percentage);

    private static readonly Pass[] ProsePasses = [new(15, 35, 0.25f), new(10, 45, 0.5f), new(10, 55, 1f)];

    // Subtitle sentences run short (median cue 13 chars), so prose windows would discard most of them.
    private static readonly Pass[] SubtitlePasses = [new(8, 30, 0.25f), new(8, 40, 0.5f), new(8, 45, 1f)];

    public static List<ExampleSentence> ExtractSentences(
        List<SentenceInfo> sentences,
        DeckWord[] words,
        Dictionary<(int WordId, byte ReadingIndex), int> formFreqRanks,
        Dictionary<int, int> wordFreqRanks,
        bool subtitleSpeech = false)
    {
        static bool IsPosMatch(DeckWord deckWord, WordInfo token)
        {
            return deckWord.SudachiPartOfSpeech == token.PartOfSpeech ||
                   deckWord.PartsOfSpeech.Any(pos => pos == token.PartOfSpeech);
        }

        static bool IsPosCompatible(DeckWord deckWord, WordInfo token)
        {
            if (IsPosMatch(deckWord, token))
                return true;

            if (PosMapper.IsNameLikeSudachiNoun(
                    token.PartOfSpeech,
                    token.PartOfSpeechSection1,
                    token.PartOfSpeechSection2,
                    token.PartOfSpeechSection3) &&
                deckWord.PartsOfSpeech.Any(pos => pos == PartOfSpeech.Name))
            {
                return true;
            }

            return false;
        }

        // Claims the deck word this token stands for, if one is still waiting for a sentence.
        static DeckWord? TakeTargetWord(WordInfo wordInfo, Dictionary<string, List<DeckWord>> wordsByText,
                                        HashSet<string> textsWithNonNameEntry)
        {
            if (!wordsByText.TryGetValue(wordInfo.Text, out var wordList) || wordList.Count <= 0) return null;

            // Find best word with matching POS — prefer direct POS match over name-like fallback.
            // When multiple DeckWords share the same text and POS (e.g. 身体 as からだ vs しんたい),
            // prefer the one whose SudachiReading matches the token's reading.
            int matchIndex = -1;
            int fallbackIndex = -1;
            int posMatchNoReading = -1;

            // Priority 0: exact WordId match from parser resolution — avoids assigning
            // an example sentence to a wrong DeckWord when the same surface text resolved
            // to different WordIds in different sentences (e.g. さっき → 1005180 vs misparse 1299060).
            if (wordInfo.ResolvedWordId.HasValue)
            {
                for (int j = 0; j < wordList.Count; j++)
                {
                    if (wordList[j].WordId == wordInfo.ResolvedWordId.Value && IsPosMatch(wordList[j], wordInfo))
                    {
                        matchIndex = j;
                        break;
                    }
                }
            }

            if (matchIndex == -1)
            {
                for (int j = 0; j < wordList.Count; j++)
                {
                    if (IsPosMatch(wordList[j], wordInfo))
                    {
                        if (!string.IsNullOrEmpty(wordInfo.Reading) &&
                            !string.IsNullOrEmpty(wordList[j].SudachiReading) &&
                            wordList[j].SudachiReading == wordInfo.Reading)
                        {
                            matchIndex = j;
                            break;
                        }

                        if (posMatchNoReading == -1)
                            posMatchNoReading = j;
                    }

                    if (fallbackIndex == -1 && IsPosCompatible(wordList[j], wordInfo))
                        fallbackIndex = j;
                }

                if (matchIndex == -1 && posMatchNoReading >= 0)
                {
                    var fallbackWord = wordList[posMatchNoReading];
                    bool readingConflict = !string.IsNullOrEmpty(wordInfo.Reading) &&
                                           (string.IsNullOrEmpty(fallbackWord.SudachiReading) ||
                                            fallbackWord.SudachiReading != wordInfo.Reading);
                    if (!readingConflict)
                        matchIndex = posMatchNoReading;
                }

                // Only use the name-like fallback when either:
                // - this surface text has no competing non-Name DeckWord (pure name word), OR
                // - the token is in person name context (followed by さん/くん/etc.)
                if (matchIndex == -1 &&
                    (!textsWithNonNameEntry.Contains(wordInfo.Text) || wordInfo.IsPersonNameContext))
                    matchIndex = fallbackIndex;
            }

            if (matchIndex < 0) return null;

            var foundWord = wordList[matchIndex];
            wordList.RemoveAt(matchIndex);
            if (wordList.Count == 0)
                wordsByText.Remove(wordInfo.Text);

            return foundWord;
        }

        var keptForms = new HashSet<(int, byte)>(words.Length);
        foreach (var word in words)
            keptForms.Add((word.WordId, word.ReadingIndex));

        // Pre-filter sentences with insufficient character diversity
        var validSentences = new HashSet<SentenceInfo>(); 
        for (int i = 0; i < sentences.Count; i++)
        {
            var sentence = sentences[i];
            var distinctChars = new HashSet<char>();
            foreach (var (wordInfo, _, _) in sentence.Words)
            {
                foreach (char c in wordInfo.Text)
                {
                    distinctChars.Add(c);
                    if (distinctChars.Count >= 6) break;
                }

                if (distinctChars.Count >= 6) break;
            }

            if (distinctChars.Count >= 6 && !(subtitleSpeech && EndsInOpenParticle(sentence)))
            {
                validSentences.Add(sentence);
            }
        }

        // Create position lookup for valid sentences only
        var sentencePositions = new Dictionary<SentenceInfo, int>();
        for (int i = 0; i < sentences.Count; i++)
        {
            if (validSentences.Contains(sentences[i]))
            {
                sentencePositions[sentences[i]] = i;
            }
        }

        // Group words by text for O(1) lookup instead of linear search
        var wordsByText = new Dictionary<string, List<DeckWord>>();
        foreach (var word in words)
        {
            if (!wordsByText.ContainsKey(word.OriginalText))
            {
                wordsByText[word.OriginalText] = new List<DeckWord>();
            }

            wordsByText[word.OriginalText].Add(new DeckWord
                                               {
                                                   WordId = word.WordId, ReadingIndex = word.ReadingIndex,
                                                   OriginalText = word.OriginalText, PartsOfSpeech = word.PartsOfSpeech,
                                                   SudachiReading = word.SudachiReading,
                                                   SudachiPartOfSpeech = word.SudachiPartOfSpeech
                                               });
        }

        // Track surface texts that have at least one non-Name DeckWord. When a text has both
        // Name and non-Name entries (e.g. 深雪 = name + deep snow), the name-like POS fallback
        // should only match tokens in person name context — otherwise the Name entry steals
        // example sentences from the Noun entry after it's consumed.
        var textsWithNonNameEntry = new HashSet<string>();
        foreach (var word in words)
        {
            if (word.PartsOfSpeech.Any(p => p is not (PartOfSpeech.Name or PartOfSpeech.Unknown)))
                textsWithNonNameEntry.Add(word.OriginalText);
        }

        var exampleSentences = new List<ExampleSentence>();
        var usedSentences = new HashSet<SentenceInfo>();

        foreach (var pass in subtitleSpeech ? SubtitlePasses : ProsePasses)
        {
            // Only consider sentences from the first X% of the text
            int maxPosition = (int)(sentences.Count * pass.Percentage);

            // Pre-filter and sort sentences for this pass
            var candidateSentences = new List<SentenceInfo>();
            foreach (var sentence in validSentences)
            {
                if (!usedSentences.Contains(sentence) &&
                    sentence.Text.Length >= pass.MinLength &&
                    sentence.Text.Length <= pass.MaxLength &&
                    sentencePositions[sentence] < maxPosition)
                {
                    candidateSentences.Add(sentence);
                }
            }

            // Sort by length descending
            candidateSentences.Sort((a, b) => b.Text.Length.CompareTo(a.Text.Length));

            for (int i = 0; i < candidateSentences.Count; i++)
            {
                var sentence = candidateSentences[i];
                var trimmedText = NormalizeTrailingPunctuation(sentence.Text).Trim();
                int leadingTrim = sentence.Text.Length - sentence.Text.AsSpan().TrimStart().Length;

                int closingTrim = 0;
                while (closingTrim < trimmedText.Length && ClosingSigns.Contains(trimmedText[closingTrim]))
                    closingTrim++;
                if (closingTrim > 0)
                {
                    trimmedText = trimmedText[closingTrim..];
                    leadingTrim += closingTrim;
                }

                var balancedText = BalanceBrackets(trimmedText, out int bracketPrepend);

                var picks = new List<(int Position, int WordId, byte ReadingIndex)>();
                var tokens = new List<SentenceToken>(sentence.Words.Count);

                foreach (var (wordInfo, position, length) in sentence.Words)
                {
                    int textPosition = position - leadingTrim + bracketPrepend;
                    var foundWord = TakeTargetWord(wordInfo, wordsByText, textsWithNonNameEntry);

                    if (foundWord != null)
                        picks.Add((Math.Clamp(textPosition, 0, 255), foundWord.WordId, foundWord.ReadingIndex));

                    // A pick carries the deck word it was matched to, which the fallback matching can resolve differently.
                    var form = foundWord != null ? (foundWord.WordId, foundWord.ReadingIndex) : wordInfo.KeptForm;
                    if (form is not { } f || (foundWord == null && !keptForms.Contains(f))) continue;
                    if (textPosition + length > balancedText.Length ||
                        !ExampleSentenceTokens.CanEncode(f.WordId, textPosition, length)) continue;

                    tokens.Add(new SentenceToken(f.WordId, f.ReadingIndex, (byte)textPosition, (byte)length,
                                                 IsTarget: foundWord != null,
                                                 IsFunctionWord: wordInfo.PartOfSpeech is PartOfSpeech.Particle or PartOfSpeech.Auxiliary));
                }

                if (picks.Count > 0)
                {
                    exampleSentences.Add(new ExampleSentence
                    {
                        Text = balancedText,
                        Position = sentencePositions[sentence],
                        Difficulty = SentenceDifficultyScorer.Score(sentence, picks, formFreqRanks, wordFreqRanks),
                        Tokens = ExampleSentenceTokens.Encode(tokens),
                        WordKeys = ExampleSentenceTokens.WordKeys(tokens, Random.Shared.Next(ExampleSentenceTokens.FineBucketCount))
                    });
                }

                usedSentences.Add(sentence);

                // Early exit if no more words available
                if (wordsByText.Count == 0)
                {
                    return exampleSentences;
                }
            }

            // Early exit if no more words available
            if (wordsByText.Count == 0)
            {
                break;
            }
        }

        return exampleSentences;
    }

    private static readonly HashSet<char> ClosingSigns =
        ['〉', '》', '】', '〕', '）', ')', '」', '』', '｝', '}'];

    private static readonly (char open, char close)[] BracketPairs =
        [('「', '」'), ('『', '』'), ('（', '）'), ('(', ')')];

    private static string BalanceBrackets(string text, out int prepended)
    {
        prepended = 0;
        foreach (var (open, close) in BracketPairs)
        {
            int balance = 0;
            foreach (char c in text)
            {
                if (c == open) balance++;
                else if (c == close) balance--;
            }

            if (balance > 0)
                text += new string(close, balance);
            else if (balance < 0)
            {
                prepended += -balance;
                text = new string(open, -balance) + text;
            }
        }

        return text;
    }

    internal static string NormalizeTrailingPunctuation(string text)
    {
        if (text.Length < 2) return text;

        int end = text.Length;
        while (end >= 2 && text[end - 1] == text[end - 2] &&
               text[end - 1] is '。' or '！' or '？' or '!' or '?' or '.' or '…' or '‥')
            end--;

        text = end == text.Length ? text : text[..end];
        // The parser writes a line-final … as 。, which lands in front of an ！？ that followed it (……？ → 。？).
        return PeriodBeforeFinalMark().Replace(text, "");
    }

    [GeneratedRegex(@"。(?=[！？!?]+[」』）]*$)")]
    private static partial Regex PeriodBeforeFinalMark();

    /// <summary>A subtitle sentence ending in a case, binding or adverbial particle is usually half of a sentence split across cues.</summary>
    private static bool EndsInOpenParticle(SentenceInfo sentence)
    {
        for (int i = sentence.Words.Count - 1; i >= 0; i--)
        {
            var word = sentence.Words[i].word;
            if (word.PartOfSpeech is PartOfSpeech.SupplementarySymbol or PartOfSpeech.Symbol or PartOfSpeech.BlankSpace)
                continue;

            return word.PartOfSpeech == PartOfSpeech.Particle &&
                   word.PartOfSpeechSection1 is PartOfSpeechSection.CaseMarkingParticle or PartOfSpeechSection.BindingParticle
                       or PartOfSpeechSection.AdverbialParticle;
        }

        return false;
    }
}
