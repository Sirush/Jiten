using System.Diagnostics;
using BunnyCDN.Net.Storage;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jiten.Core;

public static class JitenHelper
{
    public static async Task InsertDeck(DbContextOptions<JitenDbContext> options, Deck deck, byte[] cover, bool update = false)
    {
        await using var context = new JitenDbContext(options);
        await using var transaction = await context.Database.BeginTransactionAsync();

        try
        {
            var existingDeck =
                await context.Decks
                             .Include(d => d.DeckWords)
                             .Include(d => d.Children)
                             .Include(d => d.RawText)
                             .FirstOrDefaultAsync(d => d.OriginalTitle == deck.OriginalTitle && d.MediaType == deck.MediaType);

            if (existingDeck != null)
            {
                if (!update || update && existingDeck.LastUpdate >= deck.LastUpdate)
                {
                    Console.WriteLine($"Deck {deck.OriginalTitle} already exists, no update flag or deck not changed, skipping.");
                    await transaction.RollbackAsync();
                    return;
                }
                else
                {
                    Console.WriteLine($"Deck {deck.OriginalTitle} already exists, updating.");
                    await UpdateDeck(context, existingDeck, deck);
                }
            }
            else
            {
                // Fix potential null references to decks
                deck.SetParentsAndDeckWordDeck(deck);
                deck.ParentDeckId = null;

                context.Decks.Add(deck);

                await context.SaveChangesAsync();

                var coverUrl = await BunnyCdnHelper.UploadFile(cover, $"{deck.DeckId}/cover.jpg");
                deck.CoverName = coverUrl;
                context.Entry(deck).State = EntityState.Modified;
            }

            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            Console.WriteLine($"Error inserting deck: {ex.Message}");
        }
    }

    private static async Task UpdateDeck(JitenDbContext context, Deck existingDeck, Deck deck)
    {
        var deckId = existingDeck.DeckId;

        existingDeck.LastUpdate = DateTime.UtcNow;
        existingDeck.MediaType = deck.MediaType;
        existingDeck.OriginalTitle = deck.OriginalTitle;
        existingDeck.RomajiTitle = deck.RomajiTitle;
        existingDeck.EnglishTitle = deck.EnglishTitle;
        existingDeck.CharacterCount = deck.CharacterCount;
        existingDeck.WordCount = deck.WordCount;
        existingDeck.UniqueWordCount = deck.UniqueWordCount;
        existingDeck.UniqueWordUsedOnceCount = deck.UniqueWordUsedOnceCount;
        existingDeck.UniqueKanjiCount = deck.UniqueKanjiCount;
        existingDeck.UniqueKanjiUsedOnceCount = deck.UniqueKanjiUsedOnceCount;
        existingDeck.SentenceCount = deck.SentenceCount;
        existingDeck.RawText = deck.RawText;

        context.DeckWords.RemoveRange(existingDeck.DeckWords);
        foreach (var dw in deck.DeckWords)
        {
            var newDeckWord = new DeckWord
                              {
                                  WordId = dw.WordId, ReadingIndex = dw.ReadingIndex, Occurrences = dw.Occurrences, DeckId = deckId,
                                  Deck = existingDeck
                              };
            existingDeck.DeckWords.Add(newDeckWord);
        }

        await UpdateChildDecks(context, existingDeck, deck.Children);
    }

