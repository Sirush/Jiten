using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WanaKanaShaapu;

namespace Jiten.Parser
{
    public static class Program
    {
        private static Dictionary<string, List<int>> _lookups = new();
        private static Dictionary<int, JmDictWord> _allWords = new();
        private static bool _initialized = false;
        private static readonly SemaphoreSlim _initSemaphore = new SemaphoreSlim(1, 1);

        private record DeckWordCacheKey(string Text, PartOfSpeech PartOfSpeech, string DictionaryForm);

        private static readonly bool UseCache = true;
        private static readonly ConcurrentDictionary<DeckWordCacheKey, DeckWord> DeckWordCache = new();

        // Those cases are hardcoded, a better solution would be preferable but they don't work well with the current rules
        private static Dictionary<string, int> _specialCases = new()
                                                               {
                                                                   { "この", 1582920 }, { "こと", 1313580 }, { "もの", 1502390 },
                                                                   { "もん", 2780660 }, { "くせ", 1509350 }, { "いくら", 1219980 },
                                                                   { "とき", 1315840 }, { "な", 2029110 }, { "どん", 2142690 },
                                                                   { "いる", 1577980 }, { "そう", 1006610 }, { "ない", 1529520 },
                                                                   { "なんだ", 2119750 }, { "わけ", 1538330 }, { "いう", 1587040 },
                                                                   { "まま", 1585410 }, { "いく", 1219950 }, { "つく", 1495740 },
                                                                   { "たら", 2029050 }, { "彼", 1483070 }, { "いい", 2820690 },
                                                               };

        private static JitenDbContext _dbContext = null;

        public static async Task Main(string[] args)
        {
            var text = "「あそこ美味しいよねー。早くお祭り終わって欲しいなー。ノンビリ遊びに行きたーい」";
            
            var configuration = new ConfigurationBuilder()
                                .SetBasePath(Directory.GetCurrentDirectory())
                                .AddJsonFile("sharedsettings.json", optional: true, reloadOnChange: true)
                                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                                .AddEnvironmentVariables()
                                .Build();

            var connectionString = configuration.GetConnectionString("JitenDatabase");
            var optionsBuilder = new DbContextOptionsBuilder<JitenDbContext>();
            optionsBuilder.UseNpgsql(connectionString, o => { o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery); });
            
            await using var context = new JitenDbContext(optionsBuilder.Options);

            await ParseTextToDeck(context, text);
        }

        public static async Task InitDictionaries()
        {
            _lookups = await JmDictHelper.LoadLookupTable(_dbContext);
            _allWords = (await JmDictHelper.LoadAllWords(_dbContext)).ToDictionary(word => word.WordId);
        }

        public static async Task<List<DeckWord>> ParseText(JitenDbContext context, string text)
        {
            _dbContext = context;
            if (!_initialized)
            {
                await _initSemaphore.WaitAsync();
                try
                {
                    if (!_initialized) // Double-check to avoid race conditions
                    {
                        await InitDictionaries();
                        _initialized = true;
                    }
                }
                finally
                {
                    _initSemaphore.Release();
                }
            }

            var parser = new Parser();
            var wordInfos = await parser.Parse(text);


            // Only keep kanjis, kanas, digits,full width digits, latin characters, full width latin characters
            foreach (WordInfo wi in wordInfos)
            {
                wi.Text = Regex.Replace(wi.Text,
                                        "[^a-zA-Z0-9\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FAF\uFF21-\uFF3A\uFF41-\uFF5A\uFF10-\uFF19\u3005．]",
                                        "");
            }

            // Remove empty lines
            wordInfos.RemoveAll(x => string.IsNullOrWhiteSpace(x.Text));

            // Filter bad lines that cause exceptions
            wordInfos.ForEach(x => x.Text = Regex.Replace(x.Text, "ッー", ""));

            Deconjugator deconjugator = new Deconjugator();

            var processWords = wordInfos.Select(word => ProcessWord((word, 0), deconjugator)).ToList();

            var processedWords = await Task.WhenAll(processWords);
            return processedWords
                   .Where(result => result != null)
                   .Select(result => result!)
                   .ToList();
        }

