using System.Diagnostics;
using BunnyCDN.Net.Storage;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jiten.Core;

public static class JitenHelper
{
    public static async Task InsertDeck(DbContextOptions<JitenDbContext> options, Deck deck, byte[] cover)
    {
        // Ignore if the deck already exists
        await using var context = new JitenDbContext(options);

        if (await context.Decks.AnyAsync(d => d.OriginalTitle == deck.OriginalTitle && d.MediaType == deck.MediaType))
        {
            Console.WriteLine($"Deck {deck.OriginalTitle} already exists, skipping.");
            return;
        }

        // Fix potential null references to decks
        deck.SetParentsAndDeckWordDeck(deck);
        deck.ParentDeckId = null;
        
        if (deck.OriginalTitle == null)
            deck.OriginalTitle = deck.RomajiTitle ?? deck.EnglishTitle;

        context.Decks.Add(deck);

        await context.SaveChangesAsync();

        var coverUrl = await BunnyCdnHelper.UploadFile(cover, $"{deck.DeckId}/cover.jpg");
        deck.CoverName = coverUrl;

        await context.SaveChangesAsync();
    }

    public static async Task ComputeFrequencies(DbContextOptions<JitenDbContext> options)
    {
        int batchSize = 100000;
        await using var context = new JitenDbContext(options);

        Dictionary<int, JmDictWordFrequency> wordFrequencies = new();

        // Prefill the dictionary with default values
        var wordReadingCounts = await context.JMDictWords
                                             .AsNoTracking()
                                             .Select(w => new { w.WordId, ReadingsCount = w.Readings.Count })
                                             .ToDictionaryAsync(w => w.WordId, w => w.ReadingsCount);

        foreach (var kvp in wordReadingCounts)
        {
            wordFrequencies.Add(kvp.Key, new JmDictWordFrequency
                                         {
                                             WordId = kvp.Key,
                                             FrequencyRank = 0,
                                             UsedInMediaAmount = 0,
                                             ReadingsFrequencyPercentage = [..new float[kvp.Value]],
                                             ReadingsObservedFrequency = [..new Double[kvp.Value]],
                                             ReadingsFrequencyRank = [..new int[kvp.Value]],
                                             ReadingsUsedInMediaAmount = [..new int[kvp.Value]],
                                         });
        }

        var primaryDecks = await context.Decks.AsNoTracking()
                                        .Where(d => d.ParentDeck == null)
                                        .Select(d => d.DeckId)
                                        .ToListAsync();

        int totalEntries = await context.DeckWords.Where(d => primaryDecks.Contains(d.DeckId)).CountAsync();
        var processedDecks = new HashSet<(int, int)>();

        for (int i = 0; i < totalEntries; i += batchSize)
        {
            var deckWords = await context.DeckWords
                                         .AsNoTracking()
                                         .Where(d => primaryDecks.Contains(d.DeckId))
                                         .Skip(i)
                                         .Take(batchSize)
                                         .ToListAsync();

            foreach (var deckWord in deckWords)
            {
                var word = wordFrequencies[deckWord.WordId];

                word.FrequencyRank += deckWord.Occurrences;

                // The word itself must be only counted once per deck
                if (!processedDecks.Contains((deckWord.DeckId, deckWord.WordId)))
                {
                    word.UsedInMediaAmount++;
                    processedDecks.Add((deckWord.DeckId, deckWord.WordId));
                }

                word.ReadingsUsedInMediaAmount[deckWord.ReadingIndex]++;
                word.ReadingsFrequencyRank[deckWord.ReadingIndex] += deckWord.Occurrences;

                wordFrequencies.TryAdd(deckWord.WordId, word);
            }
        }

        var sortedWordFrequencies = wordFrequencies.Values
                                                   .OrderByDescending(w => w.FrequencyRank)
                                                   .ToList();

        int currentRank = 0;
        int previousFrequencyRank = sortedWordFrequencies.FirstOrDefault()?.FrequencyRank ?? 0;
        int duplicateCount = 0;
        int totalOccurences = sortedWordFrequencies.Sum(w => w.FrequencyRank);

        for (int i = 0; i < sortedWordFrequencies.Count; i++)
        {
            var word = sortedWordFrequencies[i];

            for (int j = 0; j < word.ReadingsUsedInMediaAmount.Count; j++)
            {
                word.ReadingsObservedFrequency[j] = word.ReadingsFrequencyRank[j] / (double)totalOccurences;
                word.ReadingsFrequencyPercentage[j] = (word.ReadingsFrequencyRank[j] / (double)word.FrequencyRank) * 100;
            }

            // Keep the same rank if they have the same amount of occurences

            if (word.FrequencyRank == previousFrequencyRank)
            {
                duplicateCount++;
            }
            else
            {
                currentRank += duplicateCount;
                duplicateCount = 1;
                previousFrequencyRank = word.FrequencyRank;
            }

            word.ObservedFrequency = word.FrequencyRank / (double)totalOccurences;
            word.FrequencyRank = currentRank + 1;
        }

        var allReadings = new List<int>();

        foreach (var wordFreq in sortedWordFrequencies)
        {
            allReadings.AddRange(wordFreq.ReadingsFrequencyRank);
        }

        // Sort by occurences and group the readings with the same amount of occurences so we can skip ranks 
        var frequencyGroups = allReadings
                              .GroupBy(x => x)
                              .OrderByDescending(g => g.Key)
                              .ToList();

        var frequencyRanks = new Dictionary<int, int>();
        currentRank = 1;

        // Assign a rank for each amount of occurence, skip ranks based on number of duplicates
        foreach (var group in frequencyGroups)
        {
            frequencyRanks[group.Key] = currentRank;
            currentRank += group.Count();
        }

        foreach (var wordFreq in sortedWordFrequencies)
        {
            for (int i = 0; i < wordFreq.ReadingsFrequencyRank.Count; i++)
            {
                int frequency = wordFreq.ReadingsFrequencyRank[i];
                wordFreq.ReadingsFrequencyRank[i] = frequencyRanks[frequency];
            }
        }

        // Delete previous frequency data if it exists
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE jmdict.\"WordFrequencies\"");

