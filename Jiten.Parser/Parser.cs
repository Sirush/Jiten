using System.Diagnostics;
using System.Text.RegularExpressions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Utils;
using Jiten.Parser.Data.Redis;
using Jiten.Parser.Diagnostics;
using Jiten.Parser.Grammar;
using Jiten.Parser.Resegmentation;
using Jiten.Parser.Resolution;
using Jiten.Parser.Runtime;
using Jiten.Parser.Scoring;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using WanaKanaShaapu;

namespace Jiten.Parser
{
    public static class Parser
    {
        private static readonly bool UseCache = true;

        private static readonly ParserRuntime _parserRuntime = new();
        private static IDeckWordCache DeckWordCache = null!;
        private static IJmDictCache JmDictCache = null!;

        private static IDbContextFactory<JitenDbContext> _contextFactory = null!;
        private static Dictionary<string, List<int>> _lookups = null!;
        private static Dictionary<int, int> _wordFrequencyRanks = null!;
        private static HashSet<int> _nameOnlyWordIds = null!;
        private static HashSet<int> _expressionWordIds = null!;
        private static HashSet<string> _ambiguousSurfaces = null!;
        private static readonly HashSet<char> SentenceEnders = ['。', '！', '？', '」'];

        private static readonly bool SelectiveSudachi =
            Environment.GetEnvironmentVariable("JITEN_SELECTIVE_SUDACHI") != "0"
            && BeamResegmentationEngine.IsPureIchiranMode;

        private static List<SentenceInfo> SplitTextIntoRawSentences(string text)
        {
            text = text.Replace("\r", "").Replace("\n", "");
            if (string.IsNullOrEmpty(text)) return [];

            var result = new List<SentenceInfo>();
            int sentenceStart = 0;
            bool seenEnder = false;

            for (int i = 0; i < text.Length; i++)
            {
                if (SentenceEnders.Contains(text[i]))
                {
                    seenEnder = true;
                    continue;
                }

                if (seenEnder)
                {
                    var sentenceText = text[sentenceStart..i];
                    if (sentenceText.Length > 0)
                    {
                        var si = new SentenceInfo(sentenceText);
                        si.Words.Add((new WordInfo { Text = sentenceText, PartOfSpeech = PartOfSpeech.Unknown }, 0, sentenceText.Length));
                        result.Add(si);
                    }
                    sentenceStart = i;
                    seenEnder = false;
                }
            }

            var remaining = text[sentenceStart..];
            if (remaining.Length > 0)
            {
                var si = new SentenceInfo(remaining);
                si.Words.Add((new WordInfo { Text = remaining, PartOfSpeech = PartOfSpeech.Unknown }, 0, remaining.Length));
                result.Add(si);
            }

            return result;
        }

        // Cache for compound expression lookups with bounded eviction
        private static readonly Dictionary<string, (bool validExpression, int? wordId)> CompoundExpressionCache = new();
        private static readonly LinkedList<string> CompoundCacheOrder = new(); // Tracks insertion order for LRU eviction
        private static readonly Lock CompoundCacheLock = new();
        private const int MAX_COMPOUND_CACHE_SIZE = 200_000;
        private const int EVICTION_BATCH_SIZE = 50_000; // Remove oldest 25% when limit hit

        // Compiled regexes for token cleaning (avoid recompilation on every token)
        private static readonly Regex TokenCleanRegex = new(
                                                            @"[^a-zA-Z0-9\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FAF\uFF21-\uFF3A\uFF41-\uFF5A\uFF10-\uFF19\u3005．]",
                                                            RegexOptions.Compiled);

        private static readonly Regex SmallTsuLongVowelRegex = new(@"ッー", RegexOptions.Compiled);
        private static readonly Regex DialogueRegex = new(@"[「『].{0,200}?[」』]", RegexOptions.Compiled | RegexOptions.Singleline);

        // Surface-text misparse tokens to remove after repair stages (deferred from MorphologicalAnalyser
        // so RepairLongVowelMisparses can use them for backward-merge reconstruction)
        private static readonly HashSet<string> MisparsesRemove =
        [
            "そ", "る", "ま", "ふ", "ち", "ほ", "す", "じ", "なさ", "い", "ぴ", "ふあ", "ぷ", "ちゅ", "にっ", "じら", "タ", "け", "イ", "イッ", "ほっ", "そっ",
            "ウー", "うー", "ううう", "うう", "ウウウウ", "ウウ", "ううっ", "かー", "ぐわー", "違", "タ", "ッ"
        ];

        // Excluded (WordId, ReadingIndex) pairs filtered from final parsing results.
        // Source of truth: Shared/resources/excluded_readings.json (see ExcludedReadings loader).
        private static HashSet<(int WordId, byte ReadingIndex)> ExcludedMisparses => ExcludedReadings.Set;

        public static async Task WarmupAsync(IDbContextFactory<JitenDbContext> contextFactory, Action<string>? log = null)
        {
            await EnsureInitializedAsync(contextFactory, log);
        }

        private static async Task EnsureInitializedAsync(IDbContextFactory<JitenDbContext> contextFactory, Action<string>? log = null)
        {
            _contextFactory = contextFactory;
            var runtime = await _parserRuntime.EnsureInitializedAsync(contextFactory, log);
            DeckWordCache = runtime.DeckWordCache;
            JmDictCache = runtime.JmDictCache;
            _lookups = runtime.Lookups;
            _wordFrequencyRanks = runtime.WordFrequencyRanks;
            _nameOnlyWordIds = runtime.NameOnlyWordIds;
            _expressionWordIds = runtime.ExpressionWordIds;
            _ambiguousSurfaces = runtime.AmbiguousSurfaces;
            Console.Error.WriteLine($"[parser-init] SelectiveSudachi={SelectiveSudachi} ambiguousSurfaces={_ambiguousSurfaces?.Count ?? 0}");
        }

        // Returns the candidate provider the beam should use. The beam always
        // prefers the pre-materialised jmdict."ConjugatedForms" table; every
        // subsequent parse reuses the loaded table. On any load failure we fall
        // back to the runtime deconjugator so the parser still works even if the
        // table is missing or stale.
        //
        // Cached once per process so surface caches inside the fallback
        // DeconjugatorCandidateProvider survive across texts — the per-sentence
        // SentenceSurfaceCache wrapper still scopes the hot-path cache, but the
        // underlying deconjugator memo amortises across every text in the batch.
        private static ICandidateProvider? _beamProviderCached;
        private static readonly SemaphoreSlim _beamProviderInit = new(1, 1);
        private static async Task<ICandidateProvider> GetBeamCandidateProviderAsync()
        {
            if (_beamProviderCached != null) return _beamProviderCached;
            await _beamProviderInit.WaitAsync();
            try
            {
                if (_beamProviderCached != null) return _beamProviderCached;
                try
                {
                    await ConjugationTableLoader.EnsureLoadedAsync(_contextFactory);
                    var table = ConjugationTableLoader.Table;
                    if (table != null)
                    {
                        var wordIds = table.GetUniqueWordIds();
                        foreach (var ids in _lookups.Values)
                            foreach (var id in ids)
                                wordIds.Add(id);
                        _ = Task.Run(async () =>
                        {
                            try { await JmDictCache.PreloadWordsAsync(wordIds); }
                            catch (Exception ex) { Console.Error.WriteLine($"[BeamWordPreload] Background preload failed: {ex.Message}"); }
                        });
                        return _beamProviderCached = new TableCandidateProvider(table, _lookups);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Parser] Conjugation table load failed, falling back to DeconjugatorCandidateProvider: {ex.Message}");
                }

                return _beamProviderCached = new DeconjugatorCandidateProvider(_lookups, Deconjugator.Instance);
            }
            finally
            {
                _beamProviderInit.Release();
            }
        }

        private static async Task PreprocessSentences(List<SentenceInfo> sentences,
                                                      ParserDiagnostics? diagnostics = null)
        {
            CleanSentenceTokens(sentences);
            SplitSuruInflectionsForNounCompounding(sentences);
            SplitUnknownNounTokens(sentences);
            CombineNounCompounds(sentences);
            await CombineCompounds(sentences);
            CombineExpressions(sentences);
            RepairLongVowelMisparses(sentences);
            RepairLongVowelTokens(sentences);
            FilterOrphanedMisparses(sentences);
            ValidateGrammaticalSequences(sentences, diagnostics);
            StripTrailingParticles(sentences);
            await ResegmentationEngine.TryImproveUncertainSpans(sentences, _lookups, _wordFrequencyRanks, JmDictCache, diagnostics);
            MarkPersonNameHonorificContexts(sentences);
        }

        internal static readonly HashSet<string> ArchaicPosTypes =
        [
            "v2a-s", "v2b-k", "v2b-s", "v2d-k", "v2d-s", "v2g-k", "v2g-s",
            "v2h-k", "v2h-s", "v2k-k", "v2k-s", "v2m-k", "v2m-s", "v2n-s",
            "v2r-k", "v2r-s", "v2s-s", "v2t-k", "v2t-s", "v2w-s", "v2y-k",
            "v2y-s", "v2z-s",
            "v4b", "v4g", "v4h", "v4k", "v4m", "v4n", "v4r", "v4s", "v4t",
            "adj-kari", "adj-ku", "adj-shiku"
        ];

        // Unambiguously classical-Japanese surface forms used as sentence-level archaic context markers.
        // ぬ and べき deliberately excluded — too common in modern literary/formal prose.
        private static readonly HashSet<string> ClassicalMarkerSurfaces =
        [
            "けり", "けれ", // classical past/retrospective auxiliary
            "べし", "べく", // classical necessity/conjecture
            "ごとし", "ごとく", "ごとき", // classical similarity auxiliary
            "まほし", "まほしく", // classical desiderative
            "たまふ", "たまへ", "たまひ", // classical honorific verb (v4h)
        ];

        private static bool IsClassicalSentence(
            IReadOnlyList<(WordInfo word, DeckWord? result, int? margin)> sentenceWords)
            => sentenceWords.Any(w => ClassicalMarkerSurfaces.Contains(w.word.Text));

        private static readonly HashSet<string> PersonHonorifics = ["さん", "ちゃん", "くん", "氏", "様"];

        private static readonly HashSet<string> HonorificExclusions =
        [
            "うさぎ", "ウサギ", "兎",
            "くま", "クマ", "熊",
            "たぬき", "タヌキ", "狸",
            "きつね", "キツネ", "狐",
            "さる", "サル", "猿",
            "つる", "ツル", "鶴",
            "かめ", "カメ", "亀",
            "ねずみ", "ネズミ", "鼠",
        ];

        private static void EnrichWithSudachiReadings(SentenceInfo sentence, List<(WordInfo word, int position, int length)> sudachiTokens)
        {
            foreach (var (beamWord, beamPos, beamLen) in sentence.Words)
            {
                if (!_ambiguousSurfaces.Contains(beamWord.Text)) continue;
                if (!string.IsNullOrEmpty(beamWord.Reading)) continue;

                foreach (var (sudWord, sudPos, sudLen) in sudachiTokens)
                {
                    if (sudPos == beamPos && sudLen == beamLen)
                    {
                        if (!string.IsNullOrEmpty(sudWord.Reading))
                            beamWord.Reading = sudWord.Reading;
                        if (!string.IsNullOrEmpty(sudWord.DictionaryForm))
                            beamWord.DictionaryForm = sudWord.DictionaryForm;
                        if (!string.IsNullOrEmpty(sudWord.NormalizedForm))
                            beamWord.NormalizedForm = sudWord.NormalizedForm;
                        if (sudWord.PartOfSpeech != PartOfSpeech.Unknown)
                            beamWord.PartOfSpeech = sudWord.PartOfSpeech;
                        break;
                    }
                }
            }
        }

        private static void MarkPersonNameHonorificContexts(List<SentenceInfo> sentences)
        {
            foreach (var sentence in sentences)
            {
                if (sentence.Words.Count < 2)
                    continue;

                for (int i = 0; i < sentence.Words.Count - 1; i++)
                {
                    var current = sentence.Words[i].word;
                    var next = sentence.Words[i + 1].word;

                    if (!PersonHonorifics.Contains(next.Text))
                        continue;

                    if (HonorificExclusions.Contains(current.Text))
                        continue;

                    // Pure-katakana nouns are almost always foreign names when followed by a person honorific.
                    // Sudachi doesn't classify foreign names as proper nouns, so we skip the strict POS check.
                    if (current.PartOfSpeech == PartOfSpeech.Noun && WanaKana.IsKatakana(current.Text))
                    {
                        current.IsPersonNameContext = true;
                        continue;
                    }

                    if (!PosMapper.IsNameLikeSudachiNoun(
                                                         current.PartOfSpeech,
                                                         current.PartOfSpeechSection1,
                                                         current.PartOfSpeechSection2,
                                                         current.PartOfSpeechSection3))
                        continue;

                    if (!current.HasPartOfSpeechSection(PartOfSpeechSection.PersonName) &&
                        !current.HasPartOfSpeechSection(PartOfSpeechSection.FamilyName) &&
                        !current.HasPartOfSpeechSection(PartOfSpeechSection.Name))
                        continue;

                    current.IsPersonNameContext = true;
                }
            }
        }

        private static void PropagatePersonNameContexts(List<List<SentenceInfo>> allTexts)
        {
            var confirmedNames = new HashSet<string>();

            foreach (var sentences in allTexts)
            foreach (var sentence in sentences)
            foreach (var (word, _, _) in sentence.Words)
                if (word.IsPersonNameContext)
                    confirmedNames.Add(word.Text);

            if (confirmedNames.Count == 0)
                return;

            foreach (var sentences in allTexts)
            foreach (var sentence in sentences)
            foreach (var (word, _, _) in sentence.Words)
            {
                if (word.IsPersonNameContext)
                    continue;

                if (!confirmedNames.Contains(word.Text))
                    continue;

                if (!PosMapper.IsNameLikeSudachiNoun(
                                                     word.PartOfSpeech,
                                                     word.PartOfSpeechSection1,
                                                     word.PartOfSpeechSection2,
                                                     word.PartOfSpeechSection3))
                    continue;

                if (!word.HasPartOfSpeechSection(PartOfSpeechSection.PersonName) &&
                    !word.HasPartOfSpeechSection(PartOfSpeechSection.FamilyName) &&
                    !word.HasPartOfSpeechSection(PartOfSpeechSection.Name))
                    continue;

                word.IsPersonNameContext = true;
            }
        }

        private static void PropagatePersonNameContexts(List<SentenceInfo> sentences)
        {
            PropagatePersonNameContexts(new List<List<SentenceInfo>> { sentences });
        }

        /// <summary>
        /// Sudachi + CombineInflections can merge a suru-noun with its inflection (e.g., 交換 + して → 交換して),
        /// which can prevent later noun compounding (e.g., 物々 + 交換して instead of 物々交換 + して).
        /// This pass selectively splits tokens like Xして into X + して ONLY when doing so enables a valid noun compound
        /// with preceding noun tokens (validated against the JMDict lookups table).
        /// </summary>
        private static void SplitSuruInflectionsForNounCompounding(List<SentenceInfo> sentences)
        {
            static string? GetSuruSplitSuffixPrefix(string surface)
            {
                // Keep this intentionally small to avoid creating standalone auxiliary chains.
                // We only need the core suru te-form/past forms for noun-compound recovery.
                // Note: we match as a PREFIX so this also works when the te-form is followed by auxiliaries
                // (e.g., 交換しておかない).
                if (surface.StartsWith("して", StringComparison.Ordinal)) return "して";
                if (surface.StartsWith("した", StringComparison.Ordinal)) return "した";
                if (surface.StartsWith("し", StringComparison.Ordinal)) return "し";
                return null;
            }

            static bool HasLookup(string key, Dictionary<string, List<int>> lookups)
            {
                if (lookups.TryGetValue(key, out var ids) && ids.Count > 0)
                    return true;

                var hiraganaKey = KanaConverter.ToHiragana(key, convertLongVowelMark: false);
                return hiraganaKey != key &&
                       lookups.TryGetValue(hiraganaKey, out ids) &&
                       ids.Count > 0;
            }

            foreach (var sentence in sentences)
            {
                if (sentence.Words.Count < 2)
                    continue;

                for (int i = 1; i < sentence.Words.Count; i++)
                {
                    var (word, position, length) = sentence.Words[i];

                    if (string.IsNullOrEmpty(word.DictionaryForm))
                        continue;

                    string baseNoun;
                    if (word.DictionaryForm.EndsWith("する", StringComparison.Ordinal) && word.DictionaryForm.Length > 2)
                    {
                        // e.g., 交換する / 勉強する
                        baseNoun = word.DictionaryForm[..^2];
                    }
                    else if (word.HasPartOfSpeechSection(PartOfSpeechSection.PossibleSuru))
                    {
                        // Some upstream combiners (e.g., CombineVerbDependantsSuru) keep DictionaryForm as the noun stem
                        // even when Text includes the suru inflection chain (e.g., 交換してる with DictionaryForm=交換).
                        baseNoun = word.DictionaryForm;
                    }
                    else
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(baseNoun))
                        continue;

                    if (!word.Text.StartsWith(baseNoun, StringComparison.Ordinal))
                        continue;

                    var suffixSurface = word.Text[baseNoun.Length..];
                    var allowedSuffix = GetSuruSplitSuffixPrefix(suffixSurface);
                    if (allowedSuffix == null)
                        continue;

                    // Find whether splitting enables a valid noun compound with preceding nouns
                    // Try longest window first: up to 4 preceding noun tokens + baseNoun = 5-token compound.
                    int bestStart = -1;
                    for (int windowSize = Math.Min(5, i + 1); windowSize >= 2; windowSize--)
                    {
                        int nounCount = windowSize - 1;
                        int start = i - nounCount;
                        if (start < 0) continue;

                        bool allNouns = true;
                        for (int j = start; j < i; j++)
                        {
                            if (!PosMapper.IsNounForCompounding(sentence.Words[j].word.PartOfSpeech))
                            {
                                allNouns = false;
                                break;
                            }
                        }

                        if (!allNouns) continue;

                        var prefix = string.Concat(sentence.Words.Skip(start).Take(nounCount).Select(w => w.word.Text));
                        var combined = prefix + baseNoun;
                        if (HasLookup(combined, _lookups))
                        {
                            bestStart = start;
                            break;
                        }
                    }

                    if (bestStart == -1)
                        continue;

                    // Split into base noun + suru inflection (+ optional tail).
                    // We intentionally avoid trying to preserve Sudachi readings here:
                    // merged tokens often carry only the accumulator reading, and suffixes may not be at the end.
                    var nounWord = new WordInfo(word)
                                   {
                                       Text = baseNoun, PartOfSpeech = PartOfSpeech.Noun, DictionaryForm = baseNoun,
                                       // Keep NormalizedForm from Sudachi (often important for matching); fall back to base noun.
                                       NormalizedForm = string.IsNullOrEmpty(word.NormalizedForm) ? baseNoun : word.NormalizedForm,
                                       Reading = KanaConverter.ToHiragana(baseNoun, convertLongVowelMark: false)
                                   };

                    var suruWord = new WordInfo
                                   {
                                       // Keep the full surface (e.g., しておかない / してる) as a single token.
                                       // This preserves auxiliary chains as one unit while enabling noun compounding.
                                       Text = suffixSurface, PartOfSpeech = PartOfSpeech.Verb, DictionaryForm = "する", NormalizedForm = "する",
                                       Reading = KanaConverter.ToHiragana(suffixSurface, convertLongVowelMark: false)
                                   };

                    int baseLen = nounWord.Text.Length;
                    int suffixLen = suruWord.Text.Length;

                    sentence.Words[i] = (nounWord, position, baseLen);
                    sentence.Words.Insert(i + 1, (suruWord, position + baseLen, suffixLen));
                    i++; // Skip inserted suffix
                }
            }
        }