        public static async Task<Deck> ParseTextToDeck(JitenDbContext context, string text)
        {
            _dbContext = context;
            if (!_initialized)
            {
                await _initSemaphore.WaitAsync();
                try
                {
                    if (!_initialized) // Double-check to avoid race conditions
                    {
                        await InitDictionaries();
                        _initialized = true;
                    }
                }
                finally
                {
                    _initSemaphore.Release();
                }
            }

            var timer = new Stopwatch();
            timer.Start();
            var parser = new Parser();
            var wordInfos = await parser.Parse(text);

            // TODO: support elongated vowels ふ～ -> ふう

            // Only keep kanjis, kanas, digits,full width digits, latin characters, full width latin characters 
            wordInfos.ForEach(x => x.Text =
                                  Regex.Replace(x.Text,
                                                "[^a-zA-Z0-9\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FAF\uFF21-\uFF3A\uFF41-\uFF5A\uFF10-\uFF19\u3005．]",
                                                ""));
            // Remove empty lines
            wordInfos = wordInfos.Where(x => !string.IsNullOrWhiteSpace(x.Text)).ToList();

            // Filter bad lines that cause exceptions
            // wordInfos.RemoveAll(w => w.Text is "ッー");
            wordInfos.ForEach(x => x.Text = Regex.Replace(x.Text, "ッー", ""));

            Deconjugator deconjugator = new Deconjugator();

            var uniqueWords = new List<(WordInfo wordInfo, int occurrences)>();
            var wordCount = new Dictionary<string, int>();

            foreach (var word in wordInfos)
            {
                if (!wordCount.TryAdd(word.Text, 1))
                    wordCount[word.Text]++;
                else
                    uniqueWords.Add((word, 1));
            }

            for (int i = 0; i < uniqueWords.Count; i++)
            {
                uniqueWords[i] = (uniqueWords[i].wordInfo, wordCount[uniqueWords[i].wordInfo.Text]);
            }

            // Create a thread-safe dictionary to store results with their original index
            // var processedUniqueWords = new ConcurrentDictionary<int, DeckWord>();

            timer.Stop();
            double mecabTime = timer.Elapsed.TotalMilliseconds;

            timer.Restart();

            var processWords = uniqueWords.Select(word => ProcessWord(word, deconjugator)).ToList();

            var processedWords = await Task.WhenAll(processWords);
            processedWords = processedWords
                             .Where(result => result != null)
                             .Select(result => result!)
                             .ToArray();

            // Assign the occurences for each word
            foreach (var deckWord in processedWords)
            {
                // Remove one since we already counted it at the start
                deckWord.Occurrences--;
                deckWord.Occurrences +=
                    processedWords.Count(x => x.WordId == deckWord.WordId && x.ReadingIndex == deckWord.ReadingIndex);
            }

            // deduplicate deconjugated words
            processedWords = processedWords
                             .GroupBy(x => new { x.WordId, x.ReadingIndex })
                             .Select(x => x.First())
                             .ToArray();

            // Split into sentences
            string[] sentences = Regex.Split(text, @"(?<=[。！？」）])|(?<=[…—])\r\n");
            sentences = sentences.Select(sentence =>
            {
                // Find the first Japanese character
                Match match = Regex.Match(sentence, @"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]");
                if (match.Success)
                {
                    int startIndex = match.Index;
                    // Remove all special characters
                    return Regex.Replace(sentence.Substring(startIndex),
                                         "[^a-zA-Z0-9\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FAF\uFF21-\uFF3A\uFF41-\uFF5A\uFF10-\uFF19\u3005]",
                                         "");
                }

                return "";
            }).Where(s => !string.IsNullOrEmpty(s)).ToArray();

            timer.Stop();

            double deconjugationTime = timer.Elapsed.TotalMilliseconds;

            double totalTime = mecabTime + deconjugationTime;

            Console.WriteLine("Total words found : " + wordInfos.Count);

            // Console.WriteLine("Unique words found before deconjugation : " + uniqueWordInfos.Count);
            Console.WriteLine("Unique words found after deconjugation : " + processedWords.Length);

            var characterCount = wordInfos.Sum(x => x.Text.Length);
            Console.WriteLine($"Time elapsed: {totalTime:0.0}ms");
            Console.WriteLine($"Mecab time: {mecabTime:0.0}ms ({(mecabTime / totalTime * 100):0}%), Deconjugation time: {deconjugationTime:0.0}ms ({(deconjugationTime / totalTime * 100):0}%)");

            // Character count
            Console.WriteLine("Character count: " + characterCount);
            // Time for 10000 characters
            Console.WriteLine($"Time per 10000 characters: {(totalTime / characterCount * 10000):0.0}ms");
            // Time for 1million characters
            Console.WriteLine($"Time per 1 million characters: {(totalTime / characterCount * 1000000):0.0}ms");

            return new Deck
                   {
                       CharacterCount = characterCount, WordCount = wordInfos.Count, UniqueWordCount = processedWords.Length,
                       UniqueWordUsedOnceCount = processedWords.Count(x => x.Occurrences == 1),
                       UniqueKanjiCount = wordInfos.SelectMany(w => w.Text).Distinct().Count(c => WanaKana.IsKanji(c.ToString())),
                       UniqueKanjiUsedOnceCount = wordInfos.SelectMany(w => w.Text).GroupBy(c => c)
                                                           .Count(g => g.Count() == 1 && WanaKana.IsKanji(g.Key.ToString())),
                       SentenceCount = sentences.Length, DeckWords = processedWords
                   };
        }

