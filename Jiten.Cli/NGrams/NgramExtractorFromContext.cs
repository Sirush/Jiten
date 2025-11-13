using Jiten.Core.Data;
using Jiten.Parser;
using Microsoft.Extensions.Logging;

namespace Jiten.Cli.NGrams;

public class NgramExtractorFromContext
{
    /// <summary>
    /// Extract n-grams from a parsed sentence context
    /// </summary>
    public List<ExtractedNgram> ExtractNgramsFromParsedContext(
        ParsedSentenceContext context,
        NgramExtractionConfig config)
    {
        var ngrams = new List<ExtractedNgram>();
        var targetIndex = context.TargetMorphemeIndex;
        var morphemes = context.Morphemes;

        // Extract n-grams with different window configurations
        foreach (var windowSize in config.WindowSizes)
        {
            // Generate all before/after combinations for this window size
            for (int tokensBefore = 0; tokensBefore < windowSize; tokensBefore++)
            {
                int tokensAfter = windowSize - tokensBefore - 1;

                if (tokensAfter < 0) continue;

                // Skip if outside configured limits
                if (tokensBefore > config.MaxTokensBefore ||
                    tokensAfter > config.MaxTokensAfter)
                {
                    continue;
                }

                // Extract this specific n-gram
                var ngram = ExtractNgram(
                                         morphemes,
                                         targetIndex,
                                         tokensBefore,
                                         tokensAfter,
                                         context);

                if (ngram != null)
                {
                    ngrams.Add(ngram.Value);
                }
            }
        }

        return ngrams;
    }

    private ExtractedNgram? ExtractNgram(
        List<WordInfo> morphemes,
        int targetIndex,
        int tokensBefore,
        int tokensAfter,
        ParsedSentenceContext context)
    {
        // Check boundaries
        int startIndex = targetIndex - tokensBefore;
        int endIndex = targetIndex + tokensAfter;

        if (startIndex < 0 || endIndex >= morphemes.Count)
        {
            return null;
        }

        // Check for sentence boundaries in the window
        for (int i = startIndex; i <= endIndex; i++)
        {
            if (i != targetIndex && IsSentenceBoundary(morphemes[i]))
            {
                return null; // Don't cross sentence boundaries
            }
        }

        // Extract context before target
        var beforeTokens = new List<string>();
        for (int i = startIndex; i < targetIndex; i++)
        {
            beforeTokens.Add(morphemes[i].Text);
        }

        // Extract context after target
        var afterTokens = new List<string>();
        for (int i = targetIndex + 1; i <= endIndex; i++)
        {
            afterTokens.Add(morphemes[i].Text);
        }

        // Get target word surface
        var targetSurface = morphemes[targetIndex].Text;

        // Build strings
        var contextBefore = string.Join("", beforeTokens);
        var contextAfter = string.Join("", afterTokens);
        var fullContext = contextBefore + targetSurface + contextAfter;

        return new ExtractedNgram
               {
                   WordId = context.TargetWordId, ReadingIndex = context.ReadingIndex, ContextBefore = contextBefore,
                   ContextAfter = contextAfter, ContextSize = (short)(tokensBefore + 1 + tokensAfter), TokensBefore = (short)tokensBefore,
                   TokensAfter = (short)tokensAfter, FullContext = fullContext, ExampleSentenceId = context.ExampleSentenceId,
                   TargetMorphemeIndex = targetIndex, TargetSurface = targetSurface
               };
    }

    private bool IsSentenceBoundary(WordInfo word)
    {
        return word.Text is "。" or "！" or "？" or "」" or "』";
    }
}

public struct ExtractedNgram
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public string ContextBefore { get; set; }
    public string ContextAfter { get; set; }
    public short ContextSize { get; set; }
    public short TokensBefore { get; set; }
    public short TokensAfter { get; set; }
    public string FullContext { get; set; }
    public int ExampleSentenceId { get; set; }
    public int TargetMorphemeIndex { get; set; }
    public string TargetSurface { get; set; }
}