        private static List<WordInfo> ExtractWordInfos(List<SentenceInfo> sentences)
        {
            return sentences.SelectMany(s => s.Words)
                            .Where(w => w.word.PartOfSpeech != PartOfSpeech.SupplementarySymbol)
                            .Select(w => w.word)
                            .ToList();
        }

        private static List<(WordInfo wordInfo, int occurrences)> CountUniqueWords(List<WordInfo> wordInfos)
        {
            var uniqueWords = new List<(WordInfo wordInfo, int occurrences)>();
            var wordCount =
                new Dictionary<(string text, PartOfSpeech pos, string dictionaryForm, string reading, bool isPersonNameContext, bool
                    isNameLikeSudachiNoun),
                    int>();

            foreach (var word in wordInfos)
            {
                var isNameLikeSudachiNoun = PosMapper.IsNameLikeSudachiNoun(word.PartOfSpeech, word.PartOfSpeechSection1,
                                                                            word.PartOfSpeechSection2, word.PartOfSpeechSection3);
                var key = (word.Text, word.PartOfSpeech, word.DictionaryForm, word.Reading, word.IsPersonNameContext,
                           isNameLikeSudachiNoun);

                if (!wordCount.TryAdd(key, 1))
                {
                    wordCount[key]++;
                }
                else
                {
                    uniqueWords.Add((word, 1));
                }
            }

            for (int i = 0; i < uniqueWords.Count; i++)
            {
                var wi = uniqueWords[i].wordInfo;
                var isNameLikeSudachiNoun = PosMapper.IsNameLikeSudachiNoun(wi.PartOfSpeech, wi.PartOfSpeechSection1,
                                                                            wi.PartOfSpeechSection2, wi.PartOfSpeechSection3);
                var key = (wi.Text, wi.PartOfSpeech, wi.DictionaryForm, wi.Reading, wi.IsPersonNameContext, isNameLikeSudachiNoun);
                uniqueWords[i] = (uniqueWords[i].wordInfo, wordCount[key]);
            }

            return uniqueWords;
        }

        private static async Task<List<(DeckWord? word, int? margin)>> ProcessWordsInBatches(
            List<(WordInfo wordInfo, int occurrences)> words,
            Deconjugator deconjugator,
            int batchSize = 1000,
            ParserDiagnostics? diagnostics = null)
        {
            List<(DeckWord? word, int? margin)> allProcessedWords = new();

            for (int i = 0; i < words.Count; i += batchSize)
            {
                var batch = words.GetRange(i, Math.Min(batchSize, words.Count - i));

                // Pre-fetch all DeckWord cache entries for this batch via MGET
                Dictionary<DeckWordCacheKey, DeckWord?>? prefetchedCache = null;
                if (UseCache && diagnostics == null)
                {
                    try
                    {
                        var cacheKeys = new List<DeckWordCacheKey>(batch.Count);
                        foreach (var word in batch)
                        {
                            var wi = word.wordInfo;
                            var textWithoutBar = wi.Text.TrimEnd('ー');
                            if (textWithoutBar.Length > 0 && textWithoutBar.All(char.IsDigit) ||
                                (textWithoutBar.Length == 1 && textWithoutBar.IsAsciiOrFullWidthLetter()))
                                continue;

                            var isNameLike = PosMapper.IsNameLikeSudachiNoun(wi.PartOfSpeech, wi.PartOfSpeechSection1,
                                                                             wi.PartOfSpeechSection2, wi.PartOfSpeechSection3);
                            cacheKeys.Add(new DeckWordCacheKey(wi.Text, wi.PartOfSpeech, wi.DictionaryForm,
                                                               wi.Reading, wi.IsPersonNameContext, isNameLike));
                        }

                        if (cacheKeys.Count > 0)
                            prefetchedCache = await DeckWordCache.GetManyAsync(cacheKeys);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Warning] Failed to pre-fetch DeckWordCache: {ex.Message}");
                    }
                }

                var processBatch = batch.Select(word => ProcessWord(word, deconjugator, prefetchedCache, diagnostics)).ToList();
                var batchResults = await Task.WhenAll(processBatch);

                // Batch-write newly resolved cache entries
                if (UseCache && diagnostics == null)
                {
                    try
                    {
                        var cacheEntries = new List<(DeckWordCacheKey key, DeckWord word)>();
                        foreach (var result in batchResults)
                        {
                            if (result is { CacheKey: not null, CacheWord: not null })
                                cacheEntries.Add((result.CacheKey, result.CacheWord));
                        }

                        if (cacheEntries.Count > 0)
                            await DeckWordCache.SetManyAsync(cacheEntries, CommandFlags.FireAndForget);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Warning] Failed to batch-write DeckWordCache: {ex.Message}");
                    }
                }

