using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Helpers;

/// <summary>
/// Parses many short texts in one parser call and hands back the words of each. Sudachi context does not
/// leak across the stop token, and one call beats hundreds for subtitle-sized lines.
/// </summary>
public static class ParagraphParser
{
    private const string StopToken = "\n|\n";

    public static async Task<List<List<DeckWord>>> ParseAsync(IDbContextFactory<JitenDbContext> contextFactory, string[] texts)
    {
        var parsedParagraphs = new List<List<DeckWord>>();
        if (texts.Length == 0)
            return parsedParagraphs;

        var combinedText = string.Join(StopToken, texts);

        // Spaces become stop tokens for the parser only; the original text keeps its positions for the mapping below
        var parsedText = combinedText.Replace(" ", StopToken);
        var allParsedWords = await Parser.Parser.ParseText(contextFactory, parsedText, preserveStopToken: true);

        var paragraphOffsets = new int[texts.Length];
        var currentOffset = 0;
        for (var i = 0; i < texts.Length; i++)
        {
            paragraphOffsets[i] = currentOffset;
            currentOffset += texts[i].Length + StopToken.Length;
        }

        var wordIndex = 0;
        var positionInCombined = 0;
        var positionCache = new Dictionary<int, int>();
        for (var i = 0; i < texts.Length; i++)
        {
            var paragraphWords = new List<DeckWord>();
            var paragraphEnd = paragraphOffsets[i] + texts[i].Length;

            while (wordIndex < allParsedWords.Count)
            {
                var word = allParsedWords[wordIndex];

                var searchFrom = Math.Max(positionInCombined, paragraphOffsets[i]);

                int wordPosition;
                if (positionCache.Remove(wordIndex, out var cached) && cached >= searchFrom)
                {
                    wordPosition = cached;
                }
                else
                {
                    (wordPosition, _) = TokenPositionHelper.FindTokenInSource(combinedText, word.OriginalText, searchFrom);
                }

                if (wordPosition < 0)
                {
                    wordIndex++;
                    continue;
                }

                var (_, wordSourceLength) = TokenPositionHelper.FindTokenInSource(combinedText, word.OriginalText, wordPosition);

                // A match far ahead is suspect when one of the next few words sits closer
                if (wordPosition - searchFrom > 10)
                {
                    var foundCloserWord = false;
                    for (var lookAhead = 1; lookAhead <= 5 && wordIndex + lookAhead < allParsedWords.Count; lookAhead++)
                    {
                        var futureIdx = wordIndex + lookAhead;
                        int futurePos;
                        if (positionCache.TryGetValue(futureIdx, out var cachedFuture) && cachedFuture >= searchFrom)
                        {
                            futurePos = cachedFuture;
                        }
                        else
                        {
                            var futureWord = allParsedWords[futureIdx];
                            (futurePos, _) = TokenPositionHelper.FindTokenInSource(combinedText, futureWord.OriginalText, searchFrom);
                            if (futurePos >= 0)
                                positionCache[futureIdx] = futurePos;
                        }

                        if (futurePos >= 0 && futurePos < wordPosition)
                        {
                            foundCloserWord = true;
                            break;
                        }
                    }

                    if (foundCloserWord)
                    {
                        wordIndex++;
                        continue;
                    }
                }

                if (wordPosition >= paragraphEnd)
                    break;

                if (wordPosition >= paragraphOffsets[i])
                {
                    if (wordSourceLength != word.OriginalText.Length)
                        word.OriginalText = combinedText.Substring(wordPosition, wordSourceLength);
                    paragraphWords.Add(word);
                    positionInCombined = wordPosition + wordSourceLength;
                }

                wordIndex++;
            }

            parsedParagraphs.Add(paragraphWords);
        }

        return parsedParagraphs;
    }
}
