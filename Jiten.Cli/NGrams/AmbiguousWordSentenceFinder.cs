using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jiten.Cli.NGrams;

public class AmbiguousWordSentenceFinder
{
    private readonly JitenDbContext _dbContext;

    public AmbiguousWordSentenceFinder(DbContextOptions<JitenDbContext> dbOptions)
    {
        _dbContext = new JitenDbContext(dbOptions);
    }
    
    /// <summary>
    /// Find all ExampleSentences that contain ambiguous words
    /// </summary>
    public async Task<Dictionary<int, List<SentenceOccurrence>>> FindSentencesForAmbiguousWordsAsync(
        List<int> ambiguousWordIds,
        SentenceFinderConfig config,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine(
                          $"Finding sentences for {ambiguousWordIds.Count} ambiguous words");

        var result = new Dictionary<int, List<SentenceOccurrence>>();

        // Process in batches to avoid overwhelming the database
        var batchSize = 1000;
        for (int i = 0; i < ambiguousWordIds.Count; i += batchSize)
        {
            var batch = ambiguousWordIds.Skip(i).Take(batchSize).ToList();

            // Query ExampleSentenceWords to find sentences containing these words
            var sentenceWords = await _dbContext.Set<ExampleSentenceWord>()
                                                .Where(esw => batch.Contains(esw.WordId))
                                                .Include(esw => esw.ExampleSentence)
                                                .Select(esw => new
                                                               {
                                                                   esw.WordId, esw.ReadingIndex, esw.ExampleSentenceId, esw.Position,
                                                                   esw.Length, SentenceText = esw.ExampleSentence!.Text,
                                                                   DeckId = esw.ExampleSentence.DeckId
                                                               })
                                                .ToListAsync(cancellationToken);

            // Group by WordId
            var grouped = sentenceWords.GroupBy(sw => sw.WordId);

            foreach (var group in grouped)
            {
                var wordId = group.Key;

                var occurrences = group
                                  .Select(sw => new SentenceOccurrence
                                                {
                                                    ExampleSentenceId = sw.ExampleSentenceId, SentenceText = sw.SentenceText,
                                                    WordPosition = sw.Position, WordLength = sw.Length, ReadingIndex = sw.ReadingIndex,
                                                    DeckId = sw.DeckId
                                                })
                                  .Take(config.MaxSentencesPerWord) // Limit per word
                                  .ToList();

                result[wordId] = occurrences;
            }

            Console.WriteLine(
                              $"Processed batch {Math.Min(i + batchSize, ambiguousWordIds.Count)}/{ambiguousWordIds.Count}");
        }

        var totalSentences = result.Values.Sum(list => list.Count);
        Console.WriteLine(
                          $"Found {totalSentences} total sentence occurrences for {result.Count} words");

        return result;
    }

    /// <summary>
    /// Find sentences for a single word
    /// </summary>
    public async Task<List<SentenceOccurrence>> FindSentencesForWordAsync(
        int wordId,
        SentenceFinderConfig config,
        CancellationToken cancellationToken = default)
    {
        var sentences = await _dbContext.Set<ExampleSentenceWord>()
                                        .Where(esw => esw.WordId == wordId)
                                        .Include(esw => esw.ExampleSentence)
                                        .OrderBy(esw => esw.ExampleSentenceId)
                                        .Take(config.MaxSentencesPerWord)
                                        .Select(esw => new SentenceOccurrence
                                                       {
                                                           ExampleSentenceId = esw.ExampleSentenceId,
                                                           SentenceText = esw.ExampleSentence!.Text, WordPosition = esw.Position,
                                                           WordLength = esw.Length, ReadingIndex = esw.ReadingIndex,
                                                           DeckId = esw.ExampleSentence.DeckId
                                                       })
                                        .ToListAsync(cancellationToken);

        return sentences;
    }
}

public class SentenceOccurrence
{
    public int ExampleSentenceId { get; set; }
    public string SentenceText { get; set; } = string.Empty;
    public byte WordPosition { get; set; }
    public byte WordLength { get; set; }
    public byte ReadingIndex { get; set; }
    public int DeckId { get; set; }
}

public class SentenceFinderConfig
{
    /// <summary>
    /// Maximum sentences to process per word
    /// </summary>
    public int MaxSentencesPerWord { get; set; } = 1000;

    /// <summary>
    /// Only include sentences from high-quality decks
    /// </summary>
    public bool OnlyHighQualityDecks { get; set; } = false;

    /// <summary>
    /// Minimum sentence length (characters)
    /// </summary>
    public int MinSentenceLength { get; set; } = 5;
}