                for (int j = 0; j < batch.Count; j++)
                    allProcessedWords.Add((batchResults[j].Word, batchResults[j].Margin));
            }

            return allProcessedWords;
        }

        public static async Task<List<DeckWord>> ParseText(IDbContextFactory<JitenDbContext> contextFactory, string text,
                                                           bool preserveStopToken = false,
                                                           ParserDiagnostics? diagnostics = null)
        {
            await EnsureInitializedAsync(contextFactory);

            var parser = new MorphologicalAnalyser { HasCompoundLookup = HasLookupForCompound };
            var sentences = await parser.Parse(text, preserveStopToken: preserveStopToken, diagnostics: diagnostics);

            // Beam-first: the Ichiran beam owns segmentation. Sudachi supplies only
            // morphology metadata on boundaries that happen to align in writeback.
            // PreprocessSentences / initial lookup / pre-beam resegmentation are
            // intentionally absent — they would contaminate the lattice with
            // Sudachi-normalisation opinions. See PLAN_BeamPhase1_Ports.md.
            var beamProvider = await GetBeamCandidateProviderAsync();
            await BeamResegmentationEngine.ResegmentSentences(
                sentences, beamProvider, _lookups, _wordFrequencyRanks, JmDictCache,
                resolvedWordIdLookup: null, diagnostics);

            MarkPersonNameHonorificContexts(sentences);
            PropagatePersonNameContexts(sentences);

            var wordInfos = ExtractWordInfos(sentences);
            var wordsWithOccurrences = wordInfos.Select(w => (w, 0)).ToList();
            var processedWithMargins = await ProcessWordsInBatches(wordsWithOccurrences, Deconjugator.Instance, diagnostics: diagnostics);

            var corrected = await ApplyAdjacentScoring(sentences, processedWithMargins, diagnostics);
            return ExcludeFinalMisparses(corrected);
        }

        /// <summary>
        /// Parses a single text into a Deck. Delegates to ParseTextsToDeck for a single codepath.
        /// </summary>
        public static async Task<Deck> ParseTextToDeck(IDbContextFactory<JitenDbContext> contextFactory, string text,
                                                       bool storeRawText = false,
                                                       bool predictDifficulty = true,
                                                       MediaType mediatype = MediaType.Novel,
                                                       ParserDiagnostics? diagnostics = null)
        {
            var results = await ParseTextsToDeck(contextFactory, [text], storeRawText, predictDifficulty, mediatype, diagnostics);
            return results.Count > 0 ? results[0] : new Deck();
        }

        /// <summary>
        /// Parses multiple texts into Decks in a single batch for efficiency.
        /// </summary>
        public static async Task<List<Deck>> ParseTextsToDeck(IDbContextFactory<JitenDbContext> contextFactory,
                                                              List<string> texts,
                                                              bool storeRawText = false,
                                                              bool predictDifficulty = true,
                                                              MediaType mediatype = MediaType.Novel,
                                                              ParserDiagnostics? diagnostics = null)
        {
            if (texts.Count == 0) return [];

            await EnsureInitializedAsync(contextFactory);

            var timer = new Stopwatch();
            timer.Start();

            List<List<SentenceInfo>> batchedSentences;
            var sudachiStart = ParserBenchmarkInstrumentation.Now();

            if (SelectiveSudachi && diagnostics == null)
            {
                batchedSentences = texts.Select(SplitTextIntoRawSentences).ToList();
                ParserBenchmarkInstrumentation.AddSudachi(sudachiStart);
                timer.Stop();
                if (!ParserBenchmarkInstrumentation.SuppressStageLogs)
                    Console.WriteLine($"Text-split {texts.Count} texts (selective Sudachi). Time: {timer.Elapsed.TotalMilliseconds:0.0}ms");
            }
            else
            {
                var parser = new MorphologicalAnalyser { HasCompoundLookup = HasLookupForCompound };
                batchedSentences = await parser.ParseBatch(texts, diagnostics: diagnostics);
                ParserBenchmarkInstrumentation.AddSudachi(sudachiStart);
                timer.Stop();
                if (!ParserBenchmarkInstrumentation.SuppressStageLogs)
                    Console.WriteLine($"Batch parsed {texts.Count} texts. Sudachi time: {timer.Elapsed.TotalMilliseconds:0.0}ms");
            }

            timer.Restart();

            // Beam-first: PreprocessSentences and PropagatePersonNameContexts are deferred
            // until AFTER the beam has produced its own segmentation (see ProcessSentencesToDeck).
            // Running them here would feed the beam a Jiten-rewritten lattice — the exact
            // contamination we're eliminating.

            // Pass 2: Process each preprocessed result through deconjugation/lookup pipeline
            var decks = new List<Deck>();
            Deconjugator deconjugator = Deconjugator.Instance;

            for (int textIndex = 0; textIndex < batchedSentences.Count; textIndex++)
            {
                var sentences = batchedSentences[textIndex];
                var text = texts[textIndex];
                var deck = await ProcessSentencesToDeck(sentences, text, deconjugator, storeRawText, predictDifficulty, mediatype);
                decks.Add(deck);
                batchedSentences[textIndex] = null!;
            }

            timer.Stop();
            if (!ParserBenchmarkInstrumentation.SuppressStageLogs)
                Console.WriteLine($"Processing time: {timer.Elapsed.TotalMilliseconds:0.0}ms");

            return decks;
        }

        /// <summary>
        /// Helper: Processes sentences into a Deck (deconjugation, statistics).
        /// Extracted to share between batch and single-item paths.
        /// </summary>
        private static async Task<Deck> ProcessSentencesToDeck(
            List<SentenceInfo> sentences,
            string text,
            Deconjugator deconjugator,
            bool storeRawText,
            bool predictDifficulty,
            MediaType mediatype)
        {
            // Beam-first: run the beam against raw Sudachi output (PreprocessSentences
            // was intentionally skipped upstream), then mark person-name contexts on
            // the beam-chosen token stream, then do a single lookup pass.
            var resegStart = ParserBenchmarkInstrumentation.Now();
            var beamProvider = await GetBeamCandidateProviderAsync();
            await BeamResegmentationEngine.ResegmentSentences(
                sentences, beamProvider, _lookups, _wordFrequencyRanks, JmDictCache,
                resolvedWordIdLookup: null);
            ParserBenchmarkInstrumentation.AddResegmentation(resegStart);

            if (SelectiveSudachi && _ambiguousSurfaces is { Count: > 0 })
            {
                var sudachiNeeded = new List<int>();
                for (int si = 0; si < sentences.Count; si++)
                    foreach (var (word, _, _) in sentences[si].Words)
                        if (_ambiguousSurfaces.Contains(word.Text))
                        {
                            sudachiNeeded.Add(si);
                            break;
                        }

                int ambChars = sudachiNeeded.Sum(i => sentences[i].Text.Length);
                var triggerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var si in sudachiNeeded)
                    foreach (var (word, _, _) in sentences[si].Words)
                        if (_ambiguousSurfaces.Contains(word.Text))
                        {
                            triggerCounts.TryGetValue(word.Text, out var c);
                            triggerCounts[word.Text] = c + 1;
                        }
                var topTriggers = triggerCounts.OrderByDescending(kv => kv.Value).Take(20)
                    .Select(kv => $"{kv.Key}({kv.Value})");
                Console.Error.WriteLine($"[selective-sudachi] {sudachiNeeded.Count}/{sentences.Count} sentences need Sudachi ({ambChars} chars) top: {string.Join(" ", topTriggers)}");

                if (sudachiNeeded.Count > 0)
                {
                    var sudStart = ParserBenchmarkInstrumentation.Now();
                    var ambTexts = sudachiNeeded.Select(i => sentences[i].Text).ToList();
                    var sudParser = new MorphologicalAnalyser { HasCompoundLookup = HasLookupForCompound };
                    var sudResult = await sudParser.ParseBatch(ambTexts);
                    ParserBenchmarkInstrumentation.AddSudachi(sudStart);

                    for (int k = 0; k < sudachiNeeded.Count && k < sudResult.Count; k++)
                    {
                        var si = sudachiNeeded[k];
                        var sudachiTokens = sudResult[k].SelectMany(s => s.Words).ToList();
                        EnrichWithSudachiReadings(sentences[si], sudachiTokens);
                    }
                }
            }

            MarkPersonNameHonorificContexts(sentences);

            var wordInfos = ExtractWordInfos(sentences);
            var uniqueWords = CountUniqueWords(wordInfos);

            var deconjLookupStart = ParserBenchmarkInstrumentation.Now();
            var allProcessedWithMargins = await ProcessWordsInBatches(uniqueWords, deconjugator);
            ParserBenchmarkInstrumentation.AddDeconjugationLookup(deconjLookupStart);

            var resultLookup = new Dictionary<(string, PartOfSpeech, string, string, bool, bool), (DeckWord? word, int? margin)>();
            for (int i = 0; i < uniqueWords.Count; i++)
            {
                var key = GetDedupKey(uniqueWords[i].wordInfo);
                resultLookup.TryAdd(key, allProcessedWithMargins[i]);
            }

            var adjStart = ParserBenchmarkInstrumentation.Now();
            var corrected = await ApplyAdjacentScoring(sentences, resultLookup);
            ParserBenchmarkInstrumentation.AddAdjacentScoring(adjStart);

            // Sum occurrences while deduplicating by WordId and ReadingIndex
            var processedWords = corrected
                                 .GroupBy(x => new { x.WordId, x.ReadingIndex })
                                 .Select(g =>
                                 {
                                     var first = g.First();
                                     first.Occurrences = g.Count();
                                     return first;
                                 })
                                 .ToArray();

            processedWords = ExcludeFinalMisparses(processedWords).ToArray();

            List<ExampleSentence>? exampleSentences = null;

            if (mediatype is MediaType.Novel or MediaType.NonFiction or MediaType.VideoGame or MediaType.VisualNovel or MediaType.WebNovel)
                exampleSentences = ExampleSentenceExtractor.ExtractSentences(sentences, processedWords);

            var totalWordCount = processedWords.Select(w => w.Occurrences).Sum();
            var characterCount = wordInfos.Sum(x => x.Text.Length);

            var textWithoutDialogues = DialogueRegex.Replace(text, "");
            textWithoutDialogues = TokenCleanRegex.Replace(textWithoutDialogues, "");
            var textWithoutPunctuation = TokenCleanRegex.Replace(text, "");

            int dialogueCharacterCount = textWithoutPunctuation.Length - textWithoutDialogues.Length;
            float dialoguePercentage = textWithoutPunctuation.Length > 0
                ? (float)dialogueCharacterCount / textWithoutPunctuation.Length * 100f
                : 0f;

            var deck = new Deck
                       {
                           CharacterCount = characterCount, WordCount = totalWordCount, UniqueWordCount = processedWords.Length,
                           UniqueWordUsedOnceCount = processedWords.Count(x => x.Occurrences == 1),
                           UniqueKanjiCount = wordInfos.SelectMany(w => w.Text).Distinct().Count(c => WanaKana.IsKanji(c.ToString())),
                           UniqueKanjiUsedOnceCount = wordInfos.SelectMany(w => w.Text).GroupBy(c => c)
                                                               .Count(g => g.Count() == 1 && WanaKana.IsKanji(g.Key.ToString())),
                           SentenceCount = sentences.Count, DialoguePercentage = dialoguePercentage, DeckWords = processedWords,
                           RawText = storeRawText ? new DeckRawText(text) : null, ExampleSentences = exampleSentences
                       };

            return deck;
        }

        public static async Task<List<DeckWord?>> ParseMorphenes(IDbContextFactory<JitenDbContext> contextFactory, string text,
                                                                 ParserDiagnostics? diagnostics = null)
        {
            await EnsureInitializedAsync(contextFactory);

            var parser = new MorphologicalAnalyser { HasCompoundLookup = HasLookupForCompound };
            var sentences = await parser.Parse(text, morphemesOnly: true, diagnostics: diagnostics);
            var wordInfos = sentences.SelectMany(s => s.Words).Select(w => w.word).ToList();

            wordInfos.ForEach(x => x.Text = SmallTsuLongVowelRegex.Replace(x.Text, ""));
            var wordsWithOccurrences = wordInfos.Select(w => (w, 0)).ToList();

            var processedWithMargins = await ProcessWordsInBatches(wordsWithOccurrences, Deconjugator.Instance, batchSize: 5000);

            return processedWithMargins
                   .Select(p => p.word)
                   .Where(w => w == null || !ExcludedMisparses.Contains((w.WordId, w.ReadingIndex)))
                   .ToList();
        }

        // Limit how many concurrent operations we perform to prevent overwhelming the system
        private static readonly SemaphoreSlim _processSemaphore = new SemaphoreSlim(100, 100);

        private enum ProcessWordStatus
        {
            Resolved,
            FilteredOut,
            TimedOut,
            Unresolved
        }

        private readonly struct ProcessWordResult
        {
            private ProcessWordResult(ProcessWordStatus status, DeckWord? word = null,
                                      DeckWordCacheKey? cacheKey = null, DeckWord? cacheWord = null,
                                      int? margin = null)
            {
                Status = status;
                Word = word;
                CacheKey = cacheKey;
                CacheWord = cacheWord;
                Margin = margin;
            }

            public ProcessWordStatus Status { get; }
            public DeckWord? Word { get; }
            public DeckWordCacheKey? CacheKey { get; }
            public DeckWord? CacheWord { get; }
            public int? Margin { get; }

            public static ProcessWordResult FromResolved(DeckWord word, DeckWordCacheKey? cacheKey = null,
                                                         DeckWord? cacheWord = null, int? margin = null)
                => new(ProcessWordStatus.Resolved, word, cacheKey, cacheWord, margin);

            public static ProcessWordResult FilteredOut { get; } = new(ProcessWordStatus.FilteredOut);
            public static ProcessWordResult TimedOut { get; } = new(ProcessWordStatus.TimedOut);
            public static ProcessWordResult Unresolved { get; } = new(ProcessWordStatus.Unresolved);
        }

        private static async Task<ProcessWordResult> ProcessWord((WordInfo wordInfo, int occurrences) wordData, Deconjugator deconjugator,
                                                                 Dictionary<DeckWordCacheKey, DeckWord?>? prefetchedCache = null,
                                                                 ParserDiagnostics? diagnostics = null)
        {
            // Try to acquire semaphore with timeout to prevent deadlock
            if (!await _processSemaphore.WaitAsync(TimeSpan.FromSeconds(5)))
            {
                // If we can't get the semaphore in a reasonable time, surface an explicit timeout status.
                // This is better than hanging indefinitely
                diagnostics?.RunSummary.IncrementProcessSemaphoreTimeoutCount();
                diagnostics?.RunSummary.IncrementUnresolvedTokenCount();
                return ProcessWordResult.TimedOut;
            }

            try
            {
                // Early check for digits before anything else (including cache lookup)
                var textWithoutBar = wordData.wordInfo.Text.TrimEnd('ー');
                if (textWithoutBar.Length > 0 && textWithoutBar.All(char.IsDigit) ||
                    (textWithoutBar.Length == 1 && textWithoutBar.IsAsciiOrFullWidthLetter()))
                {
                    return ProcessWordResult.FilteredOut;
                }

                var isNameLikeSudachiNoun = PosMapper.IsNameLikeSudachiNoun(
                                                                            wordData.wordInfo.PartOfSpeech,
                                                                            wordData.wordInfo.PartOfSpeechSection1,
                                                                            wordData.wordInfo.PartOfSpeechSection2,
                                                                            wordData.wordInfo.PartOfSpeechSection3);

                var cacheKey = new DeckWordCacheKey(
                                                    wordData.wordInfo.Text,
                                                    wordData.wordInfo.PartOfSpeech,
                                                    wordData.wordInfo.DictionaryForm,
                                                    wordData.wordInfo.Reading,
                                                    wordData.wordInfo.IsPersonNameContext,
                                                    isNameLikeSudachiNoun
                                                   );

                if (UseCache && diagnostics == null)
                {
                    try
                    {
                        DeckWord? cachedWord = null;
                        if (prefetchedCache != null)
                            prefetchedCache.TryGetValue(cacheKey, out cachedWord);
                        else
                            cachedWord = await DeckWordCache.GetAsync(cacheKey);

                        if (cachedWord != null && cachedWord.WordId != -1)
                        {
                            return ProcessWordResult.FromResolved(
                                                                  new DeckWord
                                                                  {
                                                                      WordId = cachedWord.WordId, OriginalText = wordData.wordInfo.Text,
                                                                      ReadingIndex = cachedWord.ReadingIndex,
                                                                      Occurrences = wordData.occurrences,
                                                                      Conjugations = cachedWord.Conjugations,
                                                                      PartsOfSpeech = cachedWord.PartsOfSpeech, Origin = cachedWord.Origin,
                                                                      SudachiReading = wordData.wordInfo.Reading,
                                                                      SudachiPartOfSpeech = wordData.wordInfo.PartOfSpeech
                                                                  }, margin: cachedWord.CachedMargin);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Warning] Failed to read from DeckWordCache: {ex.Message}");
                    }
                }

                DeckWord? processedWord = null;
                int? resolvedMargin = null;
                bool isProcessed = false;
                int attemptCount = 0;
                const int maxAttempts = 3; // Limit how many attempts we make to prevent infinite loops

                var baseWord = wordData.wordInfo.Text;
                do
                {
                    attemptCount++;
                    try
                    {
                        // If the word has a definitively pre-matched wordId (e.g. from CombineCompounds) and no
                        // competing candidate list, use it directly. When PreMatchedCandidateWordIds is also
                        // set (resegmentation case), fall through to the full scorer instead.
                        if (wordData.wordInfo.PreMatchedWordId.HasValue
                            && wordData.wordInfo.PreMatchedCandidateWordIds == null)
                        {
                            var preMatchedWordId = wordData.wordInfo.PreMatchedWordId.Value;
                            var wordCache = await JmDictCache.GetWordsAsync([preMatchedWordId]);
                            if (wordCache.TryGetValue(preMatchedWordId, out var preMatchedWord))
                            {
                                var textForReadingLookup = !string.IsNullOrEmpty(wordData.wordInfo.DictionaryForm)
                                    ? wordData.wordInfo.DictionaryForm
                                    : wordData.wordInfo.Text;
                                var readingIndex = GetBestReadingIndex(preMatchedWord, textForReadingLookup, wordData.wordInfo.Reading);
                                processedWord = new DeckWord
                                                {
                                                    WordId = preMatchedWordId, ReadingIndex = readingIndex,
                                                    OriginalText = wordData.wordInfo.Text, Occurrences = wordData.occurrences,
                                                    Conjugations = wordData.wordInfo.PreMatchedConjugations ?? [],
                                                    PartsOfSpeech = preMatchedWord.CachedPOS, Origin = preMatchedWord.Origin
                                                };
                                resolvedMargin = ScoringPolicy.HighConfidenceThreshold;
                                break;
                            }
                        }

                        if (wordData.wordInfo.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective or PartOfSpeech.Auxiliary
                                or PartOfSpeech.NaAdjective or PartOfSpeech.Expression ||
                            wordData.wordInfo.PartOfSpeechSection1 is PartOfSpeechSection.Adjectival)
                        {
                            // Try to deconjugate as verb or adjective
                            var verbResult = await DeconjugateVerbOrAdjective(wordData, deconjugator, diagnostics);
                            if (!verbResult.success || verbResult.word == null)
                            {
                                // The word might be a noun misparsed as a verb/adjective like お祭り
                                var nounResult = await DeconjugateWord(wordData, diagnostics);
                                processedWord = nounResult.word;
                                resolvedMargin = nounResult.margin;
                            }
                            else
                            {
                                processedWord = verbResult.word;
                                resolvedMargin = verbResult.margin;
                            }
                        }
                        else
                        {
                            var nounResult = await DeconjugateWord(wordData, diagnostics);
                            if (!nounResult.success || nounResult.word == null)
                            {
                                // The word might be a conjugated noun + suru
                                var verbResult = await DeconjugateVerbOrAdjective(wordData, deconjugator, diagnostics);

                                var oldPos = wordData.wordInfo.PartOfSpeech;
                                // The word might be a verb or an adjective misparsed as a noun like らしく
                                if (!verbResult.success || verbResult.word == null)
                                {
                                    wordData.wordInfo.PartOfSpeech = PartOfSpeech.Verb;
                                    verbResult = await DeconjugateVerbOrAdjective(wordData, deconjugator, diagnostics);
                                }

                                if (!verbResult.success || verbResult.word == null)
                                {
                                    wordData.wordInfo.PartOfSpeech = PartOfSpeech.IAdjective;
                                    verbResult = await DeconjugateVerbOrAdjective(wordData, deconjugator, diagnostics);
                                }

                                if (!verbResult.success || verbResult.word == null)
                                {
                                    wordData.wordInfo.PartOfSpeech = PartOfSpeech.NaAdjective;
                                    // Prefer direct lookup for na-adjective stems that Sudachi may label as nouns/names (e.g., 朧気).
                                    var naDirect = await DeconjugateWord(wordData, diagnostics);
                                    if (naDirect is { success: true, word: not null })
                                        verbResult = (true, naDirect.word, naDirect.margin);
                                    else
                                        verbResult = await DeconjugateVerbOrAdjective(wordData, deconjugator, diagnostics);
                                }

                                if (!verbResult.success || verbResult.word == null)
                                {
                                    // Interjections are frequently misclassified by Sudachi as proper-name nouns (e.g., おお…).
                                    // Try direct lookup as an interjection before giving up.
                                    wordData.wordInfo.PartOfSpeech = PartOfSpeech.Interjection;
                                    var interjectionDirect = await DeconjugateWord(wordData, diagnostics);
                                    if (interjectionDirect is { success: true, word: not null })
                                        verbResult = (true, interjectionDirect.word, interjectionDirect.margin);
                                }

                                wordData.wordInfo.PartOfSpeech = oldPos;
                                processedWord = verbResult.word;
                                resolvedMargin = verbResult.margin;
                            }
                            else if (wordData.wordInfo.PartOfSpeech is PartOfSpeech.Pronoun or PartOfSpeech.Conjunction
                                     or PartOfSpeech.Interjection or PartOfSpeech.Particle or PartOfSpeech.Adverb
                                     or PartOfSpeech.NaAdjective or PartOfSpeech.Suffix or PartOfSpeech.NounSuffix
                                     or PartOfSpeech.PrenounAdjectival)
                            {
                                processedWord = nounResult.word;
                                resolvedMargin = nounResult.margin;
                            }
                            else
                            {
                                // Also try verb deconjugation: noun-tagged tokens may be verb stems
                                // (e.g., 抱え is both a rare noun "armful" and 連用形 of the common verb 抱える).
                                // Prefer the verb if it has strictly higher priority.
                                var savedPos = wordData.wordInfo.PartOfSpeech;
                                wordData.wordInfo.PartOfSpeech = PartOfSpeech.Verb;
                                var verbFallback = await DeconjugateVerbOrAdjective(wordData, deconjugator, diagnostics);
                                wordData.wordInfo.PartOfSpeech = savedPos;

                                // Ichidan 連用形 stem fallback: when the deconjugator doesn't produce surface+"る"
                                // (because bare stems have no conjugation ending to match), try looking it up directly.
                                // E.g., 抱え → 抱える: _lookups["かかえ"] only has the noun, but _lookups["かかえる"] has the verb.
                                if (nounResult.word != null &&
                                    (!verbFallback.success || verbFallback.word == null ||
                                     verbFallback.word.WordId == nounResult.word.WordId))
                                {
                                    var surfaceHira = KanaConverter.ToHiragana(wordData.wordInfo.Text,
                                                                               convertLongVowelMark: false);
                                    var ruText = surfaceHira + "る";
                                    if (_lookups.TryGetValue(ruText, out List<int>? ruIds))
                                    {
                                        var ruWordCache = await JmDictCache.GetWordsAsync(ruIds.Distinct().ToList());
                                        bool isKanaForStem = WanaKana.IsKana(wordData.wordInfo.Text);
                                        JmDictWord? bestV1 = null;
                                        foreach (var (_, ruWord) in ruWordCache)
                                        {
                                            if (ruWord.PartsOfSpeech.Any(p => p is "v1" or "v1-s") &&
                                                (bestV1 == null ||
                                                 ruWord.GetPriorityScore(isKanaForStem) > bestV1.GetPriorityScore(isKanaForStem)))
                                                bestV1 = ruWord;
                                        }

                                        if (bestV1 != null)
                                        {
                                            // GetBestReadingIndex can't find the stem form (e.g., "抱え" isn't in 抱える's forms).
                                            // Instead find the kana form whose stem (text minus る) matches the surface.
                                            var stemHira = KanaConverter.ToHiragana(wordData.wordInfo.Text, convertLongVowelMark: false);
                                            var stemReadingIndex = bestV1.Forms
                                                                         .Where(f => f.FormType == JmDictFormType.KanaForm &&
                                                                                     KanaConverter.ToHiragana(f.Text,
                                                                                         convertLongVowelMark: false) ==
                                                                                     stemHira + "る")
                                                                         .Select(f => (byte)f.ReadingIndex)
                                                                         .DefaultIfEmpty((byte)0)
                                                                         .First();
                                            verbFallback = (
                                                true,
                                                new DeckWord
                                                {
                                                    WordId = bestV1.WordId, OriginalText = wordData.wordInfo.Text,
                                                    ReadingIndex = stemReadingIndex, Occurrences = wordData.occurrences,
                                                    Conjugations = ["continuative"], PartsOfSpeech = bestV1.CachedPOS,
                                                    Origin = bestV1.Origin
                                                }, (int?)null);
                                        }
                                    }
                                }

                                bool nounIsPureNameEntry = nounResult.word != null &&
                                                           nounResult.word.PartsOfSpeech.All(p => p is PartOfSpeech.Name
                                                                                                 or PartOfSpeech.Unknown);
                                if (wordData.wordInfo.IsPersonNameContext && nounIsPureNameEntry)
                                {
                                    processedWord = nounResult.word;
                                    resolvedMargin = nounResult.margin;
                                }
                                else if (verbFallback is { success: true, word: not null } && nounResult.word != null)
                                {
                                    var bothCache = await JmDictCache.GetWordsAsync(
                                                                                    [nounResult.word.WordId, verbFallback.word.WordId]);
                                    if (bothCache.TryGetValue(nounResult.word.WordId, out var nounEntry) &&
                                        bothCache.TryGetValue(verbFallback.word.WordId, out var verbEntry))
                                    {
                                        bool isKana = WanaKana.IsKana(wordData.wordInfo.Text);

                                        // Use the same word-level scoring as the main scorer (WordPriorityScorer +
                                        // EntryPriorityScorer) so archaic penalties, copula boosts, etc. are consistent.
                                        static int NounVerbScore(JmDictWord word, byte readingIndex)
                                        {
                                            var form = word.Forms.FirstOrDefault(f => (byte)f.ReadingIndex == readingIndex)
                                                       ?? word.Forms.FirstOrDefault();
                                            if (form == null) return 0;
                                            var candidate = new FormCandidate(word, form, readingIndex, form.Text, null);
                                            return WordPriorityScorer.Score(candidate, isNameContext: false, isArchaicSentence: false,
                                                                            ArchaicPosTypes)
                                                   + EntryPriorityScorer.Score(candidate);
                                        }

                                        var reading = wordData.wordInfo.Reading;
                                        bool nounReadingMatch = !string.IsNullOrEmpty(reading) &&
                                                                FormCandidateFactory.HasKanaReadingMatch(nounEntry, reading);
                                        bool verbReadingMatch = !string.IsNullOrEmpty(reading) &&
                                                                FormCandidateFactory.HasKanaReadingMatch(verbEntry, reading,
                                                                    allowStemMatch: true);
                                        bool verbExactReadingMatch = !string.IsNullOrEmpty(reading) &&
                                                                     FormCandidateFactory.HasKanaReadingMatch(verbEntry, reading);

                                        if (nounReadingMatch && !verbReadingMatch)
                                        {
                                            processedWord = nounResult.word;
                                            resolvedMargin = nounResult.margin;
                                        }
                                        else if (!nounReadingMatch && verbReadingMatch)
                                        {
                                            processedWord = verbFallback.word;
                                            resolvedMargin = verbFallback.margin;
                                        }
                                        else if (nounReadingMatch && verbReadingMatch && !verbExactReadingMatch)
                                        {
                                            // Verb only matches as a stem (e.g. できる's stem でき matches 出来).
                                            // Require a clear priority margin before overriding Sudachi's noun tag.
                                            // When form scoring already resolved the noun with high confidence (e.g. うえ → 上),
                                            // demand a much larger verb advantage to prevent spurious overrides (e.g. 飢える).
                                            int stemThreshold = nounResult.margin >= ScoringPolicy.HighConfidenceThreshold ? 30 : 15;
                                            int nounEvidence = NounVerbScore(nounEntry, nounResult.word.ReadingIndex);
                                            int verbEvidence = NounVerbScore(verbEntry, verbFallback.word.ReadingIndex);
                                            // Sudachi explicitly locked the surface as its own dict entry (e.g. 備え noun, not 備える stem).
                                            // Respect that tag when the noun itself has solid frequency evidence (nf rank <= 20),
                                            // so a higher-priority verb can't override a well-attested noun reading.
                                            // Skip the bias for rare-noun homographs like 抱え (nf23) where the verb 抱える is intended.
                                            if (wordData.wordInfo.DictionaryForm == wordData.wordInfo.Text)
                                            {
                                                var nounNf = (nounEntry.Priorities ?? [])
                                                    .FirstOrDefault(p => p.StartsWith("nf"));
                                                if (nounNf is { Length: > 2 }
                                                    && int.TryParse(nounNf[2..], out var nfRank)
                                                    && nfRank <= 20)
                                                    nounEvidence += 20;
                                            }
                                            if (verbEvidence - nounEvidence > stemThreshold)
                                            {
                                                processedWord = verbFallback.word;
                                                resolvedMargin = verbFallback.margin;
                                            }
                                            else
                                            {
                                                processedWord = nounResult.word;
                                                resolvedMargin = nounResult.margin;
                                            }
                                        }
                                        else if (NounVerbScore(verbEntry, verbFallback.word.ReadingIndex) >
                                                 NounVerbScore(nounEntry, nounResult.word.ReadingIndex))
                                        {
                                            processedWord = verbFallback.word;
                                            resolvedMargin = verbFallback.margin;
                                        }
                                        else
                                        {
                                            processedWord = nounResult.word;
                                            resolvedMargin = nounResult.margin;
                                        }
                                    }
                                    else
                                    {
                                        processedWord = nounResult.word;
                                        resolvedMargin = nounResult.margin;
                                    }
                                }
                                else
                                {
                                    processedWord = nounResult.word;
                                    resolvedMargin = nounResult.margin;
                                }
                            }
                        }

                        if (processedWord != null)
                        {
                            // Restore original text (before any stripping/modifications)
                            processedWord.OriginalText = baseWord;
                            break;
                        }

                        // We haven't found a match, let's try to remove the last character if it's a っ, a ー or a duplicate
                        if (wordData.wordInfo.Text.Length > 2 &&
                            (wordData.wordInfo.Text[^1] == 'っ' || wordData.wordInfo.Text[^1] == 'ー' ||
                             wordData.wordInfo.Text[^2] == wordData.wordInfo.Text[^1]))
                        {
                            wordData.wordInfo.Text = wordData.wordInfo.Text[..^1];
                        }
                        // Let's try to remove any honorifics in front of the word
                        else if (wordData.wordInfo.Text.StartsWith("お") || wordData.wordInfo.Text.StartsWith("御"))
                        {
                            wordData.wordInfo.Text = wordData.wordInfo.Text[1..];
                        }
                        // Let's try without any long vowel mark
                        else if (wordData.wordInfo.Text.Contains('ー'))
                        {
                            wordData.wordInfo.Text = wordData.wordInfo.Text.Replace("ー", "");
                        }
                        // Let's try without small っ
                        else if (wordData.wordInfo.Text.Contains('っ') || wordData.wordInfo.Text.Contains('ッ'))
                        {
                            wordData.wordInfo.Text = baseWord.Replace("っ", "").Replace("ッ", "");
                        }
                        // Let's try stripping any small kana
                        else if (wordData.wordInfo.Text.Contains('ゃ') || wordData.wordInfo.Text.Contains('ゅ') ||
                                 wordData.wordInfo.Text.Contains('ょ') || wordData.wordInfo.Text.Contains('ぁ') ||
                                 wordData.wordInfo.Text.Contains('ぃ') || wordData.wordInfo.Text.Contains('ぅ') ||
                                 wordData.wordInfo.Text.Contains('ぇ') || wordData.wordInfo.Text.Contains('ぉ'))
                        {
                            wordData.wordInfo.Text = baseWord.Replace("ゃ", "").Replace("ゅ", "")
                                                             .Replace("ょ", "").Replace("ぁ", "")
                                                             .Replace("ぃ", "").Replace("ぅ", "")
                                                             .Replace("ぇ", "").Replace("ぉ", "")
                                                             .Replace("っ", "").Replace("ッ", "");
                        }
                        else
                        {
                            isProcessed = true;
                        }

                        // Also stop if we've made too many attempts
                        if (attemptCount >= maxAttempts)
                        {
                            isProcessed = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log and consider this word processed to avoid infinite loop
                        Console.WriteLine($"[Error] Failed to process word '{wordData.wordInfo.Text}': {ex.Message}");
                        isProcessed = true;
                    }
                } while (!isProcessed);

                // Always restore original text to avoid mutating shared WordInfo
                // (used by example sentence extraction and diagnostics)
                wordData.wordInfo.Text = baseWord;

                if (processedWord == null)
                {
                    diagnostics?.RunSummary.IncrementUnresolvedTokenCount();
                    return ProcessWordResult.Unresolved;
                }

                processedWord.Occurrences = wordData.occurrences;
                processedWord.SudachiReading = wordData.wordInfo.Reading;
                processedWord.SudachiPartOfSpeech = wordData.wordInfo.PartOfSpeech;

                if (!UseCache)
                    return ProcessWordResult.FromResolved(processedWord, margin: resolvedMargin);

                var cacheWord = new DeckWord
                                {
                                    WordId = processedWord.WordId, OriginalText = processedWord.OriginalText,
                                    ReadingIndex = processedWord.ReadingIndex, Conjugations = processedWord.Conjugations,
                                    PartsOfSpeech = processedWord.PartsOfSpeech, Origin = processedWord.Origin,
                                    CachedMargin = resolvedMargin
                                };

                return ProcessWordResult.FromResolved(processedWord, cacheKey, cacheWord, resolvedMargin);
            }
            finally
            {
                _processSemaphore.Release();
            }
        }

        private static async Task<(bool success, DeckWord? word, int? margin)> DeconjugateWord(
            (WordInfo wordInfo, int occurrences) wordData,
            ParserDiagnostics? diagnostics = null)
        {
            string text = wordData.wordInfo.Text;

            // Exclude text that is primarily digits (with optional trailing ー) or single latin character
            var textWithoutBar = text.TrimEnd('ー');
            if ((textWithoutBar.Length > 0 && textWithoutBar.All(char.IsDigit)) || (text.Length == 1 && text.IsAsciiOrFullWidthLetter()))
            {
                return (false, null, null);
            }

            var textInHiragana = KanaConverter.ToHiragana(wordData.wordInfo.Text, convertLongVowelMark: false);
            List<int>? candidates;
            bool isStripped = false;
            string textStripped = "";

            if (wordData.wordInfo.PreMatchedCandidateWordIds is { Count: > 0 } constrainedIds)
            {
                candidates = new List<int>(constrainedIds);
            }
            else
            {
                var collected = LookupCandidateCollector.CollectIds(_lookups, text,
                                                                    includeKanaNormalized: true, includeLongVowelStripped: true);

                // Also look up Sudachi's NormalizedForm when it differs from the surface (e.g., チックショー → チクショー for 畜生)
                if (!string.IsNullOrEmpty(wordData.wordInfo.NormalizedForm) &&
                    wordData.wordInfo.NormalizedForm != text)
                {
                    var normalizedCollected = LookupCandidateCollector.CollectIds(_lookups, wordData.wordInfo.NormalizedForm,
                                                                                  includeKanaNormalized: true,
                                                                                  includeLongVowelStripped: true);
                    if (normalizedCollected.Count > 0)
                        collected = collected.Concat(normalizedCollected).Distinct().ToList();
                }

                candidates = collected.Count > 0 ? collected : null;

                if (text.Contains('ー'))
                {
                    isStripped = true;
                    textStripped = text.Replace("ー", "");
                }
            }

            if (candidates is { Count: not 0 })
            {
                candidates = candidates.OrderBy(c => c).ToList();

                Dictionary<int, JmDictWord> wordCache;
                try
                {
                    wordCache = await JmDictCache.GetWordsAsync(candidates);
                }
                catch (Exception ex)
                {
                    // If we hit an exception when retrieving from cache, return a failure
                    // but don't crash the entire process
                    Console.WriteLine($"Error retrieving word cache: {ex.Message}");
                    return (false, null, null);
                }

                // Early return if we got no words from cache to avoid NullReferenceException
                if (wordCache.Count == 0)
                {
                    return (false, null, null);
                }

                bool isNameLikeSudachiNoun = PosMapper.IsNameLikeSudachiNoun(
                                                                             wordData.wordInfo.PartOfSpeech,
                                                                             wordData.wordInfo.PartOfSpeechSection1,
                                                                             wordData.wordInfo.PartOfSpeechSection2,
                                                                             wordData.wordInfo.PartOfSpeechSection3);

                bool hasAnyNonNameCandidate = false;
                var compatibleNonNameMatches = new List<JmDictWord>();
                var nameCandidates = new List<JmDictWord>();

                foreach (var id in candidates)
                {
                    if (!wordCache.TryGetValue(id, out var word)) continue;

                    var posList = word.CachedPOS;
                    bool hasNonNamePos = posList.Any(p => p is not (PartOfSpeech.Name or PartOfSpeech.Unknown));
                    if (hasNonNamePos)
                        hasAnyNonNameCandidate = true;

                    // Treat pure-name entries (JMnedict name entries) separately from normal words.
                    // Some words may include both Name + non-name tags; those should be treated as non-name to avoid regressions.
                    bool isPureNameEntry = !hasNonNamePos && posList.Contains(PartOfSpeech.Name);

                    // Is stripped part to handle interjection like よー and こーら
                    bool compatible = PosMapper.IsJmDictCompatibleWithSudachi(
                                                                              word.PartsOfSpeech,
                                                                              wordData.wordInfo.PartOfSpeech,
                                                                              allowInterjectionFallback: isStripped);

                    if (compatible && hasNonNamePos)
                    {
                        compatibleNonNameMatches.Add(word);
                        continue;
                    }

                    // Names should be strongly deprioritized unless we have strong evidence.
                    // We allow them to be considered when:
                    // - the token is in a person-name honorific context (Xさん/Xくん/etc), OR
                    // - Sudachi itself classified the token as a proper/name-like noun.
                    if (isPureNameEntry && (wordData.wordInfo.IsPersonNameContext || isNameLikeSudachiNoun))
                    {
                        nameCandidates.Add(word);
                    }
                }

                // Selection via pair scoring:
                // Build (word, form) candidates from the appropriate pool, then pick the best pair.
                List<JmDictWord> candidatePool;
                bool isNameContext;

                if (wordData.wordInfo.IsPersonNameContext && nameCandidates.Count > 0)
                {
                    candidatePool = nameCandidates;
                    isNameContext = true;
                }
                else if (isNameLikeSudachiNoun && nameCandidates.Count > 0)
                {
                    candidatePool = new List<JmDictWord>(compatibleNonNameMatches);
                    candidatePool.AddRange(nameCandidates);
                    isNameContext = true;
                }
                else if (compatibleNonNameMatches.Count > 0)
                {
                    candidatePool = compatibleNonNameMatches;
                    isNameContext = false;
                }
                else if (!hasAnyNonNameCandidate && nameCandidates.Count > 0)
                {
                    candidatePool = nameCandidates;
                    isNameContext = true;
                }
                else if (hasAnyNonNameCandidate)
                {
                    // POS-relaxed fallback: strict POS matching filtered out all candidates.
                    // Allow any non-Name entry through so the scoring system gets a chance.
                    foreach (var id in candidates)
                    {
                        if (!wordCache.TryGetValue(id, out var word)) continue;
                        var posList = word.CachedPOS;
                        if (posList.Any(p => p is not (PartOfSpeech.Name or PartOfSpeech.Unknown)))
                            compatibleNonNameMatches.Add(word);
                    }

                    candidatePool = compatibleNonNameMatches;
                    isNameContext = false;
                }
                else
                {
                    var lastResortCandidates = new List<JmDictWord>();
                    foreach (var id in candidates)
                    {
                        if (wordCache.TryGetValue(id, out var word))
                            lastResortCandidates.Add(word);
                    }

                    if (lastResortCandidates.Count > 0)
                    {
                        candidatePool = lastResortCandidates;
                        isNameContext = true;
                    }
                    else
                    {
                        return (false, null, null);
                    }
                }

                var allFormCandidates = new List<FormCandidate>();
                foreach (JmDictWord word in candidatePool)
                {
                    var forms = FormCandidateFactory.EnumerateCandidateForms(word, textInHiragana, allowLooseLvmMatch: true, surface: text);
                    allFormCandidates.AddRange(forms);

                    // Also try with stripped text if applicable
                    if (!isStripped)
                        continue;

                    var strippedHira = KanaConverter.ToHiragana(textStripped, convertLongVowelMark: false);
                    if (strippedHira == textInHiragana)
                        continue;

                    var strippedForms =
                        FormCandidateFactory.EnumerateCandidateForms(word, strippedHira, allowLooseLvmMatch: true, surface: textStripped);
                    allFormCandidates.AddRange(strippedForms);
                }

                var (bestPair, margin) = PickBestFormCandidate(allFormCandidates, text,
                                                               wordData.wordInfo.DictionaryForm, wordData.wordInfo.NormalizedForm,
                                                               isNameContext,
                                                               diagnostics,
                                                               sudachiReading: wordData.wordInfo.Reading);

                // Frequency-rank tiebreaker for resegmented tokens: when the full scorer finds no preference
                // (margin == 0), defer to the frequency-best candidate identified at resegmentation time.
                if (margin == 0
                    && wordData.wordInfo.PreMatchedCandidateWordIds != null
                    && wordData.wordInfo.PreMatchedWordId is { } preferredId)
                {
                    var preferred = allFormCandidates
                                    .Where(c => c.Word.WordId == preferredId)
                                    .MaxBy(c => c.TotalScore);
                    if (preferred != null && preferred.TotalScore >= bestPair!.TotalScore)
                        bestPair = preferred;
                }

                if (bestPair == null)
                    return (false, null, null);

                DeckWord deckWord = new()
                                    {
                                        WordId = bestPair.Word.WordId, OriginalText = wordData.wordInfo.Text,
                                        ReadingIndex = bestPair.ReadingIndex, Occurrences = wordData.occurrences,
                                        PartsOfSpeech = bestPair.Word.CachedPOS, Origin = bestPair.Word.Origin
                                    };
                return (true, deckWord, margin);
            }

            return (false, null, null);
        }

        private static async Task<(bool success, DeckWord? word, int? margin)> DeconjugateVerbOrAdjective(
            (WordInfo wordInfo, int occurrences) wordData, Deconjugator deconjugator,
            ParserDiagnostics? diagnostics = null)
        {
            // Early check for digits before WanaKana (which can't convert full-width digits)
            var textWithoutBar = wordData.wordInfo.Text.TrimEnd('ー');
            if (textWithoutBar.Length > 0 && textWithoutBar.All(char.IsDigit) ||
                (textWithoutBar.Length == 1 && textWithoutBar.IsAsciiOrFullWidthLetter()))
            {
                return (false, null, null);
            }

            var normalizedText = KanaNormalizer.Normalize(KanaConverter.ToHiragana(wordData.wordInfo.Text));

            // Exclude single latin character
            if (normalizedText.Length == 1 && normalizedText.IsAsciiOrFullWidthLetter())
            {
                return (false, null, null);
            }

            var deconjugated = deconjugator.Deconjugate(normalizedText)
                                           .OrderByDescending(d => d.Text.Length).ToList();

            List<(DeconjugationForm form, List<int> ids)> candidates = new();
            foreach (var form in deconjugated)
            {
                if (_lookups.TryGetValue(form.Text, out List<int>? lookup))
                {
                    candidates.Add((form, lookup));
                }
            }

            // Track if we need to try DictionaryForm lookup later (for compound expressions)
            bool tryDictionaryFormFallback = candidates.Count == 0 && !string.IsNullOrEmpty(wordData.wordInfo.DictionaryForm);

            // if there's a candidate that's the same as the base word, put it first in the list
            var baseDictionaryWord = KanaConverter.ToHiragana(wordData.wordInfo.DictionaryForm.Replace("ゎ", "わ").Replace("ヮ", "わ"),
                                                              convertLongVowelMark: false);
            var baseDictionaryWordIndex = candidates.FindIndex(c => c.form.Text == baseDictionaryWord);
            if (baseDictionaryWordIndex != -1)
            {
                var baseDictionaryWordCandidate = candidates[baseDictionaryWordIndex];
                candidates.RemoveAt(baseDictionaryWordIndex);
                candidates.Insert(0, baseDictionaryWordCandidate);
            }

            // if there's a candidate that's the same as the base word, put it first in the list
            var baseWord = KanaConverter.ToHiragana(wordData.wordInfo.Text);
            var baseWordIndex = candidates.FindIndex(c => c.form.Text == baseWord);
            if (baseWordIndex != -1 && candidates[0].form.Text != baseDictionaryWord)
            {
                var baseWordCandidate = candidates[baseWordIndex];
                candidates.RemoveAt(baseWordIndex);
                candidates.Insert(0, baseWordCandidate);
            }

            var allCandidateIds = candidates.SelectMany(c => c.ids).Distinct().ToList();

            // If we have DictionaryForm fallback to try, add those IDs to the list
            if (tryDictionaryFormFallback)
            {
                if (_lookups.TryGetValue(baseDictionaryWord, out List<int>? dictFormLookupIds) ||
                    _lookups.TryGetValue(wordData.wordInfo.DictionaryForm, out dictFormLookupIds))
                {
                    if (dictFormLookupIds is { Count: > 0 })
                    {
                        allCandidateIds = allCandidateIds.Concat(dictFormLookupIds).Distinct().ToList();
                    }
                }

                // Also try NormalizedForm (e.g., 多き has DictionaryForm=多し but NormalizedForm=多い)
                if (dictFormLookupIds is not { Count: > 0 } && !string.IsNullOrEmpty(wordData.wordInfo.NormalizedForm))
                {
                    var normalizedHiragana = KanaConverter.ToHiragana(wordData.wordInfo.NormalizedForm,
                                                                      convertLongVowelMark: false);
                    if (_lookups.TryGetValue(normalizedHiragana, out dictFormLookupIds) ||
                        _lookups.TryGetValue(wordData.wordInfo.NormalizedForm, out dictFormLookupIds))
                    {
                        if (dictFormLookupIds is { Count: > 0 })
                        {
                            allCandidateIds = allCandidateIds.Concat(dictFormLookupIds).Distinct().ToList();
                        }
                    }
                }
            }

            // Direct-surface candidates: words in the lookups whose form exactly matches the surface.
            // E.g. 悪しからず is an adverb entry; without this, deconjugation finds 悪しい instead.
            // Note: keep the FULL lookup set for form enumeration — some ids may already be in
            // allCandidateIds via deconjugation but were POS-filtered out in the matches loop.
            var directSurfaceIds = LookupCandidateCollector.CollectIds(_lookups, wordData.wordInfo.Text, includeKanaNormalized: false);
            var newDirectSurfaceIds = directSurfaceIds.Except(allCandidateIds).ToList();
            if (newDirectSurfaceIds.Count > 0)
                allCandidateIds = allCandidateIds.Concat(newDirectSurfaceIds).Distinct().ToList();

            if (!allCandidateIds.Any())
                return (false, null, null);

            Dictionary<int, JmDictWord> wordCache;
            try
            {
                wordCache = await JmDictCache.GetWordsAsync(allCandidateIds);

                // Check if we got any results
                if (wordCache.Count == 0)
                {
                    return (false, null, null);
                }
            }
            catch (Exception ex)
            {
                // If we hit an exception when retrieving from cache, return a failure
                // but don't crash the entire process
                Console.WriteLine($"Error retrieving verb/adjective word cache: {ex.Message}");
                return (false, null, null);
            }

            var matchResults = DeconjugationMatcher.FilterMatches(candidates, wordCache, wordData.wordInfo.PartOfSpeech);
            List<(JmDictWord word, DeconjugationForm form)> matches = matchResults.Select(m => (m.Word, m.Form)).ToList();

            // When Sudachi's DictionaryForm identifies a different base form (e.g., いかん → DictForm いく),
            // remove identity matches for suru-nouns that are only Verb-compatible through vs tags.
            // This prevents nouns like 移管 from winning via surface match over the actual expression (行かん)
            // or deconjugated verb (行く) when the surface happens to be homophonic with the suru-noun.
            HashSet<int>? filteredSuruNounIds = null;
            if (matches.Count > 1 && normalizedText != baseDictionaryWord && !string.IsNullOrEmpty(baseDictionaryWord))
            {
                bool hasDictFormMatch = matches.Any(m => m.form.Text == baseDictionaryWord && m.form.Process.Count > 0);
                if (hasDictFormMatch)
                {
                    filteredSuruNounIds = matches
                                          .Where(m => m.form.Process.Count == 0 &&
                                                      FormCandidateFactory.IsSuruNounWithoutExpression(m.word.PartsOfSpeech))
                                          .Select(m => m.word.WordId)
                                          .ToHashSet();
                    if (filteredSuruNounIds.Count > 0)
                        matches.RemoveAll(m => filteredSuruNounIds.Contains(m.word.WordId));
                    else
                        filteredSuruNounIds = null;
                }
            }

            if (matches.Count == 0)
            {
                // Last resort: try using Sudachi's DictionaryForm directly if available
                // This handles cases like おかけして → 掛ける where the お prefix
                // prevents standard deconjugation rules from matching
                if (!string.IsNullOrEmpty(wordData.wordInfo.DictionaryForm))
                {
                    var dictFormHiragana = KanaConverter.ToHiragana(wordData.wordInfo.DictionaryForm.Replace("ゎ", "わ").Replace("ヮ", "わ"),
                                                                    convertLongVowelMark: false);
                    if (_lookups.TryGetValue(dictFormHiragana, out List<int>? dictLookup) ||
                        _lookups.TryGetValue(wordData.wordInfo.DictionaryForm, out dictLookup))
                    {
                        if (dictLookup is { Count: > 0 })
                        {
                            try
                            {
                                var dictWordCache = await JmDictCache.GetWordsAsync(dictLookup);
                                var recoveredProcess = deconjugated
                                                       .Where(d => d.Process.Count > 0 && d.Text == dictFormHiragana)
                                                       .MinBy(d => d.Text.Length)?.Process
                                                       ?.Where(p => !string.IsNullOrEmpty(p)).ToList() ?? [];
                                foreach (var dictWord in dictWordCache.Values)
                                {
                                    List<PartOfSpeech> pos = dictWord.CachedPOS;
                                    if (pos.Contains(wordData.wordInfo.PartOfSpeech))
                                    {
                                        var form = new DeconjugationForm(dictFormHiragana, wordData.wordInfo.Text,
                                                                         new List<string>(), new HashSet<string>(), recoveredProcess);
                                        matches.Add((dictWord, form));
                                    }
                                }
                            }
                            catch
                            {
                                /* Cache lookup failed */
                            }
                        }
                    }
                }

                // Also try NormalizedForm (e.g., 多き has DictionaryForm=多し but NormalizedForm=多い)
                if (matches.Count == 0 && !string.IsNullOrEmpty(wordData.wordInfo.NormalizedForm) &&
                    wordData.wordInfo.NormalizedForm != wordData.wordInfo.DictionaryForm)
                {
                    var normalizedHiragana = KanaConverter.ToHiragana(wordData.wordInfo.NormalizedForm,
                                                                      convertLongVowelMark: false);
                    if (_lookups.TryGetValue(normalizedHiragana, out List<int>? normalizedLookup) ||
                        _lookups.TryGetValue(wordData.wordInfo.NormalizedForm, out normalizedLookup))
                    {
                        if (normalizedLookup is { Count: > 0 })
                        {
                            try
                            {
                                var normalizedWordCache = await JmDictCache.GetWordsAsync(normalizedLookup);
                                var recoveredProcess = deconjugated
                                                       .Where(d => d.Process.Count > 0 &&
                                                                   (d.Text == normalizedHiragana ||
                                                                    d.Text == baseDictionaryWord))
                                                       .MinBy(d => d.Text.Length)?.Process
                                                       ?.Where(p => !string.IsNullOrEmpty(p)).ToList() ?? [];
                                foreach (var normalizedWord in normalizedWordCache.Values)
                                {
                                    List<PartOfSpeech> pos = normalizedWord.CachedPOS;
                                    if (pos.Contains(wordData.wordInfo.PartOfSpeech))
                                    {
                                        var form = new DeconjugationForm(normalizedHiragana, wordData.wordInfo.Text,
                                                                         new List<string>(), new HashSet<string>(), recoveredProcess);
                                        matches.Add((normalizedWord, form));
                                    }
                                }
                            }
                            catch
                            {
                                /* Cache lookup failed */
                            }
                        }
                    }
                }

                // POS-relaxed fallback: if strict POS matching filtered out everything,
                // retry allowing any non-Name entry (keep deconjugation tag validation).
                if (matches.Count == 0)
                {
                    foreach (var m in DeconjugationMatcher.FilterMatches(candidates, wordCache, wordData.wordInfo.PartOfSpeech,
                                                                         strictPosCheck: false))
                        matches.Add((m.Word, m.Form));
                }

                if (matches.Count == 0)
                {
                    return (false, null, null);
                }
            }

            // Build (word, jmDictForm, deconjForm) triples across all matches and pick the best pair by score.
            // Each match's deconjForm.Text serves as the targetHiragana for phonetic gating.
            var allFormCandidates = new List<FormCandidate>();
            foreach (var match in matches)
            {
                var formCandidates = FormCandidateFactory.EnumerateCandidateForms(match.word, match.form.Text,
                                                                                  allowLooseLvmMatch: true, deconjForm: match.form,
                                                                                  surface: wordData.wordInfo.Text);
                allFormCandidates.AddRange(formCandidates);
            }

            // Add direct-surface candidates so surface-exact entries compete with deconjugated forms.
            // Skip words already in matches (avoids duplicates that produce a false margin=0).
            // Mark POS-incompatible words so the scorer can penalise them when POS-compatible
            // matches already exist (e.g. noun 1197950 "artistry" losing to adj-na 2653620 "serious").
            var matchedWordIds = new HashSet<int>(matches.Select(m => m.word.WordId));
            foreach (var id in directSurfaceIds)
            {
                if (matchedWordIds.Contains(id)) continue;
                if (filteredSuruNounIds != null && filteredSuruNounIds.Contains(id)) continue;
                if (!wordCache.TryGetValue(id, out var directWord)) continue;
                var posList = directWord.CachedPOS;
                if (!posList.Any(p => p is not (PartOfSpeech.Name or PartOfSpeech.Unknown))) continue;
                bool isPosIncompat = matches.Count > 0 &&
                                     !PosMapper.IsJmDictCompatibleWithSudachi(directWord.PartsOfSpeech, wordData.wordInfo.PartOfSpeech);
                var forms = FormCandidateFactory.EnumerateCandidateForms(directWord, normalizedText, allowLooseLvmMatch: true,
                                                                         surface: wordData.wordInfo.Text);
                if (isPosIncompat)
                    foreach (var f in forms)
                        f.IsPosIncompatibleDirectSurface = true;
                allFormCandidates.AddRange(forms);
            }

            var (bestPair, margin) = PickBestFormCandidate(allFormCandidates, wordData.wordInfo.Text,
                                                           wordData.wordInfo.DictionaryForm, wordData.wordInfo.NormalizedForm,
                                                           isNameContext: wordData.wordInfo.IsPersonNameContext,
                                                           diagnostics,
                                                           sudachiReading: wordData.wordInfo.Reading);

            // Imperative disambiguation: godan imperative (行けよ→行く "go!") vs potential imperative
            // (行けよ→行ける "be able to go!"). The base verb is almost always correct in natural Japanese.
            if (bestPair != null
                && wordData.wordInfo.IsImperative
                && !string.IsNullOrEmpty(wordData.wordInfo.NormalizedForm)
                && wordData.wordInfo.NormalizedForm != wordData.wordInfo.DictionaryForm)
            {
                var normalizedHira = KanaConverter.ToHiragana(wordData.wordInfo.NormalizedForm,
                                                              convertLongVowelMark: false);
                var normalizedFormHira = KanaNormalizer.Normalize(normalizedHira);

                var baseVerbCandidate = allFormCandidates
                                        .Where(c => c.Form.Text == wordData.wordInfo.NormalizedForm
                                                    || KanaNormalizer.Normalize(
                                                                                KanaConverter.ToHiragana(c.Form.Text,
                                                                                    convertLongVowelMark: false)) ==
                                                    normalizedFormHira)
                                        .OrderByDescending(c => ScoringPolicy.EffectiveScore(c))
                                        .FirstOrDefault();

                if (baseVerbCandidate != null)
                {
                    bestPair = baseVerbCandidate;
                    var bestAlternate = allFormCandidates
                                        .Where(c => !c.IsPosIncompatibleDirectSurface && c.Word.WordId != bestPair.Word.WordId)
                                        .OrderByDescending(c => ScoringPolicy.EffectiveScore(c))
                                        .FirstOrDefault();
                    margin = bestAlternate != null
                        ? ScoringPolicy.EffectiveScore(bestPair) - ScoringPolicy.EffectiveScore(bestAlternate)
                        : null;
                }
            }

            if (bestPair == null)
                return (false, null, null);

            DeckWord deckWord = new()
                                {
                                    WordId = bestPair.Word.WordId, OriginalText = wordData.wordInfo.Text,
                                    ReadingIndex = bestPair.ReadingIndex, Occurrences = wordData.occurrences, Conjugations =
                                        bestPair.DeconjForm?.Process is ["casual kind request"] &&
                                        bestPair.Word.PartsOfSpeech.Contains("adj-na")
                                            ? []
                                            : bestPair.DeconjForm?.Process.ToList() ?? [],
                                    PartsOfSpeech = bestPair.Word.CachedPOS, Origin = bestPair.Word.Origin
                                };

            return (true, deckWord, margin);
        }

        public static async Task<List<DeckWord>> GetWordsDirectLookup(IDbContextFactory<JitenDbContext> contextFactory, List<string> words)
        {
            await EnsureInitializedAsync(contextFactory);

            List<DeckWord> matchedWords = new();

            // Phase 1: resolve all words to their candidate matches
            var wordCandidates = new List<(string word, List<(JmDictWord match, int readingIndex)> matches)>();
            var allCandidateWordIds = new HashSet<int>();

            foreach (var word in words)
            {
                var wordInHiragana = KanaConverter.ToHiragana(word, convertLongVowelMark: false);
                var wordNormalized = KanaNormalizer.Normalize(wordInHiragana);

                if (!_lookups.TryGetValue(wordNormalized, out var matchesIds) || matchesIds.Count == 0)
                    continue;

                var wordCache = await JmDictCache.GetWordsAsync(matchesIds);

                if (wordCache == null || wordCache.Count == 0)
                    continue;

                List<(JmDictWord match, int readingIndex)> matchesWithReading = new();

                foreach (var id in matchesIds)
                {
                    if (!wordCache.TryGetValue(id, out var match)) continue;
                    var matchedForm = match.Forms.FirstOrDefault(f => f.Text == word);
                    if (matchedForm != null)
                        matchesWithReading.Add((match, matchedForm.ReadingIndex));
                }

                if (matchesWithReading.Count == 0)
                    continue;

                wordCandidates.Add((word, matchesWithReading));
                foreach (var m in matchesWithReading)
                    allCandidateWordIds.Add(m.match.WordId);
            }

            // Phase 2: single batched frequency query
            Dictionary<(int, short), JmDictWordFormFrequency> formFrequencies = new();
            if (allCandidateWordIds.Count > 0)
            {
                await using var context = await _contextFactory.CreateDbContextAsync();
                formFrequencies = await context.WordFormFrequencies
                                               .AsNoTracking()
                                               .Where(wff => allCandidateWordIds.Contains(wff.WordId))
                                               .ToDictionaryAsync(wff => (wff.WordId, wff.ReadingIndex));
            }

            // Phase 3: pick best match per word using the pre-fetched frequencies
            foreach (var (word, matchesWithReading) in wordCandidates)
            {
                var best = matchesWithReading
                           .OrderBy(m =>
                           {
                               var key = (m.match.WordId, (short)m.readingIndex);
                               if (formFrequencies.TryGetValue(key, out var wff))
                                   return wff.FrequencyRank;
                               return int.MaxValue;
                           })
                           .First();

                matchedWords.Add(new DeckWord
                                 {
                                     WordId = best.match.WordId, ReadingIndex = (byte)best.readingIndex, OriginalText = word,
                                     SudachiReading = GetKatakanaReading(best.match, (byte)best.readingIndex)
                                 });
            }

            return ExcludeFinalMisparses(matchedWords);
        }

        private static string GetKatakanaReading(JmDictWord word, byte readingIndex)
        {
            var kanaForm = word.Forms.FirstOrDefault(f => f.FormType == JmDictFormType.KanaForm && f.ReadingIndex == readingIndex);
            return kanaForm != null ? WanaKana.ToKatakana(kanaForm.Text, new DefaultOptions { ConvertLongVowelMark = false }) : "";
        }

        private static byte GetBestReadingIndex(JmDictWord word, string originalText, string? sudachiReading = null)
        {
            if (word.Forms.Count == 0)
                return 0;

            var targetHiragana = KanaConverter.ToHiragana(originalText, convertLongVowelMark: false);
            var candidates =
                FormCandidateFactory.EnumerateCandidateForms(word, targetHiragana, allowLooseLvmMatch: true, surface: originalText);

            if (candidates.Count == 0)
                return 255;

            var (best, _) = PickBestFormCandidate(candidates, originalText,
                                                  dictionaryForm: null, normalizedForm: null, isNameContext: false,
                                                  sudachiReading: sudachiReading);

            return best?.ReadingIndex ?? 255;
        }

        private static async Task CombineCompounds(List<SentenceInfo> sentences)
        {
            foreach (var sentence in sentences)
            {
                if (sentence.Words.Count < 2)
                    continue;

                var wordInfos = sentence.Words.Select(w => w.word).ToList();
                var result = new List<(WordInfo word, int position, int length)>(sentence.Words.Count);

                // Right-to-left so verbs/adjectives at the end of compound expressions
                // get first pick of the full backward window, preventing smaller
                // sub-compounds from blocking larger ones (e.g. ことなきを得た)
                for (int i = wordInfos.Count - 1; i >= 0; i--)
                {
                    var word = wordInfos[i];

                    if (word.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective or PartOfSpeech.Expression or PartOfSpeech.Suffix)
                    {
                        var match = await TryMatchCompounds(wordInfos, i);
                        if (match.HasValue)
                        {
                            var (startIndex, dictForm, wordId) = match.Value;

                            var startPosition = sentence.Words[startIndex].position;
                            int combinedLength = 0;
                            for (int j = startIndex; j <= i; j++)
                            {
                                combinedLength += sentence.Words[j].length;
                            }

                            var originalText = string.Concat(wordInfos.Skip(startIndex).Take(i - startIndex + 1).Select(w => w.Text));
                            var combinedReading = string.Concat(wordInfos.Skip(startIndex).Take(i - startIndex + 1).Select(w => w.Reading));

                            List<string>? conjugations = null;
                            if (originalText != dictForm)
                            {
                                var hiraText = KanaConverter.ToHiragana(originalText);
                                var hiraDictForm = KanaConverter.ToHiragana(dictForm);
                                if (hiraText != hiraDictForm)
                                {
                                    var matchingForm = Deconjugator.Instance.Deconjugate(hiraText)
                                                                   .FirstOrDefault(d => d.Text == hiraDictForm);
                                    if (matchingForm != null)
                                        conjugations = matchingForm.Process.ToList();
                                }
                            }

                            var combinedWordInfo = new WordInfo
                                                   {
                                                       Text = originalText, DictionaryForm = dictForm,
                                                       PartOfSpeech = PartOfSpeech.Expression, NormalizedForm = dictForm,
                                                       Reading = KanaConverter.ToHiragana(combinedReading), PreMatchedWordId = wordId,
                                                       PreMatchedConjugations = conjugations
                                                   };

                            result.Add((combinedWordInfo, startPosition, combinedLength));
                            i = startIndex;
                            continue;
                        }
                    }

                    result.Add(sentence.Words[i]);
                }

                result.Reverse();
                sentence.Words = result;
            }
        }

        /// <summary>
        /// Repairs Sudachi over-segmentation caused by trailing ー on non-standalone tokens.
        /// When Sudachi sees e.g. あなたー, it may split into あ + な + たー.
        /// This method scans for tokens ending with ー (that aren't standalone ー or purely katakana),
        /// tries merging backwards with a bounded window, and validates against JMDict lookups.
        /// If lookup matches WITH ー (e.g. すげー is its own entry), surface keeps ー.
        /// If lookup only matches WITHOUT ー (e.g. あなた for あなたー), surface strips ー.
        /// </summary>
        private static void RepairLongVowelMisparses(List<SentenceInfo> sentences)
        {
            foreach (var sentence in sentences)
            {
                for (int i = sentence.Words.Count - 1; i >= 0; i--)
                {
                    var word = sentence.Words[i].word;

                    // Only target tokens that end with ー but aren't a standalone ー symbol
                    if (!word.Text.EndsWith("ー") || word.Text == "ー")
                        continue;

                    // Skip long katakana tokens (コーヒー, パーティー, etc.) — Sudachi handles these well.
                    // Allow short katakana stems through (ヤバー, スゴー) as they may be slang adj-i.
                    var withoutBar = word.Text.Replace("ー", "");
                    if (withoutBar.Length > 2 && WanaKana.IsKatakana(withoutBar))
                        continue;

                    // Try merging backwards: longest match first (max 5 preceding tokens + current = 6 tokens)
                    int maxBackward = Math.Min(5, i);
                    bool merged = false;

                    for (int k = maxBackward; k >= 1; k--)
                    {
                        // Don't merge across genuine particle tokens (e.g. の, に, と).
                        // Particles are grammatical boundaries, not fragments of a misparsed word.
                        // Only check tokens whose DictionaryForm == Text — reclassified auxiliaries
                        // like な (DictForm=だ) should not block, since they may be word fragments.
                        bool hasParticle = false;
                        for (int j = i - k; j < i; j++)
                        {
                            var w = sentence.Words[j].word;
                            if (w.PartOfSpeech == PartOfSpeech.Particle && w.DictionaryForm == w.Text)
                            {
                                hasParticle = true;
                                break;
                            }
                        }

                        if (hasParticle) continue;

                        var candidateParts = sentence.Words
                                                     .Skip(i - k).Take(k + 1)
                                                     .Select(w => w.word.Text);
                        var candidateSurface = string.Concat(candidateParts);
                        candidateSurface = Regex.Replace(candidateSurface, "ー{2,}", "ー");

                        // Try candidateSurface WITH ー — skip KanaNormalizer for merged tokens
                        // since normalizing ー to a vowel on artificial merges causes false positives
                        if (TryLongVowelLookup(candidateSurface, useKanaNormalizer: false))
                        {
                            MergeLongVowelTokens(sentence, i - k, i, candidateSurface);
                            i = i - k;
                            merged = true;
                            break;
                        }

                        // Try barless key — strip ー from surface since the word doesn't contain it
                        var candidateKey = candidateSurface.TrimEnd('ー');
                        if (candidateKey.Length > 0 && TryLongVowelLookup(candidateKey))
                        {
                            MergeLongVowelTokens(sentence, i - k, i, candidateKey);
                            i = i - k;
                            merged = true;
                            break;
                        }

                        // Try deconjugation of barless key
                        if (candidateKey.Length > 0 && TryDeconjugatedLongVowelLookup(candidateKey))
                        {
                            MergeLongVowelTokens(sentence, i - k, i, candidateKey);
                            i = i - k;
                            merged = true;
                            break;
                        }
                    }

                    // Fallback for るー / すー: compound verb forms (よそってあげる, 出てくれる)
                    // don't exist in the lookups table, so the backward merge loop above fails.
                    // Reattach the kana to the previous token when it's clearly a verb/aux continuation.
                    if (!merged && i > 0 && (word.Text == "るー" || word.Text == "すー"))
                    {
                        var prevEntry = sentence.Words[i - 1];
                        var prevWord = prevEntry.word;
                        var kana = word.Text[0].ToString();
                        var combined = prevWord.Text + kana;

                        bool shouldMerge =
                            TryLongVowelLookup(combined) || TryDeconjugatedLongVowelLookup(combined) ||
                            (word.Text == "るー" && prevWord.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.Auxiliary) ||
                            (word.Text == "すー" && prevWord.Text.EndsWith("で"));

                        if (shouldMerge)
                        {
                            MergeLongVowelTokens(sentence, i - 1, i, combined);
                            i = i - 1;
                            merged = true;
                        }
                    }

                    // No backward merge worked — check the current token itself:
                    // if WITH ー is a valid word (すげー, やべー), leave it;
                    // if only the barless form is valid, strip ー from the surface
                    if (merged)
                        continue;

                    var singleSurface = word.Text;

                    // If the surface has a direct (non-normalized) lookup, leave it alone.
                    // Preserves valid colloquial forms like すげー, やべー.
                    if (TryLongVowelLookup(singleSurface, useKanaNormalizer: false))
                        continue;

                    var singleKey = singleSurface.TrimEnd('ー');

                    // For short stems (ヤバー, スゴー), try adj-i resolution before normalized lookups
                    // to avoid matching name entries like すごう (Sugou) instead of すごい
                    if (singleKey.Length == 2 && TryResolveAdjStem(word, singleKey))
                        continue;

                    if (TryLongVowelLookup(singleSurface))
                        continue;

                    if (singleKey.Length > 0 && (TryLongVowelLookup(singleKey) || TryDeconjugatedLongVowelLookup(singleKey)))
                    {
                        // Don't strip ー when a prior stage (e.g. CombinePrefixes) already set a
                        // valid DictionaryForm for this token (e.g., 古くせー with DictForm 古くさい).
                        if (!string.IsNullOrEmpty(word.DictionaryForm) && word.DictionaryForm != singleSurface
                                                                       && TryLongVowelLookup(word.DictionaryForm))
                            continue;
                        word.Text = singleKey;
                    }
                }
            }
        }

        private static bool HasLookupForCompound(string dictForm)
        {
            if (_lookups == null) return true;
            if (_lookups.TryGetValue(dictForm, out var ids) && ids.Count > 0)
                return true;

            string hira;
            try
            {
                hira = KanaNormalizer.Normalize(KanaConverter.ToHiragana(dictForm));
            }
            catch
            {
                return false;
            }

            return hira != dictForm && _lookups.TryGetValue(hira, out ids) && ids.Count > 0;
        }

        private static bool TryLongVowelLookup(string text, bool useKanaNormalizer = true)
        {
            if (_lookups.TryGetValue(text, out var ids) && ids.Count > 0)
                return true;

            string hira;
            try
            {
                hira = KanaConverter.ToHiragana(text, convertLongVowelMark: false);
            }
            catch
            {
                return false;
            }

            if (hira != text && _lookups.TryGetValue(hira, out ids) && ids.Count > 0)
                return true;

            if (!useKanaNormalizer)
                return false;

            var normalized = KanaNormalizer.Normalize(hira);
            return normalized != hira && _lookups.TryGetValue(normalized, out ids) && ids.Count > 0;
        }

        /// <summary>
        /// Resolves a short stem as an i-adjective by checking if stem+い exists in lookups.
        /// Used for slang elongation patterns: ヤバー→ヤバい (やばい), スゴー→スゴい (すごい).
        /// </summary>
        private static bool TryResolveAdjStem(WordInfo word, string stem)
        {
            string hira;
            try
            {
                hira = KanaNormalizer.Normalize(
                                                KanaConverter.ToHiragana(stem, convertLongVowelMark: false));
            }
            catch
            {
                return false;
            }

            foreach (var form in Deconjugator.Instance.Deconjugate(hira))
            {
                if (form.Tags.Any(t => t == "adj-i") && form.Text == hira + "い" && TryLongVowelLookup(form.Text))
                {
                    word.Text = stem + "い";
                    word.DictionaryForm = stem + "い";
                    word.PartOfSpeech = PartOfSpeech.IAdjective;
                    return true;
                }
            }

            return false;
        }

        private static bool TryDeconjugatedLongVowelLookup(string candidateKey)
        {
            string hira;
            try
            {
                hira = KanaNormalizer.Normalize(
                                                KanaConverter.ToHiragana(candidateKey, convertLongVowelMark: false));
            }
            catch
            {
                return false;
            }

            foreach (var form in Deconjugator.Instance.Deconjugate(hira))
                if (TryLongVowelLookup(form.Text))
                    return true;

            return false;
        }

        private static void MergeLongVowelTokens(SentenceInfo sentence, int startIdx, int endIdx, string surface)
        {
            var firstToken = sentence.Words[startIdx];
            firstToken.word.Text = surface;

            int combinedLength = 0;
            for (int j = startIdx; j <= endIdx; j++)
                combinedLength += sentence.Words[j].length;

            sentence.Words[startIdx] = (firstToken.word, firstToken.position, combinedLength);

            int removeCount = endIdx - startIdx;
            if (removeCount > 0)
                sentence.Words.RemoveRange(startIdx + 1, removeCount);
        }

        /// <summary>
        /// Repairs tokens that were broken by trailing ー.
        /// When Sudachi sees e.g. 久しぶりー, it may split incorrectly.
        /// This method detects standalone ー tokens and tries to combine
        /// preceding tokens, validating against JMDict.
        ///
        /// Priority:
        /// 1. Try WITH ー (わー, ねー, すげー are valid words)
        /// 2. Try WITHOUT ー (久しぶりー → 久しぶり)
        /// 3. Fallback: attach ー to preceding token (preserve stylistic usage)
        /// </summary>
        private static void RepairLongVowelTokens(List<SentenceInfo> sentences)
        {
            foreach (var sentence in sentences)
            {
                for (int i = sentence.Words.Count - 1; i >= 0; i--)
                {
                    var word = sentence.Words[i].word;

                    // Find standalone ー tokens
                    if (word.Text != "ー" || word.PartOfSpeech != PartOfSpeech.SupplementarySymbol)
                        continue;

                    if (i == 0)
                    {
                        // ー at start with nothing to combine - just remove it
                        sentence.Words.RemoveAt(i);
                        continue;
                    }

                    bool found = false;

                    // Priority 0: detached verb ending + ー (る/す classified as Noun after a Verb/Aux)
                    // Sudachi splits e.g. あげる into あげ(Verb)+る(Noun) when followed by ー.
                    // Reattach る/す to the preceding verb and discard ー.
                    if (i >= 2)
                    {
                        var detached = sentence.Words[i - 1].word;
                        var verbBefore = sentence.Words[i - 2].word;
                        if (detached.Text is "る" or "す"
                            && detached.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.Verb or PartOfSpeech.Auxiliary
                            && verbBefore.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.Auxiliary)
                        {
                            var reattached = verbBefore.Text + detached.Text;
                            MergeLongVowelTokens(sentence, i - 2, i - 1, reattached);
                            sentence.Words.RemoveAt(i - 1);
                            i = i - 2;
                            continue;
                        }
                    }

                    // Try combining preceding tokens (longest match first)
                    for (int combineCount = Math.Min(4, i); combineCount >= 1; combineCount--)
                    {
                        var combinedTokens = sentence.Words
                                                     .Skip(i - combineCount).Take(combineCount)
                                                     .Select(w => w.word.Text);
                        var combined = string.Concat(combinedTokens);
                        // PRIORITY 1: Try WITH ー (preserves わー, ねー, すげー, etc.)
                        var withBar = combined + "ー";
                        string? withBarHiragana;
                        try
                        {
                            withBarHiragana = KanaConverter.ToHiragana(withBar);
                        }
                        catch
                        {
                            withBarHiragana = null;
                        }


                        if (withBarHiragana != null && _lookups.TryGetValue(withBarHiragana, out var idsWithBar) && idsWithBar.Count > 0)
                        {
                            // Found valid word WITH ー - merge and keep ー
                            var firstToken = sentence.Words[i - combineCount];
                            firstToken.word.Text = withBar;
                            // Calculate combined length from all merged tokens including ー
                            int combinedLength = 0;
                            for (int j = i - combineCount; j <= i; j++)
                                combinedLength += sentence.Words[j].length;
                            // Update the tuple with new length
                            sentence.Words[i - combineCount] = (firstToken.word, firstToken.position, combinedLength);
                            sentence.Words.RemoveRange(i - combineCount + 1, combineCount);
                            i = i - combineCount; // Adjust index after removal
                            found = true;
                            break;
                        }

                        // PRIORITY 2: Try WITHOUT ー (handles 久しぶりー → 久しぶり)
                        string? withoutBarHiragana;
                        try
                        {
                            withoutBarHiragana = KanaConverter.ToHiragana(combined);
                        }
                        catch
                        {
                            withoutBarHiragana = null;
                        }

                        if (withoutBarHiragana != null && _lookups.TryGetValue(withoutBarHiragana, out var idsWithout) &&
                            idsWithout.Count > 0)
                        {
                            // Found valid word WITHOUT ー - merge and strip ー
                            var firstToken = sentence.Words[i - combineCount];
                            firstToken.word.Text = combined;
                            // Calculate combined length from merged tokens (excluding ー)
                            int combinedLength = 0;
                            for (int j = i - combineCount; j < i; j++)
                                combinedLength += sentence.Words[j].length;
                            // Update the tuple with new length
                            sentence.Words[i - combineCount] = (firstToken.word, firstToken.position, combinedLength);
                            sentence.Words.RemoveRange(i - combineCount + 1, combineCount);
                            i = i - combineCount; // Adjust index after removal
                            found = true;
                            break;
                        }
                    }

                    // PRIORITY 3: Fallback
                    if (!found)
                    {
                        var prevTuple = sentence.Words[i - 1];
                        // If preceding token already ends with ー, this is just emphasis - discard it
                        // Otherwise attach ー to preserve stylistic usage
                        if (!prevTuple.word.Text.EndsWith("ー"))
                        {
                            prevTuple.word.Text += "ー";
                            // Update tuple with new length (previous length + ー length)
                            int newLength = prevTuple.length + sentence.Words[i].length;
                            sentence.Words[i - 1] = (prevTuple.word, prevTuple.position, newLength);
                        }

                        sentence.Words.RemoveAt(i);
                        // No need to adjust i since only 1 item removed at current position
                    }
                }
            }
        }

        /// <summary>
        /// Removes orphaned misparse tokens (single-kana fillers, junk tokens) that weren't consumed
        /// by earlier repair stages. Runs after RepairLongVowelMisparses/RepairLongVowelTokens so those
        /// stages can use filler tokens for backward-merge reconstruction (e.g. お+ま+えー → おまえ).
        /// </summary>
        private static void FilterOrphanedMisparses(List<SentenceInfo> sentences)
        {
            foreach (var sentence in sentences)
            {
                for (int i = sentence.Words.Count - 1; i >= 0; i--)
                {
                    var word = sentence.Words[i].word;

                    bool nextIsLongVowel = i + 1 < sentence.Words.Count &&
                                           sentence.Words[i + 1].word.Text == "ー";

                    bool shouldFilter = MisparsesRemove.Contains(word.Text) &&
                                        !(nextIsLongVowel && word.Text.Length == 1 && WanaKana.IsKana(word.Text)) &&
                                        !word.Text.EndsWith("ー");

                    bool isSingleKanaStutter = !nextIsLongVowel && word.Text.Length == 1 && WanaKana.IsKana(word.Text);
                    if (shouldFilter ||
                        word.PartOfSpeech == PartOfSpeech.Noun && !nextIsLongVowel && (
                            isSingleKanaStutter ||
                            word.Text is "エナ" or "えな"
                        ) ||
                        word.PartOfSpeech == PartOfSpeech.Symbol && isSingleKanaStutter)
                    {
                        sentence.Words.RemoveAt(i);
                    }
                }
            }
        }

        private static void ValidateGrammaticalSequences(List<SentenceInfo> sentences,
                                                         ParserDiagnostics? diagnostics = null)
        {
            foreach (var sentence in sentences)
                TransitionRuleEngine.ApplyHardRules(sentence.Words, HasNonNameLookup, diagnostics);
        }

        /// <summary>
        /// Combines adjacent noun tokens that form valid JMDict entries.
        /// Uses greedy longest-match: tries 4-token, then 3-token, then 2-token combinations.
        /// </summary>
        /// <summary>
        /// Splits noun tokens that have no JMDict lookup into individual kanji characters.
        /// This allows CombineNounCompounds to recombine them with adjacent tokens at better boundaries.
        /// e.g. Sudachi gives 各党 + 幹部, but 各党 has no lookup. Splitting into 各 + 党 lets
        /// CombineNounCompounds find 党幹部 in lookups and produce 各 + 党幹部.
        /// </summary>
        private static void SplitUnknownNounTokens(List<SentenceInfo> sentences)
        {
            foreach (var sentence in sentences)
            {
                if (sentence.Words.Count == 0)
                    continue;

                bool anySplit = false;
                var result = new List<(WordInfo word, int position, int length)>(sentence.Words.Count);

                foreach (var (word, position, length) in sentence.Words)
                {
                    // Split 〜的/〜的な suffix from nouns when the base word has a lookup but the full form doesn't.
                    // e.g. 母性的な → 母性 + 的な (母性 has lookup, 母性的/母性的な doesn't)
                    if (word.Text.Length >= 3 &&
                        (word.Text.EndsWith("的") || word.Text.EndsWith("的な")) &&
                        !HasLookup(word.Text))
                    {
                        var suffixLen = word.Text.EndsWith("的な") ? 2 : 1;
                        var baseText = word.Text[..^suffixLen];
                        var suffixText = word.Text[^suffixLen..];
                        if (HasLookup(baseText))
                        {
                            var baseWord = new WordInfo(word) { Text = baseText, DictionaryForm = baseText, NormalizedForm = baseText };
                            var suffixWord = new WordInfo(word)
                            {
                                Text = suffixText, DictionaryForm = suffixText, NormalizedForm = suffixText,
                                PartOfSpeech = PartOfSpeech.Suffix, Reading = suffixLen == 2 ? "テキナ" : "テキ"
                            };
                            result.Add((baseWord, position, baseText.Length));
                            result.Add((suffixWord, position + baseText.Length, suffixLen));
                            anySplit = true;
                            continue;
                        }
                    }

                    if (word.Text.Length >= 2 &&
                        PosMapper.IsNounForCompounding(word.PartOfSpeech) &&
                        word.Text.All(c => WanaKana.IsKanji(c.ToString())) &&
                        !(_lookups.TryGetValue(word.Text, out var ids) && ids.Count > 0))
                    {
                        for (int j = 0; j < word.Text.Length; j++)
                        {
                            var charText = word.Text[j].ToString();
                            var splitWord = new WordInfo(word)
                                            {
                                                Text = charText, DictionaryForm = charText, NormalizedForm = charText, Reading = ""
                                            };
                            result.Add((splitWord, position + j, 1));
                        }

                        anySplit = true;
                    }
                    else
                    {
                        result.Add((word, position, length));
                    }
                }

                if (anySplit)
                    sentence.Words = result;
            }
        }

        private static readonly HashSet<char> TrailingParticles =
        [
            'か', 'よ', 'ね', 'な', 'ぞ', 'ぜ', 'わ', 'さ', 'の', 'に', 'と', 'も', 'で', 'は', 'が'
        ];

        private static readonly HashSet<char> DictionaryVerbEndings =
        [
            'く', 'ぐ', 'す', 'つ', 'ぬ', 'ぶ', 'む', 'う', 'る'
        ];

        private static bool HasLookup(string text)
        {
            if (_lookups.TryGetValue(text, out var ids) && ids.Count > 0)
                return true;
            try
            {
                var hira = KanaNormalizer.Normalize(KanaConverter.ToHiragana(text, convertLongVowelMark: false));
                if (hira != text && _lookups.TryGetValue(hira, out ids) && ids.Count > 0)
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private static bool HasNonNameLookup(string text)
        {
            if (_lookups.TryGetValue(text, out var ids) && ids.Count > 0
                                                        && !ids.All(id => _nameOnlyWordIds.Contains(id)))
                return true;
            try
            {
                var hira = KanaNormalizer.Normalize(KanaConverter.ToHiragana(text, convertLongVowelMark: false));
                if (hira != text && _lookups.TryGetValue(hira, out ids) && ids.Count > 0
                    && !ids.All(id => _nameOnlyWordIds.Contains(id)))
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private static void StripTrailingParticles(List<SentenceInfo> sentences)
        {
            foreach (var sentence in sentences)
            {
                bool anySplit = false;
                var result = new List<(WordInfo word, int position, int length)>(sentence.Words.Count);

                foreach (var (word, position, length) in sentence.Words)
                {
                    if (word.Text.Length < 3 ||
                        word.PreMatchedWordId != null ||
                        word.PartOfSpeech is PartOfSpeech.Particle or PartOfSpeech.Auxiliary or
                            PartOfSpeech.Verb or PartOfSpeech.IAdjective or PartOfSpeech.SupplementarySymbol or
                            PartOfSpeech.Symbol or PartOfSpeech.Conjunction or PartOfSpeech.Adnominal or
                            PartOfSpeech.Prefix or PartOfSpeech.BlankSpace or PartOfSpeech.Name or
                            PartOfSpeech.Suffix or PartOfSpeech.NounSuffix or PartOfSpeech.Counter or
                            PartOfSpeech.Numeral or PartOfSpeech.Filler)
                    {
                        result.Add((word, position, length));
                        continue;
                    }

                    if (HasLookup(word.Text))
                    {
                        result.Add((word, position, length));
                        continue;
                    }

                    // Skip MA-merged tokens (e.g. 必要な, 遠慮しないで, 投下しました):
                    // their combined text won't be in lookups, but the base DictionaryForm is valid —
                    // let ProcessWord handle deconjugation instead of splitting them here
                    if (word.DictionaryForm != word.Text &&
                        !string.IsNullOrEmpty(word.DictionaryForm) &&
                        HasLookup(word.DictionaryForm))
                    {
                        result.Add((word, position, length));
                        continue;
                    }

                    var text = word.Text;
                    string textHira;
                    try
                    {
                        textHira = KanaNormalizer.Normalize(KanaConverter.ToHiragana(text, convertLongVowelMark: false));
                    }
                    catch
                    {
                        result.Add((word, position, length));
                        continue;
                    }

                    // Strategy 1a: Trailing particle strip (with lookup check on remainder)
                    char lastChar = textHira[^1];
                    bool hasTrailingParticle = textHira.Length >= 3 && TrailingParticles.Contains(lastChar);
                    bool remainderKnown = hasTrailingParticle && (HasLookup(text[..^1]) ||
                                                                  Deconjugator.Instance.Deconjugate(textHira[..^1])
                                                                              .Any(f => HasLookup(f.Text)));
                    if (remainderKnown)
                    {
                        var remainder = text[..^1];
                        var particleStr = text[^1..];
                        result.Add((new WordInfo(word) { Text = remainder, DictionaryForm = remainder, NormalizedForm = remainder },
                                    position, length - 1));
                        result.Add((new WordInfo { Text = particleStr, DictionaryForm = particleStr, PartOfSpeech = PartOfSpeech.Particle },
                                    position + length - 1, 1));
                        anySplit = true;
                        continue;
                    }

                    result.Add((word, position, length));
                }

                if (anySplit)
                    sentence.Words = result;
            }
        }

        private static readonly HashSet<string> NounCompoundExclusions = ["おつもり", "ものたち"];

        private static void CombineNounCompounds(List<SentenceInfo> sentences)
        {
            foreach (var sentence in sentences)
            {
                if (sentence.Words.Count < 2)
                    continue;

                var result = new List<(WordInfo word, int position, int length)>(sentence.Words.Count);
                int i = 0;

                while (i < sentence.Words.Count)
                {
                    var (word, position, length) = sentence.Words[i];

                    if (!PosMapper.IsNounForCompounding(word.PartOfSpeech))
                    {
                        // When an adjective stem is followed by a noun-like token, try combining them.
                        // Sudachi sometimes splits kana compounds like でかぶつ into でか (形容詞) + ぶつ (接尾辞),
                        // or ながもの into なが (形容詞) + もの (名詞).
                        if (word.PartOfSpeech == PartOfSpeech.IAdjective &&
                            i + 1 < sentence.Words.Count &&
                            PosMapper.IsNounForCompounding(sentence.Words[i + 1].word.PartOfSpeech))
                        {
                            var combinedText = word.Text + sentence.Words[i + 1].word.Text;
                            if (_lookups.TryGetValue(combinedText, out var adjSuffixIds) && adjSuffixIds.Count > 0 &&
                                !adjSuffixIds.All(id => _nameOnlyWordIds.Contains(id)))
                            {
                                var combinedReading = word.Reading + sentence.Words[i + 1].word.Reading;
                                int combinedLength = length + sentence.Words[i + 1].length;
                                var combinedWord = new WordInfo(word)
                                                   {
                                                       Text = combinedText, DictionaryForm = combinedText, PartOfSpeech = PartOfSpeech.Noun,
                                                       NormalizedForm = combinedText, Reading = KanaConverter.ToHiragana(combinedReading,
                                                           convertLongVowelMark: false),
                                                       PreMatchedWordId = null
                                                   };
                                result.Add((combinedWord, position, combinedLength));
                                i += 2;
                                continue;
                            }
                        }

                        result.Add(sentence.Words[i]);
                        i++;
                        continue;
                    }

                    // Try longest match first (5 tokens, then 4, then 3, then 2)
                    int bestMatch = 1;
                    for (int windowSize = Math.Min(5, sentence.Words.Count - i); windowSize >= 2; windowSize--)
                    {
                        bool allValid = true;
                        bool hasNameLikeToken = false;
                        for (int j = 0; j < windowSize; j++)
                        {
                            var w = sentence.Words[i + j].word;
                            bool isNoun = PosMapper.IsNounForCompounding(w.PartOfSpeech);
                            bool isNoParticle = w is { PartOfSpeech: PartOfSpeech.Particle, Text: "の" }
                                                && j > 0 && j < windowSize - 1;
                            if (!isNoun && !isNoParticle)
                            {
                                allValid = false;
                                break;
                            }

                            if (isNoun && PosMapper.IsNameLikeSudachiNoun(w.PartOfSpeech, w.PartOfSpeechSection1,
                                                                          w.PartOfSpeechSection2, w.PartOfSpeechSection3))
                                hasNameLikeToken = true;
                        }

                        if (!allValid)
                            continue;

                        // Combine the text
                        var combinedText = string.Concat(sentence.Words.Skip(i).Take(windowSize).Select(w => w.word.Text));

                        if (NounCompoundExclusions.Contains(combinedText))
                            continue;

                        // Check if it exists in JMDict lookups — skip name-only matches
                        // when none of the constituent tokens are name-like
                        if (_lookups.TryGetValue(combinedText, out var wordIds) && wordIds.Count > 0 &&
                            (hasNameLikeToken || !wordIds.All(id => _nameOnlyWordIds.Contains(id))))
                        {
                            bestMatch = windowSize;
                            break;
                        }

                        // Also try hiragana version
                        var hiraganaText = KanaConverter.ToHiragana(combinedText,
                                                                    convertLongVowelMark: false);
                        if (hiraganaText != combinedText &&
                            _lookups.TryGetValue(hiraganaText, out wordIds) && wordIds.Count > 0 &&
                            (hasNameLikeToken || !wordIds.All(id => _nameOnlyWordIds.Contains(id))))
                        {
                            bestMatch = windowSize;
                            break;
                        }
                    }

                    if (bestMatch > 1)
                    {
                        // Merge tokens
                        var combinedText = string.Concat(sentence.Words.Skip(i).Take(bestMatch).Select(w => w.word.Text));
                        var combinedReading = string.Concat(sentence.Words.Skip(i).Take(bestMatch).Select(w => w.word.Reading));
                        int combinedLength = 0;
                        for (int j = 0; j < bestMatch; j++)
                        {
                            combinedLength += sentence.Words[i + j].length;
                        }

                        // Preserve Sudachi POS section information when merging.
                        // Sudachi often tokenizes proper names into multiple parts, and we want the combined token to remain name-like.
                        var sectionCarrier = sentence.Words[i].word;
                        for (int j = 0; j < bestMatch; j++)
                        {
                            var candidate = sentence.Words[i + j].word;
                            if (PosMapper.IsNameLikeSudachiNoun(candidate.PartOfSpeech, candidate.PartOfSpeechSection1,
                                                                candidate.PartOfSpeechSection2, candidate.PartOfSpeechSection3))
                            {
                                sectionCarrier = candidate;
                                break;
                            }

                            if (candidate.PartOfSpeechSection1 != PartOfSpeechSection.None ||
                                candidate.PartOfSpeechSection2 != PartOfSpeechSection.None ||
                                candidate.PartOfSpeechSection3 != PartOfSpeechSection.None)
                            {
                                sectionCarrier = candidate;
                                break;
                            }
                        }

                        var combinedWord = new WordInfo(sectionCarrier)
                                           {
                                               Text = combinedText, DictionaryForm = combinedText,
                                               PartOfSpeech = sentence.Words[i].word.PartOfSpeech, NormalizedForm = combinedText, Reading =
                                                   KanaConverter.ToHiragana(combinedReading,
                                                                            convertLongVowelMark: false),
                                               PreMatchedWordId = null
                                           };

                        result.Add((combinedWord, position, combinedLength));
                        i += bestMatch;
                    }
                    else
                    {
                        result.Add(sentence.Words[i]);
                        i++;
                    }
                }

                sentence.Words = result;
            }
        }

        private static readonly HashSet<string> ExpressionExclusions = [];

        private static void CombineExpressions(List<SentenceInfo> sentences)
        {
            foreach (var sentence in sentences)
            {
                if (sentence.Words.Count < 3)
                    continue;

                var result = new List<(WordInfo word, int position, int length)>(sentence.Words.Count);
                int i = 0;

                while (i < sentence.Words.Count)
                {
                    int bestMatch = 1;
                    string? matchedText = null;

                    int maxWindow = Math.Min(8, sentence.Words.Count - i);
                    for (int windowSize = maxWindow; windowSize >= 3; windowSize--)
                    {
                        bool hasParticle = false;
                        bool hasSupplementarySymbol = false;

                        for (int j = 0; j < windowSize; j++)
                        {
                            var w = sentence.Words[i + j].word;
                            if (w.PartOfSpeech == PartOfSpeech.SupplementarySymbol)
                            {
                                hasSupplementarySymbol = true;
                                break;
                            }

                            if (w.PartOfSpeech == PartOfSpeech.Particle)
                                hasParticle = true;
                        }

                        if (hasSupplementarySymbol || !hasParticle)
                            continue;

                        var combinedText = string.Concat(
                                                         sentence.Words.Skip(i).Take(windowSize).Select(w => w.word.Text));

                        if (ExpressionExclusions.Contains(combinedText))
                            continue;

                        bool matched = false;
                        if (_lookups.TryGetValue(combinedText, out var wordIds) && wordIds.Count > 0 &&
                            wordIds.Any(id => _expressionWordIds.Contains(id)))
                        {
                            matched = true;
                        }

                        if (!matched)
                        {
                            var hiraganaText = KanaConverter.ToHiragana(combinedText, convertLongVowelMark: false);
                            if (hiraganaText != combinedText &&
                                _lookups.TryGetValue(hiraganaText, out wordIds) && wordIds.Count > 0 &&
                                wordIds.Any(id => _expressionWordIds.Contains(id)))
                            {
                                matched = true;
                            }
                        }

                        if (matched)
                        {
                            bestMatch = windowSize;
                            matchedText = combinedText;
                            break;
                        }
                    }

                    if (bestMatch > 1)
                    {
                        var combinedText = matchedText!;
                        var combinedReading = string.Concat(
                                                            sentence.Words.Skip(i).Take(bestMatch).Select(w => w.word.Reading));
                        int combinedLength = 0;
                        for (int j = 0; j < bestMatch; j++)
                            combinedLength += sentence.Words[i + j].length;

                        var combinedWord = new WordInfo
                                           {
                                               Text = combinedText, DictionaryForm = combinedText, PartOfSpeech = PartOfSpeech.Expression,
                                               NormalizedForm = combinedText,
                                               Reading = KanaConverter.ToHiragana(combinedReading, convertLongVowelMark: false),
                                               PreMatchedWordId = null
                                           };

                        result.Add((combinedWord, sentence.Words[i].position, combinedLength));
                        i += bestMatch;
                    }
                    else
                    {
                        result.Add(sentence.Words[i]);
                        i++;
                    }
                }

                sentence.Words = result;
            }
        }

        /// <summary>
        /// Cleans token text in sentences: removes non-Japanese characters and problematic sequences.
        /// Also removes empty tokens (except SupplementarySymbol boundary markers).
        /// </summary>
        private static void CleanSentenceTokens(List<SentenceInfo> sentences)
        {
            foreach (var sentence in sentences)
            {
                foreach (var (word, _, _) in sentence.Words)
                {
                    // Keep SupplementarySymbol tokens as boundary markers
                    if (word.PartOfSpeech == PartOfSpeech.SupplementarySymbol)
                        continue;

                    word.Text = TokenCleanRegex.Replace(word.Text, "");
                    word.Text = SmallTsuLongVowelRegex.Replace(word.Text, "");
                }

                sentence.Words.RemoveAll(w => string.IsNullOrWhiteSpace(w.word.Text) &&
                                              w.word.PartOfSpeech != PartOfSpeech.SupplementarySymbol);
            }
        }

        private static void CleanCompoundCache()
        {
            if (CompoundExpressionCache.Count < MAX_COMPOUND_CACHE_SIZE)
                return;

            // Evict oldest entries (first added) instead of clearing all
            int toRemove = Math.Min(EVICTION_BATCH_SIZE, CompoundCacheOrder.Count);
            for (int i = 0; i < toRemove; i++)
            {
                var oldest = CompoundCacheOrder.First;
                if (oldest == null)
                    continue;

                CompoundExpressionCache.Remove(oldest.Value);
                CompoundCacheOrder.RemoveFirst();
            }
        }

        private static void AddToCompoundCache(string key, (bool validExpression, int? wordId) value)
        {
            CleanCompoundCache();
            if (CompoundExpressionCache.TryAdd(key, value))
                CompoundCacheOrder.AddLast(key);
        }

        private static List<DeckWord> ExcludeFinalMisparses(IEnumerable<DeckWord> words)
        {
            if (ExcludedMisparses.Count == 0)
                return words.ToList();

            return words.Where(w => !ExcludedMisparses.Contains((w.WordId, w.ReadingIndex))).ToList();
        }

        private static async Task<(int startIndex, string dictionaryForm, int wordId)?> TryMatchCompounds(
            List<WordInfo> wordInfos,
            int wordIndex,
            int lastConsumedIndex = -1)
        {
            var verb = wordInfos[wordIndex];

            var dictForm = verb.DictionaryForm;

            if (string.IsNullOrEmpty(dictForm))
            {
                var deconjugated = Deconjugator.Instance.Deconjugate(KanaConverter.ToHiragana(verb.Text));
                if (deconjugated.Count == 0) return null;
                var selectedForm = deconjugated.OrderBy(d => d.Text.Length).ThenBy(d => d.Text, StringComparer.Ordinal).First();
                // If the shortest form is empty (e.g. ない → "" from stem-mizenkei rules),
                // use the original verb text to avoid matching just the prefix (しょうが) instead of the full compound (しょうがない)
                dictForm = string.IsNullOrEmpty(selectedForm.Text) ? verb.Text : selectedForm.Text;
                return await TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, dictForm);
            }

            // Try Sudachi's dictionary form first
            var result = await TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, dictForm);
            if (result.HasValue) return result;

            // Many JMDict expressions include the negative form (e.g. びくともしない, 関係ない).
            // When the verb is a negative conjugation of する/ある, also try the negative base form.
            // Only fire when the surface is actually negated — otherwise positive expressions like
            // 資格がある incorrectly match the negative-form entry 資格がない (opposite polarity).
            if (dictForm is "する" or "ある"
                && (verb.Text.Contains("ない") || verb.Text.Contains("なかっ") || verb.Text.Contains("なく")
                    || verb.Text.Contains("ねえ") || verb.Text.Contains("ねー") || verb.Text.Contains("ねぇ")
                    || verb.Text.Contains("ませ") || verb.Text.EndsWith("ん") || verb.Text.EndsWith("ず")))
            {
                var negForm = dictForm == "する" ? "しない" : "ない";
                result = await TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, negForm);
                if (result.HasValue) return result;
            }

            // Try intermediate deconjugation forms for compound expressions whose JMDict entry
            // includes a conjugation stem (e.g. 臆病風に吹かれる: dictForm is 吹く but the
            // lookup key contains the passive form 吹かれる)
            if (dictForm != verb.Text && verb.Text.Length > dictForm.Length)
            {
                var deconj = Deconjugator.Instance.Deconjugate(verb.Text);
                var intermediates = new HashSet<string>(StringComparer.Ordinal);
                foreach (var form in deconj)
                {
                    foreach (var seen in form.SeenText)
                    {
                        if (seen.Length > dictForm.Length && seen.Length < verb.Text.Length)
                            intermediates.Add(seen);
                    }
                }

                foreach (var intermediate in intermediates.OrderByDescending(s => s.Length))
                {
                    result = await TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, intermediate);
                    if (result.HasValue) return result;
                }

                // Also try deconjugated base forms shorter than dictForm, ending in a verb ending.
                // Handles e.g. しゃくれさせる (causative of v1 potential) → しゃくる (v5r base),
                // allowing 顎をしゃくれさせる to match the expression 顎をしゃくる.
                var shortBaseForms = new HashSet<string>(StringComparer.Ordinal);
                foreach (var form in deconj)
                {
                    if (form.Text.Length >= 3 && form.Text.Length < dictForm.Length
                                              && DictionaryVerbEndings.Contains(form.Text[^1]))
                        shortBaseForms.Add(form.Text);
                }

                foreach (var shortForm in shortBaseForms.OrderByDescending(s => s.Length))
                {
                    result = await TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, shortForm);
                    if (result.HasValue) return result;
                }
            }

            if (dictForm == verb.Text)
            {
                // Try NormalizedForm for lexicalized conjugated forms where Sudachi treats the
                // surface as its own dictionary entry (e.g., go-dan potential かかれる has
                // dictForm="かかれる" but normalizedForm="掛かる" → enables お目にかかる match)
                if (verb.PartOfSpeech == PartOfSpeech.Verb
                    && !string.IsNullOrEmpty(verb.NormalizedForm) && verb.NormalizedForm != dictForm)
                {
                    result = await TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, verb.NormalizedForm);
                    if (result.HasValue) return result;

                    var hiraganaNorm = KanaConverter.ToHiragana(verb.NormalizedForm, convertLongVowelMark: false);
                    if (hiraganaNorm != verb.NormalizedForm)
                    {
                        result = await TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, hiraganaNorm);
                        if (result.HasValue) return result;
                    }
                }

                // Try deconjugated forms for kanji tokens where Sudachi failed to deconjugate.
                // Skip if the kana form ends in a standard dictionary verb ending.
                var verbKana = KanaConverter.ToHiragana(verb.Text, convertLongVowelMark: false);
                if (verb.Text.Any(c => WanaKana.IsKanji(c.ToString()))
                    && !DictionaryVerbEndings.Contains(verbKana[^1]))
                {
                    var deconj = Deconjugator.Instance.Deconjugate(KanaConverter.ToHiragana(verb.Text));
                    foreach (var form in deconj
                                         .Select(d => d.Text)
                                         .Where(t => !string.IsNullOrEmpty(t) && t != dictForm)
                                         .Distinct()
                                         .OrderBy(t => t.Length)
                                         .ThenBy(t => t, StringComparer.Ordinal))
                    {
                        result = await TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, form);
                        if (result.HasValue) return result;
                    }
                }
            }

            return null;
        }

        private static async Task<(int startIndex, string dictionaryForm, int wordId)?> TryMatchCompoundWindow(
            List<WordInfo> wordInfos,
            int wordIndex,
            int lastConsumedIndex,
            string dictForm)
        {
            for (int windowSize = Math.Min(5, wordIndex + 1); windowSize >= 2; windowSize--)
            {
                int startIndex = wordIndex - windowSize + 1;

                if (startIndex <= lastConsumedIndex)
                    continue;

                var firstWord = wordInfos[startIndex];
                if (firstWord.PartOfSpeech == PartOfSpeech.Particle)
                    continue;
                if (firstWord is { PartOfSpeech: PartOfSpeech.Conjunction, Text.Length: 1 })
                    continue;
                if (firstWord.WasReclassifiedFromSuffix)
                    continue;

                bool expressionOnly = firstWord.PartOfSpeech is PartOfSpeech.Suffix or PartOfSpeech.Auxiliary;

                bool hasPunctuation = false;
                for (int j = startIndex; j < wordIndex; j++)
                {
                    if (wordInfos[j].PartOfSpeech == PartOfSpeech.SupplementarySymbol)
                    {
                        hasPunctuation = true;
                        break;
                    }
                }

                if (hasPunctuation)
                    continue;

                var prefix = string.Concat(wordInfos.Skip(startIndex).Take(windowSize - 1).Select(w => w.Text));

                if (windowSize == 2 && prefix == dictForm && wordInfos[wordIndex].Text != dictForm)
                    continue;

                var candidate = prefix + dictForm;

                if (!expressionOnly)
                {
                    lock (CompoundCacheLock)
                    {
                        if (CompoundExpressionCache.TryGetValue(candidate, out var cached))
                        {
                            if (cached.validExpression)
                                return (startIndex, candidate, cached.wordId!.Value);

                            continue;
                        }
                    }
                }

                if (_lookups.TryGetValue(candidate, out var wordIds) && wordIds.Count > 0)
                {
                    var validWordId =
                        await CompoundWordSelector.FindValidCompoundWordId(wordIds, JmDictCache, expressionOnly,
                                                                           WanaKana.IsKana(candidate));
                    if (validWordId.HasValue)
                    {
                        lock (CompoundCacheLock)
                        {
                            AddToCompoundCache(candidate, (true, validWordId.Value));
                        }

                        return (startIndex, candidate, validWordId.Value);
                    }
                }

                var hiraganaCandidate = KanaConverter.ToHiragana(candidate, convertLongVowelMark: false);
                if (hiraganaCandidate != candidate && _lookups.TryGetValue(hiraganaCandidate, out wordIds) && wordIds.Count > 0)
                {
                    var validWordId =
                        await CompoundWordSelector.FindValidCompoundWordId(wordIds, JmDictCache, expressionOnly, isKana: true);
                    if (validWordId.HasValue)
                    {
                        lock (CompoundCacheLock)
                        {
                            AddToCompoundCache(candidate, (true, validWordId.Value));
                        }

                        return (startIndex, candidate, validWordId.Value);
                    }
                }

                lock (CompoundCacheLock)
                {
                    AddToCompoundCache(candidate, (false, null));
                }
            }

            return null;
        }

        #region Form-based pair scoring

        private static (FormCandidate? best, int? margin) PickBestFormCandidate(
            List<FormCandidate> allCandidates,
            string surface,
            string? dictionaryForm,
            string? normalizedForm,
            bool isNameContext,
            ParserDiagnostics? diagnostics = null,
            string? sudachiReading = null)
        {
            var context = FormScoringContext.Create(
                                                    surface,
                                                    dictionaryForm,
                                                    normalizedForm,
                                                    isNameContext,
                                                    sudachiReading);

            var result = FormCandidateSelector.PickTopCandidates(allCandidates, context, ArchaicPosTypes, diagnostics);
            return (result.Best, result.MarginToSecond);
        }

        #endregion

        #region Adjacent-word scoring

        private static (string text, PartOfSpeech pos, string dictionaryForm, string reading, bool isPersonNameContext, bool isNameLike)
            GetDedupKey(WordInfo wi) =>
            (wi.Text, wi.PartOfSpeech, wi.DictionaryForm, wi.Reading, wi.IsPersonNameContext,
             PosMapper.IsNameLikeSudachiNoun(wi.PartOfSpeech, wi.PartOfSpeechSection1,
                                             wi.PartOfSpeechSection2, wi.PartOfSpeechSection3));

        private static (DeckWord? word, int? margin) LookupResult(
            WordInfo wi,
            Dictionary<(string, PartOfSpeech, string, string, bool, bool), (DeckWord? word, int? margin)> resultLookup)
        {
            var key = GetDedupKey(wi);
            resultLookup.TryGetValue(key, out var result);
            return result;
        }

        private static async Task<List<DeckWord>> ApplyAdjacentScoring(
            List<SentenceInfo> sentences,
            List<(DeckWord? word, int? margin)> processedResults,
            ParserDiagnostics? diagnostics = null)
        {
            int pos = 0;
            var sentencePairs = BuildSentencePairs(sentences, wi =>
                                                       pos < processedResults.Count ? processedResults[pos++] : (null, null));
            return await ApplyAdjacentScoringCore(sentencePairs, diagnostics);
        }

        private static async Task<List<DeckWord>> ApplyAdjacentScoring(
            List<SentenceInfo> sentences,
            Dictionary<(string, PartOfSpeech, string, string, bool, bool), (DeckWord? word, int? margin)> resultLookup,
            ParserDiagnostics? diagnostics = null)
        {
            var sentencePairs = BuildSentencePairs(sentences, wi => LookupResult(wi, resultLookup));
            return await ApplyAdjacentScoringCore(sentencePairs, diagnostics);
        }

        private static List<List<(WordInfo word, DeckWord? result, int? margin)>> BuildSentencePairs(
            List<SentenceInfo> sentences,
            Func<WordInfo, (DeckWord? word, int? margin)> lookupResult)
        {
            var result = new List<List<(WordInfo, DeckWord?, int?)>>(sentences.Count);
            foreach (var sentence in sentences)
            {
                var pairs = sentence.Words
                                    .Where(w => w.word.PartOfSpeech != PartOfSpeech.SupplementarySymbol)
                                    .Select(w =>
                                    {
                                        var (word, margin) = lookupResult(w.word);
                                        return (w.word, word, margin);
                                    })
                                    .ToList();
                result.Add(pairs);
            }

            return result;
        }

        private static async Task<List<DeckWord>> ApplyAdjacentScoringCore(
            List<List<(WordInfo word, DeckWord? result, int? margin)>> sentencePairs,
            ParserDiagnostics? diagnostics = null)
        {
            // Pass 1: identify tokens needing rederivation and collect all word IDs
            var allWordIds = new HashSet<int>();
            var rederiveStates = new Dictionary<(int sentIdx, int tokIdx), RederivationHelper.RederiveState>();

            for (int si = 0; si < sentencePairs.Count; si++)
            {
                var sentenceWords = sentencePairs[si];
                bool isArchaicPass1 = IsClassicalSentence(sentenceWords);
                for (int i = 0; i < sentenceWords.Count; i++)
                {
                    var (currentInfo, currentResult, currentMargin) = sentenceWords[i];
                    if (currentResult == null) continue;

                    // High confidence → skip rederivation, unless archaic sentence (Phase 1 scored without archaic context)
                    if (!isArchaicPass1 && currentMargin >= ScoringPolicy.HighConfidenceThreshold) continue;

                    WordInfo? prevInfo = i > 0 ? sentenceWords[i - 1].word : null;
                    WordInfo? nextInfo = i < sentenceWords.Count - 1 ? sentenceWords[i + 1].word : null;
                    var prevResult = i > 0 ? sentenceWords[i - 1].result : null;
                    var nextResult = i < sentenceWords.Count - 1 ? sentenceWords[i + 1].result : null;

                    // Low confidence → always rederive (bypass HasApplicableRules check)
                    bool forceRederive = currentMargin.HasValue && currentMargin.Value < ScoringPolicy.LowConfidenceThreshold;

                    if (!forceRederive && !isArchaicPass1 && !TransitionRuleEngine.CouldAnySoftRuleApply(
                         currentResult.PartsOfSpeech, currentInfo.Text,
                         prevResult?.PartsOfSpeech, prevInfo?.Text,
                         nextResult?.PartsOfSpeech, nextInfo?.Text))
                        continue;

                    var state = RederivationHelper.CollectRederivationIds(currentInfo, _lookups, Deconjugator.Instance);
                    if (state == null) continue;

                    rederiveStates[(si, i)] = state;
                    allWordIds.UnionWith(state.CandidateIds);
                }
            }

            // Single batch fetch for all needed words
            Dictionary<int, JmDictWord> wordCache;
            try
            {
                wordCache = allWordIds.Count > 0
                    ? await JmDictCache.GetWordsAsync(allWordIds)
                    : new Dictionary<int, JmDictWord>();
            }
            catch
            {
                wordCache = new Dictionary<int, JmDictWord>();
            }

            // Pass 2: score and pick best candidates
            var corrected = new List<DeckWord>();
            int globalPos = 0;

            for (int si = 0; si < sentencePairs.Count; si++)
            {
                var sentenceWords = sentencePairs[si];
                bool isArchaicSentence = IsClassicalSentence(sentenceWords);
                var resolvedResults = sentenceWords.Select(sw => sw.result).ToArray();

                for (int i = 0; i < sentenceWords.Count; i++)
                {
                    var (currentInfo, currentResult, _) = sentenceWords[i];
                    globalPos++;

                    if (currentResult == null)
                    {
                        diagnostics?.LogDroppedToken(currentInfo.Text, currentInfo.PartOfSpeech,
                            "Unresolved: no JMDict match found during lookup");
                        continue;
                    }

                    if (!rederiveStates.TryGetValue((si, i), out var state))
                    {
                        currentInfo.ResolvedWordId = currentResult.WordId;
                        corrected.Add(currentResult);
                        continue;
                    }

                    var candidates = RederivationHelper.BuildCandidatesFromWords(state, wordCache);

                    if (candidates.Count == 0)
                    {
                        currentInfo.ResolvedWordId = currentResult.WordId;
                        corrected.Add(currentResult);
                        continue;
                    }

                    WordInfo? prevInfo = i > 0 ? sentenceWords[i - 1].word : null;
                    WordInfo? nextInfo = i < sentenceWords.Count - 1 ? sentenceWords[i + 1].word : null;
                    var prevResult = i > 0 ? resolvedResults[i - 1] : null;
                    var nextResult = i < sentenceWords.Count - 1 ? sentenceWords[i + 1].result : null;

                    var context = new AdjacentWordScorer.AdjacentContext(
                                                                         PrevResolvedPOS: prevResult?.PartsOfSpeech,
                                                                         NextResolvedPOS: nextResult?.PartsOfSpeech,
                                                                         PrevText: prevInfo?.Text,
                                                                         NextText: nextInfo?.Text);

                    var scoringContext = FormScoringContext.Create(
                                                                   currentInfo.Text, currentInfo.DictionaryForm, currentInfo.NormalizedForm,
                                                                   currentInfo.IsPersonNameContext, currentInfo.Reading,
                                                                   isArchaicSentence,
                                                                   isSentenceInitial: i == 0,
                                                                   isSentenceFinal: i == sentenceWords.Count - 1);

                    bool anyNonZeroBonus = false;
                    var bonusCache = new Dictionary<FormCandidate, (int bonus, List<string> rules)>();
                    foreach (var candidate in candidates)
                    {
                        var trace = FormCandidateScorer.Score(candidate, scoringContext, ArchaicPosTypes);
                        candidate.SetScoreTrace(trace);

                        var cached = AdjacentWordScorer.CalculateContextBonus(candidate, context);
                        bonusCache[candidate] = cached;
                        if (cached.bonus != 0) anyNonZeroBonus = true;
                    }

                    // Archaic sentence context changes base scores; if it would flip the winner, also re-select.
                    if (!anyNonZeroBonus && isArchaicSentence)
                    {
                        var phase2Best = candidates.MaxBy(c => c.TotalScore);
                        anyNonZeroBonus = phase2Best != null &&
                                          (phase2Best.Word.WordId != currentResult.WordId ||
                                           phase2Best.ReadingIndex != currentResult.ReadingIndex);
                    }

                    if (!anyNonZeroBonus)
                    {
                        currentInfo.ResolvedWordId = currentResult.WordId;
                        corrected.Add(currentResult);
                        continue;
                    }

                    var newBest = FormCandidateSelector.PickTopCandidatesWithBonus(candidates,
                                                                                   c => bonusCache.TryGetValue(c, out var cb)
                                                                                       ? cb.bonus
                                                                                       : 0);
                    var (newBestBonus, newBestRules) = newBest != null && bonusCache.TryGetValue(newBest, out var nb)
                        ? (nb.bonus, nb.rules)
                        : (0, (List<string>?)null);
                    int newBestAdjusted = newBest != null ? ScoringPolicy.EffectiveScore(newBest) + newBestBonus : int.MinValue;

                    bool changed = newBest != null &&
                                   (newBest.Word.WordId != currentResult.WordId || newBest.ReadingIndex != currentResult.ReadingIndex);

                    if (diagnostics != null && newBest != null && newBestRules is { Count: > 0 })
                    {
                        var firstPassScore = candidates.FirstOrDefault(c =>
                                                                           c.Word.WordId == currentResult.WordId &&
                                                                           c.ReadingIndex == currentResult.ReadingIndex)?.TotalScore ?? 0;

                        diagnostics.AdjacentScoring.Add(new AdjacentScoringEntry
                                                        {
                                                            Position = globalPos - 1, Surface = currentInfo.Text, LeftContext =
                                                                prevInfo != null
                                                                    ? new AdjacentTokenInfo
                                                                      {
                                                                          Text = prevInfo.Text, Pos = prevInfo.PartOfSpeech
                                                                      }
                                                                    : null,
                                                            RightContext = nextInfo != null
                                                                ? new AdjacentTokenInfo
                                                                  {
                                                                      Text = nextInfo.Text, Pos = nextInfo.PartOfSpeech
                                                                  }
                                                                : null,
                                                            RulesMatched = newBestRules,
                                                            FirstPassWinner = new AdjacentCandidateInfo
                                                                              {
                                                                                  WordId = currentResult.WordId,
                                                                                  ReadingIndex = currentResult.ReadingIndex,
                                                                                  Score = firstPassScore, ContextBonus = 0,
                                                                                  AdjustedScore = firstPassScore
                                                                              },
                                                            AdjustedWinner = new AdjacentCandidateInfo
                                                                             {
                                                                                 WordId = newBest.Word.WordId,
                                                                                 ReadingIndex = newBest.ReadingIndex,
                                                                                 Score = newBest.TotalScore, ContextBonus = newBestBonus,
                                                                                 AdjustedScore = newBestAdjusted
                                                                             },
                                                            Changed = changed
                                                        });
                    }

                    if (changed && newBest != null)
                    {
                        bool wordIdChanged = newBest.Word.WordId != currentResult.WordId;
                        var newResult = new DeckWord
                                        {
                                            WordId = newBest.Word.WordId, OriginalText = currentInfo.Text,
                                            ReadingIndex = newBest.ReadingIndex, Occurrences = currentResult.Occurrences, Conjugations =
                                                newBest.DeconjForm?.Process is ["casual kind request"] &&
                                                newBest.Word.PartsOfSpeech.Contains("adj-na")
                                                    ? []
                                                    : newBest.DeconjForm?.Process.ToList() ??
                                                      (wordIdChanged ? [] : currentResult.Conjugations),
                                            PartsOfSpeech = newBest.Word.CachedPOS, Origin = newBest.Word.Origin,
                                            SudachiReading = currentInfo.Reading, SudachiPartOfSpeech = currentInfo.PartOfSpeech
                                        };
                        currentInfo.ResolvedWordId = newBest.Word.WordId;
                        corrected.Add(newResult);
                        resolvedResults[i] = newResult;
                    }
                    else
                    {
                        currentInfo.ResolvedWordId = currentResult.WordId;
                        corrected.Add(currentResult);
                    }
                }
            }

            MergeNaAdjAttributive(corrected);
            MergeNdaExpression(corrected);

            return corrected;
        }

        // Writeback merge: bare ん + だ → んだ expression (JMDict 2849387), AND なん + だ → なんだ
        // (where なん is treated as a 2-char form of the ん explanatory). The lattice often fails
        // to keep these bundled — the 1-char edges for ん and だ outscore the 2-char compound because
        // both pieces are common particles. The test suite always expects んだ / なんだ as one token
        // after a predicate. Gated on the preceding token being verb / adj-i / adj-na / expression
        // (so standalone ん at sentence start stays split: `ほうがいい + ん + じゃない` has じゃない, not だ).
        private const int NdaExpressionWordId = 2849387;
        private static void MergeNdaExpression(List<DeckWord> words)
        {
            for (int i = words.Count - 2; i >= 0; i--)
            {
                var cur = words[i];
                var next = words[i + 1];
                if (next.OriginalText != "だ") continue;
                if (cur.OriginalText != "ん" && cur.OriginalText != "なん") continue;
                if (i == 0) continue;
                var prev = words[i - 1];
                if (!PrecedesNdaExpression(prev)) continue;

                cur.OriginalText = cur.OriginalText + "だ";
                cur.WordId = NdaExpressionWordId;
                cur.PartsOfSpeech = new List<PartOfSpeech> { PartOfSpeech.Expression };
                words.RemoveAt(i + 1);
            }
        }

        private static bool PrecedesNdaExpression(DeckWord prev)
        {
            foreach (var p in prev.PartsOfSpeech)
            {
                if (p == PartOfSpeech.Verb || p == PartOfSpeech.IAdjective
                    || p == PartOfSpeech.NaAdjective || p == PartOfSpeech.Auxiliary
                    || p == PartOfSpeech.Expression)
                    return true;
            }
            return false;
        }

        // Writeback merge: adj-na + bare な → single attributive token. Pure output-side post-processing;
        // runs after beam segmentation so mis-split na-adjectives (e.g. 元気 + な) are rejoined.
        private static void MergeNaAdjAttributive(List<DeckWord> words)
        {
            for (int i = words.Count - 2; i >= 0; i--)
            {
                var cur = words[i];
                var next = words[i + 1];
                if (next.OriginalText != "な") continue;
                if (cur == null || next == null) continue;
                if (!cur.PartsOfSpeech.Contains(PartOfSpeech.NaAdjective)) continue;

                // Only merge when the adj-na token is its canonical dictionary surface — avoids merging
                // derived surfaces (e.g. verb+そう which Ichiran also tags adj-na) that tests expect split.
                if (!string.IsNullOrEmpty(cur.SudachiReading) &&
                    cur.SudachiPartOfSpeech != PartOfSpeech.NaAdjective &&
                    cur.SudachiPartOfSpeech != PartOfSpeech.Noun &&
                    cur.SudachiPartOfSpeech != PartOfSpeech.PrenounAdjectival &&
                    cur.SudachiPartOfSpeech != PartOfSpeech.NominalAdjective)
                    continue;

                cur.OriginalText += "な";
                var conj = cur.Conjugations;
                if (!conj.Contains("attributive"))
                {
                    conj.Add("attributive");
                    cur.Conjugations = conj;
                }
                words.RemoveAt(i + 1);
            }
        }

        #endregion

        #region Confidence resegmentation helpers

        private static Dictionary<(int sentenceIndex, int wordIndex), int?> BuildMarginMap(
            List<SentenceInfo> sentences,
            List<(DeckWord? word, int? margin)> processedWithMargins)
        {
            var map = new Dictionary<(int, int), int?>();
            int flatIndex = 0;
            for (int si = 0; si < sentences.Count; si++)
            {
                for (int wi = 0; wi < sentences[si].Words.Count; wi++)
                {
                    if (sentences[si].Words[wi].word.PartOfSpeech == PartOfSpeech.SupplementarySymbol)
                        continue;
                    if (flatIndex < processedWithMargins.Count)
                        map[(si, wi)] = processedWithMargins[flatIndex].margin;
                    flatIndex++;
                }
            }

            return map;
        }

        private static Dictionary<(int sentenceIndex, int wordIndex), int?> BuildMarginMapFromLookup(
            List<SentenceInfo> sentences,
            Dictionary<(string, PartOfSpeech, string, string, bool, bool), (DeckWord? word, int? margin)> resultLookup)
        {
            var map = new Dictionary<(int, int), int?>();
            for (int si = 0; si < sentences.Count; si++)
            {
                for (int wi = 0; wi < sentences[si].Words.Count; wi++)
                {
                    var word = sentences[si].Words[wi].word;
                    if (word.PartOfSpeech == PartOfSpeech.SupplementarySymbol)
                        continue;
                    var key = GetDedupKey(word);
                    if (resultLookup.TryGetValue(key, out var result))
                        map[(si, wi)] = result.margin;
                }
            }

            return map;
        }

        #endregion
    }
}
