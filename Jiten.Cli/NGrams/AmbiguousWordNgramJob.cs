using Jiten.Cli.NGrams;
using Jiten.Core;
using Microsoft.EntityFrameworkCore;

public class AmbiguousWordNgramJob
{
    private readonly AmbiguousWordDetector _ambiguityDetector;
    private readonly AmbiguousWordSentenceFinder _sentenceFinder;
    private readonly SentenceReparserService _reparser;
    private readonly NgramExtractorFromContext _ngramExtractor;
    private readonly JitenDbContext _dbContext;

    public AmbiguousWordNgramJob(DbContextOptions<JitenDbContext> dbOptions)
    {
        _dbContext = new JitenDbContext(dbOptions);
        _ambiguityDetector = new AmbiguousWordDetector(dbOptions);
        _sentenceFinder = new AmbiguousWordSentenceFinder(dbOptions);
        _reparser = new SentenceReparserService();
        _ngramExtractor = new NgramExtractorFromContext();
    }
    
    /// <summary>
    /// Complete pipeline: Identify ambiguous words → Find sentences → Re-parse → Extract n-grams
    /// </summary>
    public async Task ProcessAmbiguousWordsAsync(
        AmbiguousWordProcessingConfig config,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Starting ambiguous word n-gram extraction pipeline");

        // Step 1: Identify ambiguous words
        Console.WriteLine("Step 1: Identifying ambiguous words");
        var ambiguousWords = await _ambiguityDetector.IdentifyAmbiguousWordsAsync(
                                                                                  config.AmbiguityConfig,
                                                                                  cancellationToken);

        Console.WriteLine("Found {0} ambiguous words", ambiguousWords.Count);

        // Optional: Filter by word IDs if provided
        if (config.SpecificWordIds != null && config.SpecificWordIds.Any())
        {
            ambiguousWords = ambiguousWords
                             .Where(w => config.SpecificWordIds.Contains(w.WordId))
                             .ToList();

            Console.WriteLine(
                              $"Filtered to {ambiguousWords.Count} specific words");
        }

        // Create lookup dictionary for ambiguity scores
        var ambiguityScoreLookup = ambiguousWords
            .ToDictionary(w => w.WordId, w => w.AmbiguityScore);

        // Step 2: Find sentences for each ambiguous word
        Console.WriteLine("Step 2: Finding example sentences");
        var wordToSentences = await _sentenceFinder.FindSentencesForAmbiguousWordsAsync(
                                                                                        ambiguousWords.Select(w => w.WordId).ToList(),
                                                                                        config.SentenceFinderConfig,
                                                                                        cancellationToken);

        var totalSentences = wordToSentences.Values.Sum(list => list.Count);
        Console.WriteLine(
                          $"Found {totalSentences} sentences for {wordToSentences.Count} words");

        // Step 3: Process each word
        var processedWords = 0;
        var totalNgrams = 0;

        foreach (var (wordId, sentences) in wordToSentences)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                Console.WriteLine(
                                  $"Processing word {wordId} ({processedWords + 1}/{wordToSentences.Count}) with {sentences.Count} sentences");

                var wordAmbiguityScore = ambiguityScoreLookup.GetValueOrDefault(wordId, 0.5f);

                var ngramsForWord = await ProcessSingleWordAsync(
                                                                 wordId,
                                                                 wordAmbiguityScore,
                                                                 sentences,
                                                                 config,
                                                                 cancellationToken);

                totalNgrams += ngramsForWord;
                processedWords++;

                if (processedWords % 10 == 0)
                {
                    Console.WriteLine(
                                      $"Progress: {processedWords}/{wordToSentences.Count} words, {totalNgrams} n-grams extracted");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                                  $"Error processing word {wordId}");
            }
        }

        Console.WriteLine(
                          $"Pipeline completed: Processed {processedWords} words, extracted {totalNgrams} n-grams");

        // Update statistics
        await UpdateNgramStatisticsAsync(cancellationToken);
    }

    /// <summary>
    /// Process a single ambiguous word: Re-parse sentences and extract n-grams
    /// </summary>
    private async Task<int> ProcessSingleWordAsync(
        int wordId,
        float wordAmbiguityScore,
        List<SentenceOccurrence> sentences,
        AmbiguousWordProcessingConfig config,
        CancellationToken cancellationToken)
    {
        // Step 3a: Re-parse sentences to get morphemes
        var parsedContexts = await _reparser.ReparseSentencesAsync(
                                                                   sentences,
                                                                   wordId,
                                                                   config.NgramExtractionConfig,
                                                                   cancellationToken);

        if (parsedContexts.Count == 0)
        {
            Console.WriteLine($"No valid parsed contexts for word {wordId}");
            return 0;
        }

        // Step 3b: Extract n-grams from each parsed context
        var allNgrams = new List<ExtractedNgram>();

        foreach (var context in parsedContexts)
        {
            var ngrams = _ngramExtractor.ExtractNgramsFromParsedContext(
                                                                        context,
                                                                        config.NgramExtractionConfig);

            allNgrams.AddRange(ngrams);
        }

        if (allNgrams.Count == 0)
        {
            Console.WriteLine($"No n-grams extracted for word {wordId}");
            return 0;
        }

        // Step 3c: Group duplicates and count occurrences
        var groupedNgrams = allNgrams
                            .GroupBy(n => new { n.WordId, n.ReadingIndex, n.FullContext, n.TokensBefore, n.TokensAfter })
                            .Select(g => new NgramGroup
                                         {
                                             Ngram = g.First(), Occurrences = g.Count(),
                                             Sources = g.Select(n => new NgramSourceInfo
                                                                     {
                                                                         ExampleSentenceId = n.ExampleSentenceId,
                                                                         TargetMorphemeIndex = n.TargetMorphemeIndex
                                                                     }).ToList()
                                         })
                            .ToList();

        // Step 3d: Calculate significance scores for each n-gram
        var ngramWithScores = groupedNgrams
                              .Select(g => new NgramWithScore
                                           {
                                               Ngram = g.Ngram, Occurrences = g.Occurrences, Sources = g.Sources, SignificanceScore =
                                                   CalculateNgramSignificance(
                                                                              g.Ngram,
                                                                              g.Occurrences,
                                                                              wordAmbiguityScore)
                                           })
                              .ToList();

        // Step 3e: Filter by significance and take top N
        var significantNgrams = ngramWithScores
                                .Where(g => g.SignificanceScore >= config.MinSignificanceScore)
                                .OrderByDescending(g => g.SignificanceScore)
                                .Take(config.MaxNgramsPerWord)
                                .ToList();

        // Step 3f: Store in database
        await StoreNgramsAsync(significantNgrams, cancellationToken);

        Console.WriteLine(
                          $"Word {wordId}: Extracted {allNgrams.Count} n-grams, kept {significantNgrams.Count} significant ones (ambiguity: {wordAmbiguityScore:F2})");

        return significantNgrams.Count;
    }

    /// <summary>
    /// Calculate significance score for an n-gram based on word ambiguity and n-gram properties
    /// </summary>
    private float CalculateNgramSignificance(
        ExtractedNgram ngram,
        int occurrences,
        float wordAmbiguityScore)
    {
        // Factor 1: Word ambiguity (inherited from word) - 40% weight
        var ambiguityComponent = wordAmbiguityScore * 0.40f;

        // Factor 2: Frequency score (how often this n-gram appears) - 25% weight
        var frequencyScore = CalculateFrequencyScore(occurrences);
        var frequencyComponent = frequencyScore * 0.25f;

        // Factor 3: Context quality (length, diversity) - 20% weight
        var contextQualityScore = CalculateContextQuality(ngram);
        var contextComponent = contextQualityScore * 0.20f;

        // Factor 4: Window balance (prefer balanced context) - 15% weight
        var balanceScore = CalculateWindowBalance(ngram);
        var balanceComponent = balanceScore * 0.15f;

        var totalScore = ambiguityComponent + frequencyComponent + contextComponent + balanceComponent;

        return Math.Clamp(totalScore, 0f, 1f);
    }

    /// <summary>
    /// Calculate frequency score using logarithmic scale
    /// </summary>
    private float CalculateFrequencyScore(int occurrences)
    {
        if (occurrences <= 0) return 0f;

        // Logarithmic scale:
        // 1 occurrence = 0.3
        // 5 occurrences = 0.5
        // 10 occurrences = 0.6
        // 50 occurrences = 0.8
        // 100+ occurrences = 1.0
        return Math.Min((float)Math.Log10(occurrences + 1) / 2f, 1f);
    }

    /// <summary>
    /// Calculate context quality based on length and content diversity
    /// </summary>
    private float CalculateContextQuality(ExtractedNgram ngram)
    {
        var contextLength = ngram.FullContext.Length;

        // 1. Length score (longer contexts are more informative)
        var lengthScore = Math.Min(contextLength / 15f, 1f); // Max at 15 characters

        // 2. Diversity score (unique characters ratio)
        var uniqueChars = ngram.FullContext.Distinct().Count();
        var diversityScore = contextLength > 0
            ? uniqueChars / (float)contextLength
            : 0f;

        // 3. Contains kanji bonus (more semantic information)
        var containsKanji = ngram.FullContext.Any(c => c >= 0x4E00 && c <= 0x9FAF);
        var kanjiBonus = containsKanji ? 0.2f : 0f;

        // 4. Avoid too short contexts
        if (contextLength < 2)
        {
            return 0.1f; // Very low score for contexts that are too short
        }

        var quality = (lengthScore * 0.4f) + (diversityScore * 0.4f) + kanjiBonus;
        return Math.Min(quality, 1f);
    }

    /// <summary>
    /// Calculate window balance score (prefer contexts with words both before and after)
    /// </summary>
    private float CalculateWindowBalance(ExtractedNgram ngram)
    {
        var totalTokens = ngram.TokensBefore + ngram.TokensAfter;

        if (totalTokens == 0)
        {
            return 0.3f; // Unigram (target word only) - low score
        }

        // Perfect balance = 1.0, completely unbalanced = 0.5
        var beforeRatio = ngram.TokensBefore / (float)totalTokens;
        var afterRatio = ngram.TokensAfter / (float)totalTokens;

        // Calculate how close to 50-50 split
        var balanceDeviation = Math.Abs(beforeRatio - 0.5f);
        var balanceScore = 1f - (balanceDeviation * 2f); // 0.5 deviation = 0.0 score

        // Bonus for having at least one token on each side
        var hasBothSides = ngram.TokensBefore > 0 && ngram.TokensAfter > 0;
        var bothSidesBonus = hasBothSides ? 0.2f : 0f;

        return Math.Min(balanceScore + bothSidesBonus, 1f);
    }

    /// <summary>
    /// Store n-grams in database
    /// </summary>
    private async Task StoreNgramsAsync(
        List<NgramWithScore> ngramWithScores,
        CancellationToken cancellationToken)
    {
        foreach (var item in ngramWithScores)
        {
            // Check if already exists
            var existing = await _dbContext.Set<PrecomputedNgram>()
                                           .FirstOrDefaultAsync(n =>
                                                                    n.WordId == item.Ngram.WordId &&
                                                                    n.ReadingIndex == item.Ngram.ReadingIndex &&
                                                                    n.FullContext == item.Ngram.FullContext,
                                                                cancellationToken);

            if (existing != null)
            {
                // Update existing
                existing.Occurrences += item.Occurrences;
                existing.SignificanceScore = Math.Max(existing.SignificanceScore, item.SignificanceScore);
                existing.LastUpdated = DateTimeOffset.UtcNow;

                Console.WriteLine(
                                  $"Updated existing n-gram {existing.NgramId} for word {item.Ngram.WordId}, new occurrences: {existing.Occurrences}");
            }
            else
            {
                // Create new
                var precomputedNgram = new PrecomputedNgram
                                       {
                                           WordId = item.Ngram.WordId, ReadingIndex = item.Ngram.ReadingIndex,
                                           ContextBefore = item.Ngram.ContextBefore, ContextAfter = item.Ngram.ContextAfter,
                                           ContextSize = item.Ngram.ContextSize, TokensBefore = item.Ngram.TokensBefore,
                                           TokensAfter = item.Ngram.TokensAfter, FullContext = item.Ngram.FullContext,
                                           Occurrences = item.Occurrences, SignificanceScore = item.SignificanceScore,
                                           BertEmbeddingComputed = false, LastUpdated = DateTimeOffset.UtcNow
                                       };

                _dbContext.Set<PrecomputedNgram>().Add(precomputedNgram);
                await _dbContext.SaveChangesAsync(cancellationToken);

                // Store sources
                foreach (var source in item.Sources)
                {
                    _dbContext.Set<NgramSource>().Add(new NgramSource
                                                      {
                                                          NgramId = precomputedNgram.NgramId, ExampleSentenceId = source.ExampleSentenceId,
                                                          WordPosition = (short)source.TargetMorphemeIndex
                                                      });
                }

                await _dbContext.SaveChangesAsync(cancellationToken);

                Console.WriteLine(
                                  $"Created new n-gram {precomputedNgram.NgramId} for word {item.Ngram.WordId}, significance: {item.SignificanceScore:F2}");
            }
        }
    }

    /// <summary>
    /// Update aggregate statistics for n-grams
    /// </summary>
    private async Task UpdateNgramStatisticsAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Updating n-gram statistics");

        // Aggregate statistics per word
        var stats = await _dbContext.Set<PrecomputedNgram>()
                                    .GroupBy(n => n.WordId)
                                    .Select(g => new
                                                 {
                                                     WordId = g.Key, TotalNgrams = g.Count(),
                                                     SignificantNgrams = g.Count(n => n.SignificanceScore > 0.5f),
                                                     AvgSignificanceScore = g.Average(n => n.SignificanceScore),
                                                     BertEmbeddingsComputed = g.Count(n => n.BertEmbeddingComputed),
                                                     MaxSignificance = g.Max(n => n.SignificanceScore),
                                                     TotalOccurrences = g.Sum(n => n.Occurrences)
                                                 })
                                    .ToListAsync(cancellationToken);

        foreach (var stat in stats)
        {
            var existing = await _dbContext.Set<NgramStatistics>()
                                           .FirstOrDefaultAsync(s => s.WordId == stat.WordId, cancellationToken);

            if (existing != null)
            {
                // Update existing
                existing.TotalNgrams = stat.TotalNgrams;
                existing.SignificantNgrams = stat.SignificantNgrams;
                existing.AvgSignificanceScore = stat.AvgSignificanceScore;
                existing.BertEmbeddingsComputed = stat.BertEmbeddingsComputed;
                existing.LastProcessed = DateTimeOffset.UtcNow;
                existing.AmbiguityScore = stat.MaxSignificance; // Use max significance as proxy
            }
            else
            {
                // Create new
                _dbContext.Set<NgramStatistics>().Add(new NgramStatistics
                                                      {
                                                          WordId = stat.WordId, TotalNgrams = stat.TotalNgrams,
                                                          SignificantNgrams = stat.SignificantNgrams,
                                                          AvgSignificanceScore = stat.AvgSignificanceScore,
                                                          BertEmbeddingsComputed = stat.BertEmbeddingsComputed,
                                                          LastProcessed = DateTimeOffset.UtcNow, AmbiguityScore = stat.MaxSignificance
                                                      });
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        Console.WriteLine(
                          $"Statistics updated for {stats.Count} words");
    }
}

