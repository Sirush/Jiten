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

        private static JitenDbContext _dbContext = null;

        public static async Task Main(string[] args)
        {
            // var text = "「あそこ美味しいよねー。早くお祭り終わって欲しいなー。ノンビリ遊びに行きたーい」";
            var text = await File.ReadAllTextAsync("Y:\\00_JapaneseStudy\\JL\\Backlogs\\Default_2024.12.28_10.52.47-2024.12.28_19.58.40.txt");

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

        public static async Task<Deck> ParseTextToDeck(JitenDbContext context, string text, bool storeRawText = false)
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
                       SentenceCount = sentences.Length, DeckWords = processedWords, RawText = storeRawText ? new DeckRawText(text) : null,
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
                           Occurrences = wordData.occurrences, Conjugations = cachedWord.Conjugations
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
                                             ReadingIndex = processedWord.ReadingIndex, Conjugations = processedWord.Conjugations
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

                List<JmDictWord> matches = new();
                JmDictWord? bestMatch = null;

                foreach (var id in candidates)
                {
                    if (!_allWords.TryGetValue(id, out var word)) continue;

                    List<PartOfSpeech> pos = word.PartsOfSpeech.Select(x => x.ToPartOfSpeech())
                                                 .ToList();
                    if (!pos.Contains(wordData.wordInfo.PartOfSpeech)) continue;

                    matches.Add(word);
                }

                if (matches.Count == 0)
                {
                    if (!_allWords.TryGetValue(candidates[0], out bestMatch))
                    {
                        processedWord = null;
                        return true;
                    }
                }
                else if (matches.Count > 1)
                    bestMatch = matches.OrderByDescending(m => m.GetPriorityScore(WanaKana.IsKana(wordData.wordInfo.Text))).First();
                else
                    bestMatch = matches[0];

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
                                           .OrderByDescending(d => d.Text.Length).ToList();

            List<(DeconjugationForm form, List<int> ids)> candidates = new();
            foreach (var form in deconjugated)
            {
                if (_lookups.TryGetValue(form.Text, out List<int> lookup))
                {
                    candidates.Add((form, lookup));
                }
            }

            if (candidates.Count == 0)
            {
                processedWord = null;
                return true;
            }

            // if there's a candidate that's the same as the base word, put it first in the list
            var baseDictionaryWord = WanaKana.ToHiragana(wordData.wordInfo.DictionaryForm);
            var baseDictionaryWordIndex = candidates.FindIndex(c => c.form.Text == baseDictionaryWord);
            if (baseDictionaryWordIndex != -1)
            {
                var baseDictionaryWordCandidate = candidates[baseDictionaryWordIndex];
                candidates.RemoveAt(baseDictionaryWordIndex);
                candidates.Insert(0, baseDictionaryWordCandidate);
            }

            // if there's a candidate that's the same as the base word, put it first in the list
            var baseWord = WanaKana.ToHiragana(wordData.wordInfo.Text);
            var baseWordIndex = candidates.FindIndex(c => c.form.Text == baseWord);
            if (baseWordIndex != -1)
            {
                var baseWordCandidate = candidates[baseWordIndex];
                candidates.RemoveAt(baseWordIndex);
                candidates.Insert(0, baseWordCandidate);
            }

            List<(JmDictWord word, DeconjugationForm form)> matches = new();
            (JmDictWord word, DeconjugationForm form) bestMatch;

            foreach (var candidate in candidates)
            {
                foreach (var id in candidate.ids)
                {
                    var currentId = id;
                    if (!_allWords.TryGetValue(currentId, out var word)) continue;

                    List<PartOfSpeech> pos = word.PartsOfSpeech.Select(x => x.ToPartOfSpeech())
                                                 .ToList();
                    if (!pos.Contains(wordData.wordInfo.PartOfSpeech)) continue;

                    matches.Add((word, candidate.form));
                }
            }

            if (matches.Count == 0)
            {
                processedWord = null;
                return false;
            }

            if (matches.Count > 1)
            {
                bestMatch = matches.OrderByDescending(m => m.Item1.GetPriorityScore(WanaKana.IsKana(wordData.wordInfo.Text))).First();

                if (!WanaKana.IsKana(wordData.wordInfo.NormalizedForm))
                {
                    foreach (var match in matches)
                    {
                        if (match.word.Readings.Any(r => r == wordData.wordInfo.NormalizedForm))
                        {
                            bestMatch = match;
                            break;
                        }
                    }
                }
            }
            else
                bestMatch = matches[0];

            var normalizedReadings =
                bestMatch.word.Readings.Select(r => WanaKana.ToHiragana(r, new DefaultOptions() { ConvertLongVowelMark = false })).ToList();
            byte readingIndex = (byte)normalizedReadings.IndexOf(bestMatch.form.Text);

            // not found, try with converting the long vowel mark
            if (readingIndex == 255)
            {
                normalizedReadings =
                    bestMatch.word.Readings.Select(r => WanaKana.ToHiragana(r)).ToList();
                readingIndex = (byte)normalizedReadings.IndexOf(bestMatch.form.Text);
            }

            DeckWord deckWord = new()
                                {
                                    WordId = bestMatch.word.WordId, OriginalText = wordData.wordInfo.Text, ReadingIndex = readingIndex,
                                    Occurrences = wordData.occurrences, Conjugations = bestMatch.form.Process
                                };
            processedWord = deckWord;
            return true;
        }
    }
}