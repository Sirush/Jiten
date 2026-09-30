using Jiten.Core;
using Jiten.Core.Data;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CsvHelper;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Cli.ML;

public class DbFeatureExtractor
{
    private IDbContextFactory<JitenDbContext> _contextFactory;
    private static readonly ConcurrentBag<ExtractedFeatures> _allFeaturesList = new ConcurrentBag<ExtractedFeatures>();
    private static int _completedCount = 0;
    private static int _lastSaveCount = 0;

    public DbFeatureExtractor(IDbContextFactory<JitenDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task ExtractFeatures(Func<IDbContextFactory<JitenDbContext>, string, bool, bool, MediaType, Task<Deck>> parseFunction, string saveDirectory)
    {
        // Calculate static values (like ScoreHardcapValue)
        var _ = MLConfig.ScoreHardcapValue; // Access to ensure static constructor runs
        Console.WriteLine($"Calculated SCORE_HARDCAP_VALUE: {MLConfig.ScoreHardcapValue}");

        var mainStopwatch = Stopwatch.StartNew();
        await using var context = await _contextFactory.CreateDbContextAsync();
        var decks = await context.Decks.Where(d => (d.ParentDeck != null || (d.ParentDeck == null && d.Children.Count == 0)) && (d.MediaType == MediaType.Anime || d.MediaType == MediaType.Movie || d.MediaType == MediaType.Drama || d.MediaType == MediaType.Audio)).Include(d => d.RawText).ToListAsync();
        Console.WriteLine($"Loaded {decks.Count} decks from database.");

        List<MLInputData> itemsToProcess = new();

        foreach (var deck in decks)
        {
            itemsToProcess.Add(new()
                               {
                                   OriginalFileName = $"{deck.OriginalTitle}({deck.DeckId})", TextFilePath = deck.RawText!.RawText,
                                   MediaType = (int)deck.MediaType, DifficultyScore = 0
                               });
        }

        if (!itemsToProcess.Any())
        {
            Console.WriteLine("No items to process.");
            return;
        }

        Console.WriteLine($"Loaded {itemsToProcess.Count} items to process from {saveDirectory}.");

        var parallelOptions =
            new ParallelOptions { MaxDegreeOfParallelism = MLConfig.NumThreads };

        Console.WriteLine($"Starting feature extraction for {itemsToProcess.Count} files using {MLConfig.NumThreads} threads...");

        try
        {
            await Parallel.ForEachAsync(itemsToProcess, parallelOptions, async (item, ct) =>
            {
                if (ct.IsCancellationRequested) return;

                Stopwatch itemStopwatch = Stopwatch.StartNew();
                try
                {
                    Console.WriteLine("Processing file: " + item.OriginalFileName + "");
                    ExtractedFeatures features = await ProcessFileAsync(item, parseFunction);
                    Console.WriteLine("Processing file: " + item.OriginalFileName + " done.");
                    _allFeaturesList.Add(features);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    Console.WriteLine($"Processing of {item.OriginalFileName} cancelled due to application shutdown.");
                }
                catch (OperationCanceledException) // Timeout
                {
                    Console.WriteLine($"Timeout ({MLConfig.ChunkProcessingTimeoutSeconds}s) processing file {item.OriginalFileName}.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing file {item.OriginalFileName} + {ex.Message} + {ex.StackTrace}");
                }
                finally
                {
                    itemStopwatch.Stop();
                    int currentCompleted = Interlocked.Increment(ref _completedCount);
                    Console.WriteLine($"Processed {item.OriginalFileName} ({currentCompleted}/{itemsToProcess.Count}) in {itemStopwatch.ElapsedMilliseconds}ms. Thread: {Thread.CurrentThread.ManagedThreadId}");


                    if (currentCompleted % MLConfig.SaveInterval == 0 || currentCompleted == itemsToProcess.Count)
                        await SaveResults(Path.Combine(saveDirectory, MLConfig.OutputCsvPath), currentCompleted == itemsToProcess.Count);
                }
            });
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Main processing loop cancelled.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Unhandled exception in main processing loop: " + ex);
        }
        finally
        {
            Console.WriteLine("Main processing loop finished or interrupted.");

            if (_allFeaturesList.Count > _lastSaveCount)
            {
                Console.WriteLine("Performing final save...");
                await SaveResults(Path.Combine(saveDirectory, MLConfig.OutputCsvPath), true);
            }

            mainStopwatch.Stop();
            Console.WriteLine($"Total execution time: {mainStopwatch.Elapsed.TotalSeconds:F2} seconds");
            if (itemsToProcess.Any())
            {
                Console.WriteLine($"Average time per item (overall): {mainStopwatch.Elapsed.TotalMilliseconds / itemsToProcess.Count:F2} ms");
            }
        }
    }


    public async Task<ExtractedFeatures> ProcessFileAsync(MLInputData mlInput,
                                                          Func<IDbContextFactory<JitenDbContext>, string, bool, bool, MediaType, Task<Deck>> parseFunction)
    {
        var features = new ExtractedFeatures { Filename = mlInput.OriginalFileName, DifficultyRating = mlInput.DifficultyScore };

        string content;
        try
        {
            content = mlInput.TextFilePath;
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to read text file: {mlInput.TextFilePath}", ex);
        }

        var deck = await parseFunction(_contextFactory, content, false, false, MediaType.Novel);

        if (deck == null || deck.CharacterCount == 0 || deck.WordCount == 0)
            throw new Exception($"Received empty deck: {mlInput.TextFilePath}");

        deck.MediaType = (MediaType)mlInput.MediaType;

        List<DeckWord> deckWords = new List<DeckWord>();
        features.CharacterCount = deck.CharacterCount;
        features.WordCount = deck.WordCount;
        features.UniqueWordCount = deck.UniqueWordCount;
        features.UniqueWordOnceCount = deck.UniqueWordUsedOnceCount;
        features.UniqueKanjiCount = deck.UniqueKanjiCount;
        features.UniqueKanjiOnceCount = deck.UniqueKanjiUsedOnceCount;

        if (deck.MediaType is MediaType.Manga or MediaType.Anime or MediaType.Movie or MediaType.Drama or MediaType.Audio or MediaType.YouTube)
            deck.SentenceCount = 0;

        features.SentenceCount = deck.SentenceCount;
        features.AverageSentenceLength = deck.AverageSentenceLength;
        features.DialoguePercentage = deck.DialoguePercentage;
        features.Ttr = features.UniqueWordCount / features.WordCount;

        deckWords = deck.DeckWords.ToList();

        MLHelper.ExtractCharacterCounts(content, features);
        await using (var context = await _contextFactory.CreateDbContextAsync())
        {
            await MLHelper.ExtractFrequencyStats(context, deckWords, features);
        }
        MLHelper.ExtractConjugationStats(deckWords, features);
        MLHelper.ExtractReadabilityScore(deckWords, features);
        MLHelper.ExtractSemanticComplexity(deckWords, features);

        return features;
    }
    
    private static async Task SaveResults(string outputPath, bool isFinalSave)
    {
        Console.WriteLine($"Attempting to save {(_allFeaturesList.Count - _lastSaveCount)} new results (total {_allFeaturesList.Count})...");
        if (!_allFeaturesList.Any()) return;

        var featuresToSave = _allFeaturesList.ToList();

        try
        {
            var recordsForCsv = new List<dynamic>();
            foreach (var feature in featuresToSave)
            {
                var record = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                record["Filename"] = feature.Filename;
                record["DifficultyRating"] = feature.DifficultyRating;

                record["CharacterCount"] = feature.CharacterCount;
                record["WordCount"] = feature.WordCount;
                record["UniqueWordCount"] = feature.UniqueWordCount;
                record["UniqueWordOnceCount"] = feature.UniqueWordOnceCount;
                record["UniqueKanjiCount"] = feature.UniqueKanjiCount;
                record["UniqueKanjiOnceCount"] = feature.UniqueKanjiOnceCount;
                record["SentenceCount"] = feature.SentenceCount;
                record["Ttr"] = feature.Ttr;
                record["AverageSentenceLength"] = feature.AverageSentenceLength;
                // record["LogSentenceLength"] = feature.LogSentenceLength;
                record["DialoguePercentage"] = feature.DialoguePercentage;

                record["TotalCount"] = feature.TotalCount;
                record["KanjiCount"] = feature.KanjiCount;
                record["HiraganaCount"] = feature.HiraganaCount;
                record["KatakanaCount"] = feature.KatakanaCount;
                record["OtherCount"] = feature.OtherCount;
                record["KanjiRatio"] = feature.KanjiRatio;
                record["HiraganaRatio"] = feature.HiraganaRatio;
                record["KatakanaRatio"] = feature.KatakanaRatio;
                record["OtherRatio"] = feature.OtherRatio;
                // record["KanjiToKanaRatio"] = feature.KanjiToKanaRatio;

                record["KangoPercentage"] = feature.KangoPercentage;
                record["WagoPercentage"] = feature.WagoPercentage;
                record["GairaigoPercentage"] = feature.GairaigoPercentage;
                record["VerbPercentage"] = feature.VerbPercentage;
                record["ParticlePercentage"] = feature.ParticlePercentage;
                record["AvgWordPerSentence"] = feature.AvgWordPerSentence;
                record["ReadabilityScore"] = feature.ReadabilityScore;
                
                record["LogicalConnectorRatio"] = feature.LogicalConnectorRatio;
                record["ModalMarkerRatio"] = feature.ModalMarkerRatio;
                record["RelativeClauseMarkerRatio"] = feature.RelativeClauseMarkerRatio;
                record["MetaphorMarkerRatio"] = feature.MetaphorMarkerRatio;

                record["AvgLogFreqRank"] = feature.AvgLogFreqRank;
                // record["AvgFreqRank"] = feature.AvgFreqRank;
                // record["MedianLogFreqank"] = feature.MedianLogFreqRank;
                record["StdLogFreqRank"] = feature.StdLogFreqRank;
                record["MinFreqRank"] = feature.MinFreqRank;
                // record["MaxFreqRank"] = feature.MaxFreqRank;
                record["AvgLogObsFreq"] = feature.AvgLogObsFreq;
                // record["MedianLogObsFreq"] = feature.MedianLogObsFreq;
                record["StdLogObsFreq"] = feature.StdLogObsFreq;
                // record["MinObsFreq"] = feature.MinObsFreq;
                // record["MaxObsFreq"] = feature.MaxObsFreq;
                record["LowFreqRankPerc"] = feature.LowFreqRankPerc;
                record["LowFreqObsPerc"] = feature.LowFreqObsPerc;
                record["AvgReadingFreqRank"] = feature.AvgReadingFreqRank;
                record["MedianReadingFreqRank"] = feature.MedianReadingFreqRank;
                record["AvgReadingObsFreq"] = feature.AvgReadingObsFreq;
                record["MedianReadingObsFreq"] = feature.MedianReadingObsFreq;
                record["AvgReadingFreqPerc"] = feature.AvgReadingFreqPerc;
                // record["MedianReadingFreqPerc"] = feature.MedianReadingFreqPerc;
                record["AvgCustomScorePerWord"] = feature.AvgCustomScorePerWord;
                // record["MedianCustomWordScore"] = feature.MedianCustomWordScore;
                record["StdCustomWordScore"] = feature.StdCustomWordScore;
                // record["MaxCustomWordScore"] = feature.MaxCustomWordScore;
                record["PercCustomScoreAboveSoftcapStart"] = feature.PercCustomScoreAboveSoftcapStart;

                record["TotalConjugations"] = feature.TotalConjugations;
                foreach (var kvp in feature.ConjugationCategoryCounts)
                {
                    record[kvp.Key] = kvp.Value;
                }

                foreach (var kvp in feature.ConjugationCategoryRatios)
                {
                    record[kvp.Key] = kvp.Value;
                }

                record["RatioConjugations"] = feature.RatioConjugations;

                foreach (var kvp in feature.PosCategoryCounts)
                {
                    record[kvp.Key] = kvp.Value;
                }

                foreach (var kvp in feature.PosCategoryRatios)
                {
                    record[kvp.Key] = kvp.Value;
                }

                recordsForCsv.Add(record);
            }


            using var writer = new StreamWriter(outputPath, append: false, Encoding.UTF8); // Overwrite mode
            using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
            await csv.WriteRecordsAsync(recordsForCsv);
            await writer.FlushAsync();

            _lastSaveCount = featuresToSave.Count; // Update based on what was actually attempted to save
            Console.WriteLine($"Successfully saved {featuresToSave.Count} rows to {outputPath}.");
        }
        catch (Exception)
        {
            Console.WriteLine($"Failed to save CSV to {outputPath}");
        }
    }
}