// Helper classes for type safety
public class NgramGroup
{
    public ExtractedNgram Ngram { get; set; }
    public int Occurrences { get; set; }
    public List<NgramSourceInfo> Sources { get; set; } = new();
}

public class NgramSourceInfo
{
    public int ExampleSentenceId { get; set; }
    public int TargetMorphemeIndex { get; set; }
}

public class NgramWithScore
{
    public ExtractedNgram Ngram { get; set; }
    public int Occurrences { get; set; }
    public List<NgramSourceInfo> Sources { get; set; } = new();
    public float SignificanceScore { get; set; }
}

public class AmbiguousWordProcessingConfig
{
    public AmbiguityConfig AmbiguityConfig { get; set; } = new();
    public SentenceFinderConfig SentenceFinderConfig { get; set; } = new();
    public NgramExtractionConfig NgramExtractionConfig { get; set; } = new();

    /// <summary>
    /// Process only these specific word IDs (null = process all ambiguous words)
    /// </summary>
    public List<int>? SpecificWordIds { get; set; }

    /// <summary>
    /// Minimum significance score to keep an n-gram
    /// </summary>
    public float MinSignificanceScore { get; set; } = 0.3f;

    /// <summary>
    /// Maximum n-grams to store per word+reading combination
    /// </summary>
    public int MaxNgramsPerWord { get; set; } = 50;
}