        private static async Task<DeckWord?> ProcessWord((WordInfo wordInfo, int occurrences) wordData, Deconjugator deconjugator)
        {
            var cacheKey = new DeckWordCacheKey(
                                                wordData.wordInfo.Text,
                                                wordData.wordInfo.PartOfSpeech,
                                                wordData.wordInfo.DictionaryForm
                                               );

            if (UseCache && DeckWordCache.TryGetValue(cacheKey, out var cachedWord))
            {
                return new DeckWord
                       {
                           WordId = cachedWord.WordId, OriginalText = cachedWord.OriginalText, ReadingIndex = cachedWord.ReadingIndex,
                           Occurrences = wordData.occurrences
                       };
            }

            DeckWord? processedWord;


            bool isProcessed = false;

            do
            {
                if (wordData.wordInfo.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective
                        or PartOfSpeech.NaAdjective || wordData.wordInfo.PartOfSpeechSection1 is PartOfSpeechSection.Adjectival)
                {
                    if (!DeconjugateVerbOrAdjective(wordData, deconjugator, out processedWord))
                        DeconjugateWord(wordData, out processedWord); // The word might be a noun misparsed as a verb/adjective like お祭り
                }
                else
                {
                    DeconjugateWord(wordData, out processedWord);
                }

                if (processedWord != null) break;

                // We haven't found a match, let's try to remove the last character if it's a っ, a ー or a duplicate
                if (wordData.wordInfo.Text.Length > 2 &&
                    (wordData.wordInfo.Text[^1] == 'っ' || wordData.wordInfo.Text[^1] == 'ー' ||
                     wordData.wordInfo.Text[^2] == wordData.wordInfo.Text[^1]))
                {
                    wordData.wordInfo.Text = wordData.wordInfo.Text[..^1];
                }
                // Let's try to remove any honorifics in front of the word
                else if (wordData.wordInfo.Text.StartsWith("お"))
                {
                    wordData.wordInfo.Text = wordData.wordInfo.Text[1..];
                }
                // Let's try without any long vowel mark
                else if (wordData.wordInfo.Text.Contains("ー"))
                {
                    wordData.wordInfo.Text = wordData.wordInfo.Text.Replace("ー", "");
                }
                else
                {
                    isProcessed = true;
                }
            } while (!isProcessed);

            if (processedWord != null)
            {
                processedWord.Occurrences = wordData.occurrences;

                if (UseCache)
                {
                    DeckWordCache.TryAdd(cacheKey,
                                         new DeckWord
                                         {
                                             WordId = processedWord.WordId, OriginalText = processedWord.OriginalText,
                                             ReadingIndex = processedWord.ReadingIndex
                                         });
                }
            }

            return processedWord;
        }

        private static bool DeconjugateWord((WordInfo wordInfo, int occurrences) wordData, out DeckWord? processedWord)
        {
            var textInHiragana =
                WanaKana.ToHiragana(wordData.wordInfo.Text,
                                    new DefaultOptions { ConvertLongVowelMark = false });

            if (_lookups.TryGetValue(textInHiragana, out List<int> candidates))
            {
                candidates = candidates.OrderBy(c => c).ToList();

                JmDictWord? bestMatch = null;

                foreach (var id in candidates)
                {
                    if (!_allWords.TryGetValue(id, out var word)) continue;

                    // Initialize to the first word in case it fails
                    if (bestMatch == null)
                        bestMatch = word;

                    List<PartOfSpeech> pos = word.PartsOfSpeech.Select(x => x.ToPartOfSpeech())
                                                 .ToList();
                    if (!pos.Contains(wordData.wordInfo.PartOfSpeech)) continue;

                    bestMatch = word;
                    break;
                }

                if (bestMatch == null)
                {
                    if (!_allWords.TryGetValue(candidates[0], out bestMatch))
                    {
                        processedWord = null;
                        return true;
                    }
                }

                if (_specialCases.TryGetValue(textInHiragana, out int specialCaseId))
                {
                    bestMatch = _allWords[specialCaseId];
                }

                var normalizedReadings =
                    bestMatch.Readings.Select(r => WanaKana.ToHiragana(r, new DefaultOptions() { ConvertLongVowelMark = false }))
                             .ToList();
                byte readingIndex = (byte)normalizedReadings.IndexOf(textInHiragana);

                // not found, try with converting the long vowel mark
                if (readingIndex == 255)
                {
                    normalizedReadings =
                        bestMatch.Readings.Select(r => WanaKana.ToHiragana(r)).ToList();
                    readingIndex = (byte)normalizedReadings.IndexOf(textInHiragana);
                }

                DeckWord deckWord = new()
                                    {
                                        WordId = bestMatch.WordId, OriginalText = wordData.wordInfo.Text, ReadingIndex = readingIndex,
                                        Occurrences = wordData.occurrences
                                    };
                processedWord = deckWord;
                return true;
            }

            processedWord = null;
            return false;
        }

