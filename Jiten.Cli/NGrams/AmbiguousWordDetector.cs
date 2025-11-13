using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Cli.NGrams;

public class AmbiguousWordDetector
{
    private readonly JitenDbContext _dbContext;

    public AmbiguousWordDetector(DbContextOptions<JitenDbContext> dbOptions)
    {
        _dbContext = new JitenDbContext(dbOptions);
    }

    /// <summary>
    /// Identify all ambiguous words that need n-gram precomputation
    /// </summary>
    public async Task<List<AmbiguousWordInfo>> IdentifyAmbiguousWordsAsync(
        AmbiguityConfig config,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Identifying ambiguous words...");

        var ambiguousWords = new List<AmbiguousWordInfo>();

        // Query 1: Words with multiple definitions
        // var multiDefinitionWords = await _dbContext.Set<JmDictWord>()
        //     .Where(w => w.Definitions.Count >= config.MinDefinitionsForAmbiguity)
        //     .Select(w => new
        //     {
        //         w.WordId,
        //         DefinitionCount = w.Definitions.Count,
        //         w.Readings,
        //         w.PartsOfSpeech
        //     })
        //     .ToListAsync(cancellationToken);
        //
        // foreach (var word in multiDefinitionWords)
        // {
        //     ambiguousWords.Add(new AmbiguousWordInfo
        //     {
        //         WordId = word.WordId,
        //         AmbiguityReason = AmbiguityReason.MultipleDefinitions,
        //         AmbiguityScore = CalculateDefinitionAmbiguity(word.DefinitionCount),
        //         DefinitionCount = word.DefinitionCount
        //     });
        // }

        // Query 2: Homonyms (same reading, different kanji/meanings)
        var homonyms = await FindHomonymsAsync(config, cancellationToken);
        ambiguousWords.AddRange(homonyms);

        // Query 3: Words with multiple parts of speech
        // var multiPosWords = await FindMultiPosWordsAsync(config, cancellationToken);
        // ambiguousWords.AddRange(multiPosWords);

        // Query 4: Known problematic words (manual list)
        if (config.IncludeKnownProblematic)
        {
            var problematic = GetKnownProblematicWords();
            ambiguousWords.AddRange(problematic);
        }

        // Deduplicate and sort by ambiguity score
        var deduplicated = ambiguousWords
                           .GroupBy(w => w.WordId)
                           .Select(g => new AmbiguousWordInfo
                                        {
                                            WordId = g.Key, AmbiguityReason = g.First().AmbiguityReason,
                                            AmbiguityScore = g.Max(w => w.AmbiguityScore), DefinitionCount = g.First().DefinitionCount,
                                            HomonymCount = g.First().HomonymCount
                                        })
                           .Where(w => w.AmbiguityScore >= config.MinAmbiguityScore)
                           .OrderByDescending(w => w.AmbiguityScore)
                           .ToList();

        Console.WriteLine(
                          $"Identified {deduplicated.Count} ambiguous words (threshold: {config.MinAmbiguityScore})");

        return deduplicated.Take(10).ToList();
    }

    private async Task<List<AmbiguousWordInfo>> FindHomonymsAsync(
        AmbiguityConfig config,
        CancellationToken cancellationToken)
    {
        // Find words that share readings
        // First, get all words with their readings
        // We need to pull this to client since EF can't translate the complex grouping
        var wordsWithReadings = await _dbContext.Set<JmDictWord>()
                                                .Select(w => new { w.WordId, w.Readings })
                                                .ToListAsync(cancellationToken);

        // Flatten words to (WordId, Reading) pairs
        var wordReadingPairs = wordsWithReadings
                               .SelectMany(w => w.Readings.Select(r => new { w.WordId, Reading = r }))
                               .ToList();

        // Group by reading and find readings with multiple words
        var homonymGroups = wordReadingPairs
                            .GroupBy(x => x.Reading)
                            .Where(g => g.Select(x => x.WordId).Distinct().Count() >= config.MinHomonymsForAmbiguity)
                            .ToList();

        var result = new List<AmbiguousWordInfo>();

        foreach (var group in homonymGroups)
        {
            var wordIds = group.Select(x => x.WordId).Distinct().ToList();
            var homonymCount = wordIds.Count;

            foreach (var wordId in wordIds)
            {
                result.Add(new AmbiguousWordInfo
                           {
                               WordId = wordId, AmbiguityReason = AmbiguityReason.Homonym,
                               AmbiguityScore = CalculateHomonymAmbiguity(homonymCount), HomonymCount = homonymCount - 1
                           });
            }
        }

        Console.WriteLine($"Found {homonymGroups.Count} homonym groups");


        return result;
    }