        // Bulk insert using PostgreSQL COPY command
        const string tempTable = "temp_word_frequencies";

        // Create a new connection for the COPY operation
        await using var conn = new NpgsqlConnection(context.Database.GetConnectionString());
        await conn.OpenAsync();

        // First, retrieve the structure of the target table
        var command = new NpgsqlCommand(@"
        SELECT string_agg(column_name || ' ' || data_type, ', ' ORDER BY ordinal_position)
        FROM information_schema.columns
        WHERE table_name = 'WordFrequencies'
          AND column_name != 'id'
    ", conn);

        string tableStructure = (string)(await command.ExecuteScalarAsync())!;

        // Create temp table with exact same structure
        await using (var cmd = new NpgsqlCommand($@"
        CREATE TEMP TABLE {tempTable} AS 
        SELECT * FROM jmdict.""WordFrequencies"" WHERE 1=0;", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }


        // Perform the COPY operation
        await using (var writer =
                     await
                         conn.BeginBinaryImportAsync($@"COPY {tempTable} (""WordId"", ""FrequencyRank"", ""ObservedFrequency"", ""UsedInMediaAmount"", ""ReadingsFrequencyPercentage"", ""ReadingsObservedFrequency"", ""ReadingsFrequencyRank"", ""ReadingsUsedInMediaAmount"") FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var word in sortedWordFrequencies)
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(word.WordId);
                await writer.WriteAsync(word.FrequencyRank);
                await writer.WriteAsync(word.ObservedFrequency);
                await writer.WriteAsync(word.UsedInMediaAmount);
                await writer.WriteAsync(word.ReadingsFrequencyPercentage, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Double);
                await writer.WriteAsync(word.ReadingsObservedFrequency, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Double);
                await writer.WriteAsync(word.ReadingsFrequencyRank, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer);
                await writer.WriteAsync(word.ReadingsUsedInMediaAmount, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer);
            }

            await writer.CompleteAsync();
        }

        // Insert from temp table to final table and cleanup
        await using (var cmd = new NpgsqlCommand($@"
        INSERT INTO jmdict.""WordFrequencies""
        SELECT * FROM {tempTable};
        DROP TABLE {tempTable};", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Calculate the difficulty of decks. Still very WIP
    /// </summary>
    public static async Task ComputeDifficulty(DbContextOptions<JitenDbContext> options, bool verbose, MediaType mediaType)
    {
        int averageWordDifficultyWeight = 25;
        int peakWordDifficultyWeight = 15;
        int characterCountWeight = 0;
        int uniqueWordCountWeight = 15;
        int uniqueKanjiCountWeight = 30;
        int averageSentenceLengthWeight = 15;

        int batchSize = 10;
        await using var context = new JitenDbContext(options);

        var allDecks = await context.Decks.Where(d => d.MediaType == mediaType && d.ParentDeck == null).ToListAsync();
        
        if (allDecks.Count == 0)
        {
            Console.WriteLine("No decks found for media type " + mediaType);
            return;
        }
        
        var allFrequencies = await context.JmDictWordFrequencies.AsNoTracking().ToListAsync();
        var wordFrequencies = allFrequencies.ToDictionary(f => f.WordId, f => f);

        Dictionary<int, double> wordDifficultiesTotal = new();
        Dictionary<int, double> peakWordDifficulties = new();

        // Compute the word difficulty for each deck, based on the frequency rank of each words present in it
        for (int i = 0; i < allDecks.Count; i += batchSize)
        {
            var decks = allDecks.Skip(i).Take(batchSize).ToList();
            var deckWords = await context.DeckWords.AsNoTracking().Where(dw => decks.Select(d => d.DeckId).Contains(dw.DeckId))
                                         .ToListAsync();

            foreach (var deck in decks)
            {
                wordDifficultiesTotal.Add(deck.DeckId, 0);
                peakWordDifficulties.Add(deck.DeckId, 0);

                Dictionary<int, int> wordFrequencyBy1k = new Dictionary<int, int>();

                // Get all deck words for this specific deck
                var deckWordsList = deckWords.Where(dw => dw.DeckId == deck.DeckId).ToList();

                // Calculate total occurrences in the deck
                int totalOccurrences = deckWordsList.Sum(dw => dw.Occurrences);

                // Create list of words with their rank and occurrences
                var wordsWithRankAndOccurrences = deckWordsList
                                                  .Select(word => (
                                                              word,
                                                              rank: wordFrequencies[word.WordId].ReadingsFrequencyRank[word.ReadingIndex],
                                                              occurrences: word.Occurrences
                                                          ))
                                                  .OrderByDescending(pair => pair.rank) // Sort by rarity first
                                                  .ToList();

                // Select rarest words until we reach 5% of total occurrences
                var peakWordEntries = new List<(DeckWord word, int rank, int occurrences)>();
                int accumulatedOccurrences = 0;
                int targetOccurrences = (int)(totalOccurrences * 0.1);

                foreach (var entry in wordsWithRankAndOccurrences)
                {
                    peakWordEntries.Add(entry);
                    accumulatedOccurrences += entry.occurrences;

                    if (accumulatedOccurrences >= targetOccurrences)
                        break;
                }

                // Keep track of peak word entries to exclude them from average calculation
                var peakWordEntryIds = new HashSet<int>(peakWordEntries.Select(pair => pair.word.DeckWordId));

                foreach (var word in deckWordsList.Where(dw => !peakWordEntryIds.Contains(dw.DeckWordId)))
                {
                    var frequencyRank = wordFrequencies[word.WordId].ReadingsFrequencyRank[word.ReadingIndex];

                    var nRank = frequencyRank / 1000;

                    if (!wordFrequencyBy1k.TryAdd(nRank, 1))
                        wordFrequencyBy1k[nRank] += word.Occurrences;
                }

                var list = wordFrequencyBy1k.Select(x => (x.Key, x.Value))
                                            .OrderBy(x => x.Key)
                                            .ToList();

                // Scale each value by rank exponentially
                foreach (var wf in list)
                {
                    wordDifficultiesTotal[deck.DeckId] +=
                        (wf.Value / (double)deck.WordCount * 100) * Math.Exp(wf.Key / 10.0);
                }

                Dictionary<int, int> peakWordFrequencyBy1k = new Dictionary<int, int>();

                foreach (var word in peakWordEntries)
                {
                    var nRank = word.rank / 1000;

                    if (!peakWordFrequencyBy1k.TryAdd(nRank, 1))
                        peakWordFrequencyBy1k[nRank]++;
                }

                var peakWordList = wordFrequencyBy1k.Select(x => (x.Key, x.Value))
                                                    .OrderBy(x => x.Key)
                                                    .ToList();

                // Scale each value by rank exponentially
                foreach (var wf in peakWordList)
                {
                    peakWordDifficulties[deck.DeckId] +=
                        (wf.Value / (double)deck.WordCount * 100) * Math.Exp(wf.Key / 5.0);
                }
            }
        }


        // Take the min and max values among all the decks
        // We go by 1st and 9th decile to avoid outliers
        double minDifficulty = wordDifficultiesTotal.Values.Min();
        double maxDifficulty = wordDifficultiesTotal.Values.Max();
        uint minCharacterCount = (uint)allDecks.Min(d => d.CharacterCount);
        uint maxCharacterCount = (uint)allDecks.Max(d => d.CharacterCount);
        uint minUniqueWordCount = (uint)allDecks.Min(d => d.UniqueWordCount);
        uint maxUniqueWordCount = (uint)allDecks.Max(d => d.UniqueWordCount);
        uint minUniqueKanjiCount = (uint)allDecks.Min(d => d.UniqueKanjiCount);
        uint maxUniqueKanjiCount = (uint)allDecks.Max(d => d.UniqueKanjiCount);
        double minAverageSentenceLength = allDecks.Min(d => d.AverageSentenceLength);
        double maxAverageSentenceLength = allDecks.Max(d => d.AverageSentenceLength);

        // double minDifficulty = wordDifficultiesTotal.Values.OrderBy(d => d).ElementAt((int)(allDecks.Count * 0.05f));
        // double maxDifficulty = wordDifficultiesTotal.Values.OrderBy(d => d).ElementAt((int)(allDecks.Count * 0.99f));
        // uint minCharacterCount = (uint)allDecks.OrderBy(d => d.CharacterCount).ElementAt((int)(allDecks.Count * 0.05f)).CharacterCount;
        // uint maxCharacterCount = (uint)allDecks.OrderBy(d => d.CharacterCount).ElementAt((int)(allDecks.Count * 0.99f)).CharacterCount;
        // uint minUniqueWordCount = (uint)allDecks.OrderBy(d => d.UniqueWordCount).ElementAt((int)(allDecks.Count * 0.05f)).UniqueWordCount;
        // uint maxUniqueWordCount = (uint)allDecks.OrderBy(d => d.UniqueWordCount).ElementAt((int)(allDecks.Count * 0.99f)).UniqueWordCount;
        // uint minUniqueKanjiCount = (uint)allDecks.OrderBy(d => d.UniqueKanjiCount).ElementAt((int)(allDecks.Count * 0.05f)).UniqueKanjiCount;
        // uint maxUniqueKanjiCount = (uint)allDecks.OrderBy(d => d.UniqueKanjiCount).ElementAt((int)(allDecks.Count * 0.99f)).UniqueKanjiCount;
        // double minAverageSentenceLength =
        //     allDecks.OrderBy(d => d.AverageSentenceLength).ElementAt((int)(allDecks.Count * 0.05f)).AverageSentenceLength;
        // double maxAverageSentenceLength =
        //     allDecks.OrderBy(d => d.AverageSentenceLength).ElementAt((int)(allDecks.Count * 0.99f)).AverageSentenceLength;

        foreach (var deck in allDecks)
        {
            double difficulty = 0;

            // float wordDifficulty =
            //     MapDoubleToWeight(wordDifficultiesTotal[deck.DeckId], minDifficulty, maxDifficulty, wordDifficultyWeight);
            // float characterCountDifficulty =
            //     MapToWeight((uint)deck.CharacterCount, minCharacterCount, maxCharacterCount, characterCountWeight);
            // float uniqueWordCountDifficulty =
            //     MapToWeight((uint)deck.UniqueWordCount, minUniqueWordCount, maxUniqueWordCount, uniqueWordCountWeight);
            // float uniqueKanjiCountDifficulty =
            //     MapToWeight((uint)deck.UniqueKanjiCount, minUniqueKanjiCount, maxUniqueKanjiCount, uniqueKanjiCountWeight);
            // float averageSentenceLengthDifficulty = MapDoubleToWeight(deck.AverageSentenceLength, minAverageSentenceLength,
            //                                                           maxAverageSentenceLength, averageSentenceLengthWeight);

            float wordDifficulty = MapWithZScore(wordDifficultiesTotal[deck.DeckId], wordDifficultiesTotal.Values.ToList(),
                                                 averageWordDifficultyWeight);
            float peakWordDifficulty = MapWithZScore(peakWordDifficulties[deck.DeckId], peakWordDifficulties.Values.ToList(),
                                                     peakWordDifficultyWeight);
            float characterCountDifficulty = MapWithZScore((uint)deck.CharacterCount,
                                                           allDecks.Select(d => (double)d.CharacterCount).ToList(), characterCountWeight);
            float uniqueWordCountDifficulty = MapWithZScore((uint)deck.UniqueWordCount,
                                                            allDecks.Select(d => (double)d.UniqueWordCount).ToList(),
                                                            uniqueWordCountWeight);
            float uniqueKanjiCountDifficulty = MapWithZScore((uint)deck.UniqueKanjiCount,
                                                             allDecks.Select(d => (double)d.UniqueKanjiCount).ToList(),
                                                             uniqueKanjiCountWeight);
            float averageSentenceLengthDifficulty = MapWithZScore(deck.AverageSentenceLength,
                                                                  allDecks.Select(d => (double)d.AverageSentenceLength).ToList(),
                                                                  averageSentenceLengthWeight);

            difficulty += wordDifficulty;
            difficulty += peakWordDifficulty;
            difficulty += characterCountDifficulty;
            difficulty += uniqueWordCountDifficulty;
            difficulty += uniqueKanjiCountDifficulty;
            difficulty += averageSentenceLengthDifficulty;

            deck.Difficulty = (int)Math.Round(difficulty);

            if (verbose)
            {
                Console.WriteLine("========");
                Console.WriteLine("Difficulty for deck " + deck.OriginalTitle);
                Console.WriteLine("Word difficulty:" + wordDifficulty);
                Console.WriteLine("Peak word difficulty:" + peakWordDifficulty);
                Console.WriteLine("Character count difficulty:" + characterCountDifficulty);
                Console.WriteLine("Unique word count difficulty:" + uniqueWordCountDifficulty);
                Console.WriteLine("Unique kanji count difficulty:" + uniqueKanjiCountDifficulty);
                Console.WriteLine("Average sentence length difficulty:" + averageSentenceLengthDifficulty);
                Console.WriteLine("Total difficulty:" + deck.Difficulty);
            }
        }

        await context.SaveChangesAsync();

        // Map to the weight in a linear way
        // float MapToWeight(uint value, uint min, uint max, int weight)
        // {
        //     if (value < min)
        //         return 0;
        //
        //     if (value > max)
        //         return weight;
        //
        //     return (value - min) / (float)(max - min) * weight;
        // }
        //
        // float MapDoubleToWeight(double value, double min, double max, int weight)
        // {
        //     if (value < min)
        //         return 0;
        //
        //     if (value > max)
        //         return weight;
        //
        //     return (float)((value - min) / (max - min)) * weight;
        // }

        float MapWithZScore(double value, List<double> allValues, int weight)
        {
            double mean = allValues.Average(v => v);
            double stdDev = Math.Sqrt(allValues.Average(v => Math.Pow(v - mean, 2)));

            // Convert to z-score then normalize to 0-1 range (cap at ±2 std deviations)
            double zScore = (value - mean) / stdDev;
            double normalized = (Math.Max(-2, Math.Min(2, zScore)) + 2) / 4;

            return (float)(normalized * weight);
        }
    }

    /// <summary>
    /// Get infos about a deck to debug the difficulty
    /// </summary>
    public static async Task DebugDeck(DbContextOptions<JitenDbContext> options, int deckId)
    {
        await using var context = new JitenDbContext(options);

// Retrieve the deck
        var deck = await context.Decks
                                .AsNoTracking()
                                .FirstOrDefaultAsync(d => d.DeckId == deckId);
        if (deck == null)
        {
            Console.WriteLine($"Deck with id {deckId} not found.");
            return;
        }

        Console.WriteLine("=============");
        Console.WriteLine("Deck info");
        Console.WriteLine("=============");
        Console.WriteLine("Original name: " + deck.OriginalTitle);
        Console.WriteLine("Romaji name: " + deck.RomajiTitle);
        Console.WriteLine("Character count: " + deck.CharacterCount);


        var deckWords = await context.DeckWords
                                     .AsNoTracking()
                                     .Where(dw => dw.DeckId == deckId)
                                     .ToListAsync();

        var wordIds = deckWords.Select(dw => dw.WordId)
                               .Distinct()
                               .ToList();

        var wordFrequencies = await context.JmDictWordFrequencies
                                           .AsNoTracking()
                                           .Where(wf => wordIds.Contains(wf.WordId))
                                           .ToListAsync();

        var words = await context.JMDictWords
                                 .AsNoTracking()
                                 .Where(w => wordIds.Contains(w.WordId))
                                 .ToListAsync();

        var query = new List<(JmDictWord word, JmDictWordFrequency frequency)>();

        foreach (var wf in wordFrequencies)
        {
            foreach (var w in words)
            {
                if (wf.WordId == w.WordId)
                {
                    query.Add(new(w, wf));
                }
            }
        }

        // var rarestWords = query.OrderByDescending(x => x.frequency.FrequencyRank)
        //                        .Take(100)
        //                        .ToList();
        //
        // Console.WriteLine("First 100 rarest words in deck:");
        // foreach (var item in rarestWords)
        // {
        //     Console.WriteLine(item.word.Readings[0] + " - " + item.frequency.FrequencyRank);
        // }


        Dictionary<int, int> wordFrequencyBy10k = new Dictionary<int, int>();
        foreach (var w in words)
        {
            var frequencyRank = wordFrequencies.First(wf => wf.WordId == w.WordId).FrequencyRank;
            var nRank = frequencyRank / 1000;

            if (wordFrequencyBy10k.ContainsKey(nRank))
                wordFrequencyBy10k[nRank]++;
            else
                wordFrequencyBy10k[nRank] = 1;
        }

        var list = wordFrequencyBy10k.Select(x => (x.Key, x.Value))
                                     .OrderBy(x => x.Key)
                                     .ToList();

        double totalPoints = 0;
        foreach (var wf in list)
        {
            var points = (wf.Value / (double)words.Count * 100) * Math.Exp(wf.Key / 10.0);
            totalPoints += points;
        }

        foreach (var wf in list)
        {
            var points = (wf.Value / (double)words.Count * 100) * Math.Exp(wf.Key / 10.0);

            Console.WriteLine($"Percentage of words in rank {wf.Key} : {(wf.Value / (double)words.Count * 100f).ToString("F2")}%");
            Console.WriteLine("Points: " + points + " - Percentage of total points: " + (points / totalPoints * 100f).ToString("F2") + "%");
        }

        Console.WriteLine("Total points: " + totalPoints);
    }
}