    private static async Task UpdateChildDecks(JitenDbContext context, Deck existingDeck, ICollection<Deck> children)
    {
        var newChildren = children.ToDictionary(c => c.OriginalTitle);
        var existingChildren = existingDeck.Children.ToList();

        foreach (var child in children)
        {
            var key = child.OriginalTitle;
            Deck? existingChild = existingChildren.FirstOrDefault(c => c.OriginalTitle == key);

            if (existingChild != null)
            {
                Console.WriteLine("Updating child deck " + key);
                try
                {
                    var rawTextReference =
                        context.Entry(existingChild)
                               .Reference(nameof(existingChild.RawText)); // Or the actual navigation property name if different
                    await rawTextReference.LoadAsync();
                }
                catch (Exception loadEx)
                {
                    Console.WriteLine($"Error explicitly loading data for child deck {key}: {loadEx.Message}");
                    continue;
                }

                await UpdateDeck(context, existingChild, child);
                newChildren.Remove(key);
            }
            else
            {
                Console.WriteLine("Inserting new child deck " + key);

                var newChildDeck = new Deck();
                newChildDeck.ParentDeckId = existingDeck.DeckId;
                newChildDeck.ParentDeck = existingDeck;
                newChildDeck.CreationDate = DateTime.UtcNow;
                newChildDeck.LastUpdate = DateTimeOffset.UtcNow;
                newChildDeck.MediaType = existingDeck.MediaType;
                newChildDeck.OriginalTitle = child.OriginalTitle;
                newChildDeck.RomajiTitle = child.RomajiTitle;
                newChildDeck.EnglishTitle = child.EnglishTitle;
                newChildDeck.DeckOrder = child.DeckOrder;
                newChildDeck.CharacterCount = child.CharacterCount;
                newChildDeck.UniqueWordCount = child.UniqueWordCount;
                newChildDeck.UniqueKanjiCount = child.UniqueKanjiCount;
                newChildDeck.SentenceCount = child.SentenceCount;
                newChildDeck.WordCount = child.WordCount;
                newChildDeck.RawText = child.RawText;

                foreach (var dw in child.DeckWords)
                {
                    var newDeckWord = new DeckWord
                                      {
                                          WordId = dw.WordId, ReadingIndex = dw.ReadingIndex, Occurrences = dw.Occurrences, Deck = child
                                      };
                    newChildDeck.DeckWords.Add(newDeckWord);
                }

                context.Decks.Add(newChildDeck);
                newChildren.Remove(key);
            }
        }

        var childrenToDelete = existingChildren
                               .Where(ec => children.All(c => c.OriginalTitle != ec.OriginalTitle))
                               .ToList();


        foreach (var childToDelete in childrenToDelete)
        {
            Console.WriteLine($"Deleting child deck {childToDelete.OriginalTitle}.");
            context.DeckWords.RemoveRange(childToDelete.DeckWords);
            context.Decks.Remove(childToDelete);
        }
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
                                             WordId = kvp.Key, FrequencyRank = 0, UsedInMediaAmount = 0,
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

        var wordAggregates = await context.DeckWords
                                          .Where(d => primaryDecks.Contains(d.DeckId))
                                          .GroupBy(d => d.WordId)
                                          .Select(g => new
                                                       {
                                                           WordId = g.Key, TotalOccurrences = g.Sum(dw => dw.Occurrences),
                                                           DistinctDeckCount =
                                                               g.Select(dw => dw.DeckId).Distinct().Count()
                                                       })
                                          .ToListAsync();

        foreach (var agg in wordAggregates)
        {
            if (wordFrequencies.TryGetValue(agg.WordId, out var freq))
            {
                freq.FrequencyRank = agg.TotalOccurrences; // Store raw count
                freq.UsedInMediaAmount = agg.DistinctDeckCount;
            }
        }

        var readingAggregates = await context.DeckWords
                                             .Where(d => primaryDecks.Contains(d.DeckId))
                                             .GroupBy(d => new { d.WordId, d.ReadingIndex })
                                             .Select(g => new
                                                          {
                                                              g.Key.WordId, g.Key.ReadingIndex,
                                                              TotalOccurrences = g.Sum(dw => dw.Occurrences), EntryCount = g.Count()
                                                          })
                                             .ToListAsync();

        foreach (var agg in readingAggregates)
        {
            if (wordFrequencies.TryGetValue(agg.WordId, out var freq))
            {
                freq.ReadingsFrequencyRank[agg.ReadingIndex] = agg.TotalOccurrences; // Store raw count
                freq.ReadingsUsedInMediaAmount[agg.ReadingIndex] = agg.EntryCount;
            }
        }

        var sortedWordFrequencies = wordFrequencies.Values
                                                   .OrderByDescending(w => w
                                                                          .FrequencyRank)
                                                   .ToList();


        long totalOccurrences = sortedWordFrequencies.Sum(w => (long)w.FrequencyRank);

        int currentRank = 0;
        int previousRawFrequency = -1;
        int rankStep = 0;

        for (int i = 0; i < sortedWordFrequencies.Count; i++)
        {
            var word = sortedWordFrequencies[i];

            int wordRawFrequencyCount = word.FrequencyRank;

            for (int j = 0; j < word.ReadingsObservedFrequency.Count; j++)
            {
                // Prevent division by zero
                if (totalOccurrences > 0)
                    word.ReadingsObservedFrequency[j] = word.ReadingsFrequencyRank[j] / (double)totalOccurrences;
                else
                    word.ReadingsObservedFrequency[j] = 0;

                // Prevent division by zero
                if (wordRawFrequencyCount > 0)
                    word.ReadingsFrequencyPercentage[j] = (word.ReadingsFrequencyRank[j] / (double)wordRawFrequencyCount) * 100.0;
                else
                    word.ReadingsFrequencyPercentage[j] = 0;
            }

            // Prevent division by zero
            if (totalOccurrences > 0)
            {
                word.ObservedFrequency = wordRawFrequencyCount / (double)totalOccurrences;
            }
            else
            {
                word.ObservedFrequency = 0;
            }

            if (wordRawFrequencyCount != previousRawFrequency)
            {
                currentRank += rankStep;
                rankStep = 1;
                previousRawFrequency = wordRawFrequencyCount;
            }
            else
            {
                rankStep++;
            }

            word.FrequencyRank = currentRank + 1;
        }

        var readingFrequencyCounts = new Dictionary<int, int>();

        foreach (var wordFreq in sortedWordFrequencies)
        {
            foreach (int readingRawFreq in wordFreq.ReadingsFrequencyRank)
            {
                if (readingRawFreq <= 0) continue;

                readingFrequencyCounts.TryGetValue(readingRawFreq, out int currentCount);
                readingFrequencyCounts[readingRawFreq] = currentCount + 1;
            }
        }


        // Sort frequencies descending and calculate ranks
        var sortedReadingFrequencies = readingFrequencyCounts.OrderByDescending(kvp => kvp.Key).ToList();
        var readingFrequencyFinalRanks = new Dictionary<int, int>();
        currentRank = 1;
        foreach (var kvp in sortedReadingFrequencies)
        {
            readingFrequencyFinalRanks.Add(kvp.Key, currentRank);
            currentRank += kvp.Value; // Increment rank by the number of readings sharing this frequency
        }

        int zeroReadingRank = currentRank;

        // Assign the calculated ranks back to the readings
        foreach (var wordFreq in sortedWordFrequencies)
        {
            for (int i = 0; i < wordFreq.ReadingsFrequencyRank.Count; i++)
            {
                int readingRawFreq = wordFreq.ReadingsFrequencyRank[i];

                // Check if this reading had a non-zero frequency and find its rank
                if (readingRawFreq > 0 && readingFrequencyFinalRanks.TryGetValue(readingRawFreq, out int finalRank))
                {
                    // Overwrite raw count with final rank
                    wordFreq.ReadingsFrequencyRank[i] = finalRank;
                }
                else
                {
                    wordFreq.ReadingsFrequencyRank[i] = zeroReadingRank;
                }
            }
        }

        Console.WriteLine("Finished calculations.");


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
        var allJmDictWords = await context.JMDictWords.AsNoTracking().Select(w => new {w.WordId, w.ReadingTypes}).ToDictionaryAsync(x => x.WordId, x => x.ReadingTypes);

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

                    // If the word is in kana, we take the rank of the vocabulary instead
                    if (allJmDictWords[word.WordId][word.ReadingIndex] == JmDictReadingType.KanaReading)
                        nRank = wordFrequencies[word.WordId].FrequencyRank / 1000;

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
                        (wf.Value / (double)deck.WordCount * 100) * Math.Exp(wf.Key / 7.0);
                }