        private static bool DeconjugateVerbOrAdjective((WordInfo wordInfo, int occurrences) wordData, Deconjugator deconjugator,
                                                       out DeckWord? processedWord)
        {
            var deconjugated = deconjugator.Deconjugate(WanaKana.ToHiragana(wordData.wordInfo.Text))
                                           .Select(d => d.Text).ToList();
            deconjugated = deconjugated.OrderByDescending(d => d.Length).ToList();

            List<(string text, List<int> ids)> candidates = new();
            foreach (var form in deconjugated)
            {
                if (_lookups.TryGetValue(form, out List<int> lookup))
                {
                    // Reorder to have the most common words first. Might need a frequency list if it's not enough
                    var orderedLookup = lookup.OrderBy(l => l).ToList();
                    candidates.Add((form, orderedLookup));
                }
            }

            if (candidates.Count == 0)
            {
                processedWord = null;
                return true;
            }

            candidates = candidates.OrderByDescending(c => c.text.Length).ToList();

            // if there's a candidate that's the same as the base word, put it first in the list
            var baseDictionaryWord = WanaKana.ToHiragana(wordData.wordInfo.DictionaryForm);
            var baseDictionaryWordIndex = candidates.FindIndex(c => c.text == baseDictionaryWord);
            if (baseDictionaryWordIndex != -1)
            {
                var baseDictionaryWordCandidate = candidates[baseDictionaryWordIndex];
                candidates.RemoveAt(baseDictionaryWordIndex);
                candidates.Insert(0, baseDictionaryWordCandidate);
            }

            // if there's a candidate that's the same as the base word, put it first in the list
            var baseWord = WanaKana.ToHiragana(wordData.wordInfo.Text);
            var baseWordIndex = candidates.FindIndex(c => c.text == baseWord);
            if (baseWordIndex != -1)
            {
                var baseWordCandidate = candidates[baseWordIndex];
                candidates.RemoveAt(baseWordIndex);
                candidates.Insert(0, baseWordCandidate);
            }

            foreach (var candidate in candidates)
            {
                foreach (var id in candidate.ids)
                {
                    var currentId = id;
                    if (!_allWords.TryGetValue(currentId, out var word)) continue;

                    List<PartOfSpeech> pos = word.PartsOfSpeech.Select(x => x.ToPartOfSpeech())
                                                 .ToList();
                    if (!pos.Contains(wordData.wordInfo.PartOfSpeech)) continue;
                    
                    if (_specialCases.TryGetValue(candidate.text, out int specialCaseId))
                    {
                        word = _allWords[specialCaseId];
                        currentId = specialCaseId;
                    }

                    var normalizedReadings =
                        word.Readings.Select(r => WanaKana.ToHiragana(r, new DefaultOptions() { ConvertLongVowelMark = false })).ToList();
                    byte readingIndex = (byte)normalizedReadings.IndexOf(candidate.text);

                    // not found, try with converting the long vowel mark
                    if (readingIndex == 255)
                    {
                        normalizedReadings =
                            word.Readings.Select(r => WanaKana.ToHiragana(r)).ToList();
                        readingIndex = (byte)normalizedReadings.IndexOf(candidate.text);
                    }

                    DeckWord deckWord = new()
                                        {
                                            WordId = currentId, OriginalText = wordData.wordInfo.Text, ReadingIndex = readingIndex,
                                            Occurrences = wordData.occurrences
                                        };
                    processedWord = deckWord;
                    return true;
                }
            }

            processedWord = null;
            return false;
        }
    }
}