    private async Task<List<AmbiguousWordInfo>> FindMultiPosWordsAsync(
        AmbiguityConfig config,
        CancellationToken cancellationToken)
    {
        // Pull to client since we need to count array length
        var multiPosWords = await _dbContext.Set<JmDictWord>()
                                            .Select(w => new { w.WordId, w.PartsOfSpeech })
                                            .ToListAsync(cancellationToken);

        var result = multiPosWords
                     .Where(w => w.PartsOfSpeech.Count >= config.MinPartsOfSpeechForAmbiguity)
                     .Select(w => new AmbiguousWordInfo
                                  {
                                      WordId = w.WordId, AmbiguityReason = AmbiguityReason.MultiplePOS,
                                      AmbiguityScore = CalculatePosAmbiguity(w.PartsOfSpeech.Count)
                                  })
                     .ToList();

        return result;
    }

    private List<AmbiguousWordInfo> GetKnownProblematicWords()
    {
        // Known highly ambiguous words in Japanese
        var problematicWordIds = new[]
                                 {
                                     1404930, // 橋 (bridge)
                                     1405010, // 箸 (chopsticks)
                                     1404950, // 端 (edge)
                                     1581610, // 本 (book)
                                     1581520, // 本 (real/true)
                                     1374390, // 川 (river/surname)
                                     1595000, // 雨 (rain)
                                     1594990, // 飴 (candy)
                                     // Add more known ambiguous words
                                 };

        return problematicWordIds.Select(id => new AmbiguousWordInfo
                                               {
                                                   WordId = id, AmbiguityReason = AmbiguityReason.KnownProblematic, AmbiguityScore = 1.0f
                                               }).ToList();
    }

    private float CalculateDefinitionAmbiguity(int definitionCount)
    {
        // More definitions = more ambiguous
        // Scale: 2 defs = 0.3, 5 defs = 0.6, 10+ defs = 1.0
        return Math.Min(definitionCount / 10f, 1f);
    }

    private float CalculateHomonymAmbiguity(int homonymCount)
    {
        // More homonyms = more ambiguous
        // Scale: 2 words = 0.4, 5 words = 0.8, 10+ words = 1.0
        return Math.Min(homonymCount / 10f, 1f);
    }

    private float CalculatePosAmbiguity(int posCount)
    {
        // Multiple POS = moderately ambiguous
        // Scale: 2 POS = 0.3, 3 POS = 0.5, 5+ POS = 0.8
        return Math.Min(posCount / 6f, 0.8f);
    }
}

public class AmbiguousWordInfo
{
    public int WordId { get; set; }
    public AmbiguityReason AmbiguityReason { get; set; }
    public float AmbiguityScore { get; set; }
    public int DefinitionCount { get; set; }
    public int HomonymCount { get; set; }
}

public enum AmbiguityReason
{
    MultipleDefinitions,
    Homonym,
    MultiplePOS,
    KnownProblematic
}

public class AmbiguityConfig
{
    public int MinDefinitionsForAmbiguity { get; set; } = 3;
    public int MinHomonymsForAmbiguity { get; set; } = 2;
    public int MinPartsOfSpeechForAmbiguity { get; set; } = 2;
    public float MinAmbiguityScore { get; set; } = 0.3f;
    public bool IncludeKnownProblematic { get; set; } = true;
}