                Dictionary<int, int> peakWordFrequencyBy1k = new Dictionary<int, int>();

                foreach (var peakEntry in peakWordEntries)
                {
                    var nRank = peakEntry.rank / 1000;
                    
                    // If the word is in kana, we take the rank of the vocabulary instead
                    if (allJmDictWords[peakEntry.word.WordId][peakEntry.word.ReadingIndex] == JmDictReadingType.KanaReading)
                        nRank = wordFrequencies[peakEntry.word.WordId].FrequencyRank / 1000;

                    if (!peakWordFrequencyBy1k.TryAdd(nRank, 1))
                        peakWordFrequencyBy1k[nRank]++;
                }

                var peakWordList = peakWordFrequencyBy1k.Select(x => (x.Key, x.Value))
                                                        .OrderBy(x => x.Key)
                                                        .ToList();

                // Scale each value by rank exponentially
                foreach (var wf in peakWordList)
                {
                    peakWordDifficulties[deck.DeckId] +=
                        (wf.Value / (double)deck.WordCount * 100) * Math.Exp(wf.Key / 4.0);
                }
            }
        }

        (double mean, double stdDev) wordDifficultyStats = GetStats(wordDifficultiesTotal.Values.ToList());
        (double mean, double stdDev) peakWordDifficultyStats = GetStats(peakWordDifficulties.Values.ToList());
        (double mean, double stdDev) characterCountStats = GetStats(allDecks.Select(d => (double)d.CharacterCount).ToList());
        (double mean, double stdDev) uniqueWordCountStats = GetStats(allDecks.Select(d => (double)d.UniqueWordCount).ToList());
        (double mean, double stdDev) uniqueKanjiCountStats = GetStats(allDecks.Select(d => (double)d.UniqueKanjiCount).ToList());
        (double mean, double stdDev) averageSentenceLengthStats = GetStats(allDecks.Select(d => (double)d.AverageSentenceLength).ToList());


        foreach (var deck in allDecks)
        {
            double difficulty = 0;

            float wordDifficulty = MapWithZScore(wordDifficultiesTotal[deck.DeckId], wordDifficultyStats.mean, wordDifficultyStats.stdDev,
                                                 averageWordDifficultyWeight);
            float peakWordDifficulty = MapWithZScore(peakWordDifficulties[deck.DeckId], peakWordDifficultyStats.mean,
                                                     peakWordDifficultyStats.stdDev,
                                                     peakWordDifficultyWeight);
            float characterCountDifficulty = MapWithZScore(deck.CharacterCount, characterCountStats.mean, characterCountStats.stdDev,
                                                           characterCountWeight);
            float uniqueWordCountDifficulty = MapWithZScore(deck.UniqueWordCount, uniqueWordCountStats.mean, uniqueWordCountStats.stdDev,
                                                            uniqueWordCountWeight);
            float uniqueKanjiCountDifficulty = MapWithZScore(deck.UniqueKanjiCount, uniqueKanjiCountStats.mean,
                                                             uniqueKanjiCountStats.stdDev,
                                                             uniqueKanjiCountWeight);
            float averageSentenceLengthDifficulty = MapWithZScore(deck.AverageSentenceLength
                                                                  , averageSentenceLengthStats.mean, averageSentenceLengthStats.stdDev,
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

        (double mean, double stdDev) GetStats(List<double> allValues)
        {
            double mean = allValues.Average(v => v);
            double stdDev = Math.Sqrt(allValues.Average(v => Math.Pow(v - mean, 2)));

            return (mean, stdDev);
        }

        float MapWithZScore(double value, double mean, double stdDev, int weight)
        {
            if (weight == 0)
                return 0;

            if (stdDev == 0)
                return (float)(0.5 * weight);

            // Convert to z-score then normalize to 0-1 range (cap at ±2 std deviations)
            double zScore = (value - mean) / stdDev;
            double cappedZScore = Math.Max(-2.0, Math.Min(2.0, zScore));
            double normalized = (cappedZScore + 2.0) / 4.0;

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