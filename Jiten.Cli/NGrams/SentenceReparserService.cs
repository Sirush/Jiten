using Jiten.Parser;
using Microsoft.Extensions.Logging;

namespace Jiten.Cli.NGrams;

public class SentenceReparserService
{
    private readonly MorphologicalAnalyser _analyser = new();

    /// <summary>
    /// Re-parse a sentence to get full morpheme breakdown
    /// </summary>
    public async Task<ParsedSentenceContext> ReparseAndExtractContextAsync(
        SentenceOccurrence sentence,
        int targetWordId,
        NgramExtractionConfig config)
    {
        // Parse the sentence text to get all morphemes
        var morphemes = (await _analyser.Parse(sentence.SentenceText))[0].Words.Select(w => w.word).ToList();

        if (morphemes == null || morphemes.Count == 0)
        {
            throw new Exception($"Failed to parse sentence: {sentence.SentenceText}");
        }

        // Find the target word in the parsed morphemes
        var targetMorphemeIndex = FindTargetWordInMorphemes(
                                                            morphemes,
                                                            sentence.WordPosition,
                                                            sentence.WordLength);

        if (targetMorphemeIndex == -1)
        {
            Console.WriteLine(
                              $"Could not find target word at position {sentence.WordPosition} in sentence {sentence.ExampleSentenceId}");

            return null!;
        }

        return new ParsedSentenceContext
               {
                   ExampleSentenceId = sentence.ExampleSentenceId, SentenceText = sentence.SentenceText, Morphemes = morphemes,
                   TargetMorphemeIndex = targetMorphemeIndex, TargetWordId = targetWordId, ReadingIndex = sentence.ReadingIndex
               };
    }

    /// <summary>
    /// Find the morpheme that corresponds to the target word position
    /// </summary>
    private int FindTargetWordInMorphemes(
        List<WordInfo> morphemes,
        byte characterPosition,
        byte characterLength)
    {
        int currentPosition = 0;

        for (int i = 0; i < morphemes.Count; i++)
        {
            var morpheme = morphemes[i];
            var morphemeLength = morpheme.Text.Length;

            // Check if this morpheme overlaps with the target position
            if (currentPosition <= characterPosition &&
                currentPosition + morphemeLength > characterPosition)
            {
                // Found the morpheme containing the target word
                return i;
            }

            currentPosition += morphemeLength;
        }

        return -1; // Not found
    }

    /// <summary>
    /// Batch re-parse multiple sentences
    /// </summary>
    public async Task<List<ParsedSentenceContext>> ReparseSentencesAsync(
        List<SentenceOccurrence> sentences,
        int targetWordId,
        NgramExtractionConfig config,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ParsedSentenceContext>();

        foreach (var sentence in sentences)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var parsed = await ReparseAndExtractContextAsync(
                                                                 sentence,
                                                                 targetWordId,
                                                                 config);

                if (parsed != null)
                {
                    results.Add(parsed);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error re-parsing sentence {sentence.ExampleSentenceId}");
            }
        }

        return results;
    }
}

public class ParsedSentenceContext
{
    public int ExampleSentenceId { get; set; }
    public string SentenceText { get; set; } = string.Empty;
    public List<WordInfo> Morphemes { get; set; } = new();
    public int TargetMorphemeIndex { get; set; }
    public int TargetWordId { get; set; }
    public byte ReadingIndex { get; set; }
}