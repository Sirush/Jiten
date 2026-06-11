using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Utils;
using Jiten.Parser.Data.Redis;
using Jiten.Parser.Diagnostics;
using Jiten.Parser.Grammar;
using Jiten.Parser.Misparse;
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
        public static bool RubyPriorsEnabled
        {
            get => Scoring.RubyReadingPriors.Enabled;
            set => Scoring.RubyReadingPriors.Enabled = value;
        }

        private static readonly bool UseCache = true;

        private static readonly ParserRuntime _parserRuntime = new();
        private static IDeckWordCache DeckWordCache = null!;
        private static IJmDictCache JmDictCache = null!;

        private static IDbContextFactory<JitenDbContext> _contextFactory = null!;
        private static Dictionary<string, List<int>> _lookups = null!;
        private static Dictionary<int, int> _wordFrequencyRanks = null!;
        private static HashSet<int> _nameOnlyWordIds = null!;
        private static HashSet<int> _expressionWordIds = null!;
        private static HashSet<int> _kanjiBackedWordIds = null!;
        internal static Dictionary<int, Data.JmDictWordMeta> WordMeta = null!;

        private static int _maxLookupKeyLength;
        private static Dictionary<string, List<int>>.AlternateLookup<ReadOnlySpan<char>> _lookupsAlt;

        private static HashSet<long>? _compoundHashSet;
        private static long[]? _hashBasePowers;
        private const long HASH_BASE = 131L;

        // Compiled regexes for token cleaning (avoid recompilation on every token)
        private static readonly Regex TokenCleanRegex = new(
                                                            @"[^a-zA-Z0-9\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FAF\uFF21-\uFF3A\uFF41-\uFF5A\uFF10-\uFF19\u3005．]",
                                                            RegexOptions.Compiled);

        private static readonly Regex SmallTsuLongVowelRegex = new(@"ッー", RegexOptions.Compiled);
        private static readonly Regex MultiLongVowelRegex = new(@"ー{2,}", RegexOptions.Compiled);
        private static readonly Regex DialogueRegex = new(@"[「『].{0,200}?[」』]", RegexOptions.Compiled | RegexOptions.Singleline);

        // Surface-text misparse tokens to remove after repair stages (deferred from MorphologicalAnalyser
        // so RepairLongVowelMisparses can use them for backward-merge reconstruction)
        private static readonly HashSet<string> MisparsesRemove =
        [
            "そ", "る", "ま", "ふ", "ち", "ほ", "す", "じ", "なさ", "い", "ぴ", "ふあ", "ぷ", "ちゅ", "にっ", "じら", "タ", "け", "イ", "イッ", "ほっ", "そっ",
            "ウー", "うー", "ううう", "うう", "ウウウウ", "ウウ", "ううっ", "かー", "ぐわー", "違", "タ", "ッ", "ニヒヒ"
        ];

        // Excluded (WordId, ReadingIndex) pairs to filter from final parsing results
        private static readonly HashSet<(int WordId, byte ReadingIndex)> ExcludedMisparses =
        [
            (1291070, 1), (1587980, 1), (1443970, 5), (2029660, 0), (1177490, 5), (2029000, 1),
            (1244950, 1), (1243940, 1), (2747970, 1), (2029680, 0), (1193570, 6), (1796500, 2),
            (1811220, 1), (2654270, 0), (2269410, 1), (2439040, 3), (2861095, 0), (2836250, 0),
            (1595910, 4), (2577750, 0), (1365520, 1), (1310720, 1), (1528180, 1), (2866457, 1),
            (2394370, 4), (1203250, 2), (1537250, 2), (2783750, 1), (2654250, 0), (2609820, 1),
            (2080360, 3), (1333240, 2), (2035220, 2), (5616612, 5), (2249020, 1), (2783700, 1),
            (2411420, 0), (1604890, 2), (2602280, 1), (1407450, 1), (1595120, 1), (2083370, 1),
            (2862482, 0), (2849996, 0), (1266970, 2), (2574180, 2), (2574180, 1), (1550770, 1),
            (5626489, 28), (5045509, 3), (2029780, 0), (5430309, 1), (1496170, 2), (2564800, 1),
            (2026870, 1), (1585310, 4), (1585310, 5), (2252690, 1), (2835861, 0), (1223130, 1),
            (1246880, 1), (1246880, 2), (1461140, 8), (1461140, 6), (2029700, 0), (2594040, 2),
            (1324950, 1), (1949190, 1), (1344210, 1), (2029730, 0), (5612068, 1), (1370270,3), (1581200,2), (1332670,2), (1150090,1),
            (1533340,3), (5050910,0), (2406530,0), (1243650,2), (1158500,1), (2759530,0),
            (1689970,3), (5257597,0), (1446210, 2), (2416380,1), (1244220,1),
            (1578010,2)
        ];

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
            _lookupsAlt = _lookups.GetAlternateLookup<ReadOnlySpan<char>>();
            _wordFrequencyRanks = runtime.WordFrequencyRanks;
            WordFrequencyPriors.Current = runtime.WordObservedFrequencies;
            _nameOnlyWordIds = runtime.NameOnlyWordIds;
            _expressionWordIds = runtime.ExpressionWordIds;
            _kanjiBackedWordIds = runtime.KanjiBackedWordIds;
            WordMeta = runtime.WordMeta;
            if (_compoundHashSet == null)
                BuildCompoundHashSet();
        }

        private static void BuildCompoundHashSet()
        {
            var set = new HashSet<long>(_lookups.Count);
            int maxLen = 0;
            foreach (var key in _lookups.Keys)
            {
                set.Add(HiraRollingHash(key));
                if (key.Length > maxLen) maxLen = key.Length;
            }
            _compoundHashSet = set;
            _maxLookupKeyLength = maxLen;
            _hashBasePowers = new long[maxLen + 1];
            _hashBasePowers[0] = 1;
            for (int i = 1; i <= maxLen; i++)
                _hashBasePowers[i] = unchecked(_hashBasePowers[i - 1] * HASH_BASE);
        }

        private static long RollingHash(string s)
        {
            unchecked
            {
                long h = 0;
                for (int i = 0; i < s.Length; i++)
                    h = h * HASH_BASE + s[i];
                return h;
            }
        }

        private static long HiraRollingHash(string s)
        {
            unchecked
            {
                long h = 0;
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c >= 'ァ' && c <= 'ヶ') c = (char)(c - 0x60);
                    h = h * HASH_BASE + c;
                }
                return h;
            }
        }

        private static void PreprocessSentences(List<SentenceInfo> sentences,
                                                ParserDiagnostics? diagnostics = null,
                                                HashSet<string>? protectedSurfaces = null,
                                                BenchmarkTimings? timings = null)
        {
            Stopwatch? sw = timings != null ? Stopwatch.StartNew() : null;

            void DbgDump(string label)
            {
                if (Environment.GetEnvironmentVariable("JITEN_STAGE_DEBUG") is { Length: > 0 })
                    Console.WriteLine($"[prep] {label}: " + string.Join("|", sentences.SelectMany(s => s.Words).Select(w => w.word.Text)));
            }

            CleanSentenceTokens(sentences);
            DbgDump("CleanSentenceTokens");
            SplitSuruInflectionsForNounCompounding(sentences);
            SplitUnknownNounTokens(sentences, protectedSurfaces, diagnostics);
            DbgDump("SplitUnknownNounTokens");

            if (sw != null) { timings!.PrepOtherMs += sw.Elapsed.TotalMilliseconds; sw.Restart(); }

            CombineNounCompounds(sentences);
            DbgDump("CombineNounCompounds");

            if (sw != null) { timings!.PrepNounCompoundsMs += sw.Elapsed.TotalMilliseconds; sw.Restart(); }

            CombineCompounds(sentences);
            DbgDump("CombineCompounds");

            if (sw != null) { timings!.PrepCompoundsMs += sw.Elapsed.TotalMilliseconds; sw.Restart(); }

            CombineExpressions(sentences);
            DbgDump("CombineExpressions");

            CombineLexicalAdverbs(sentences);
            DbgDump("CombineLexicalAdverbs");

            if (sw != null) { timings!.PrepExpressionsMs += sw.Elapsed.TotalMilliseconds; sw.Restart(); }

            RepairLongVowels(sentences);
            DbgDump("RepairLongVowels");

            if (sw != null) { timings!.PrepOtherMs += sw.Elapsed.TotalMilliseconds; sw.Restart(); }

            FilterOrphanedMisparses(sentences, diagnostics);
            ValidateGrammaticalSequences(sentences, diagnostics);
            StripTrailingParticles(sentences);

            if (sw != null) { timings!.PrepGrammarMs += sw.Elapsed.TotalMilliseconds; sw.Restart(); }

            ResegmentationEngine.TryImproveUncertainSpans(sentences, _lookups, _wordFrequencyRanks, WordMeta, protectedSurfaces,
                                                          diagnostics);

            if (sw != null) { timings!.PrepResegmentationMs += sw.Elapsed.TotalMilliseconds; sw.Restart(); }

            MarkPersonNameHonorificContexts(sentences);

            if (sw != null) { timings!.PrepOtherMs += sw.Elapsed.TotalMilliseconds; }
        }

        private static readonly HashSet<string> ArchaicPosTypes =
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
            "汝", "なんじ", // archaic pronoun (literary/fantasy)
        ];

        private static bool IsClassicalSentence(
            IReadOnlyList<(WordInfo word, DeckWord? result, int? margin)> sentenceWords)
            => sentenceWords.Any(w => ClassicalMarkerSurfaces.Contains(w.word.Text));

        private static readonly HashSet<string> PersonHonorifics = ["さん", "ちゃん", "くん", "たん", "氏", "様", "殿", "先生", "課長", "部長"];

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

        private static bool IsNameSplitCandidate(WordInfo w) =>
            (PosMapper.IsNounForCompounding(w.PartOfSpeech) || w.PartOfSpeech == PartOfSpeech.Counter
             // 乃/之 in names (希里乃, 竜之介) come back as the archaic genitive particle
             || (w.PartOfSpeech == PartOfSpeech.Particle && w.Text is "乃" or "之"))
            && w.Text.Length > 0
            && (w.Text.All(JapaneseTextHelper.IsKanji) || WanaKana.IsKatakana(w.Text));

        /// <summary>
        /// Document-level rescue for split names: a name confirmed by an honorific anywhere in the
        /// document licenses re-joining its split occurrences elsewhere (七海さん once → every bare
        /// 七|海 pair becomes 七海). Pair+honorific occurrences (希里|乃|さん) confirm the joined
        /// surface directly when it has a JMDict name entry. Numeral-initial names (七海, 九条) are
        /// deliberately only merged with this evidence — without it the counter reading (二|条の光芒)
        /// must win.
        /// </summary>
        private static void MergeConfirmedNameSplits(List<List<SentenceInfo>> allTexts)
        {
            var confirmed = new HashSet<string>();

            foreach (var sentences in allTexts)
            foreach (var sentence in sentences)
            {
                var words = sentence.Words;
                for (int i = 0; i < words.Count; i++)
                {
                    if (words[i].word.IsPersonNameContext && words[i].word.Text.Length >= 2)
                        confirmed.Add(words[i].word.Text);

                    if (i + 2 >= words.Count || !PersonHonorifics.Contains(words[i + 2].word.Text))
                        continue;

                    var a = words[i].word;
                    var b = words[i + 1].word;
                    if (!IsNameSplitCandidate(a) || !IsNameSplitCandidate(b))
                        continue;

                    var joined = a.Text + b.Text;
                    if (joined.Length > 6)
                        continue;

                    if (_lookups.TryGetValue(joined, out var ids) && ids.Any(id => _nameOnlyWordIds.Contains(id)))
                        confirmed.Add(joined);
                }
            }

            if (confirmed.Count == 0)
                return;

            foreach (var sentences in allTexts)
            foreach (var sentence in sentences)
            {
                var words = sentence.Words;
                for (int i = 0; i < words.Count - 1; i++)
                {
                    var (a, posA, lenA) = words[i];
                    var (b, _, lenB) = words[i + 1];
                    if (!IsNameSplitCandidate(a) || !IsNameSplitCandidate(b))
                        continue;

                    var joined = a.Text + b.Text;
                    if (!confirmed.Contains(joined))
                        continue;

                    var mergedWord = new WordInfo(a)
                    {
                        Text = joined,
                        DictionaryForm = joined,
                        NormalizedForm = joined,
                        PartOfSpeech = PartOfSpeech.Noun,
                        Reading = a.Reading + b.Reading,
                        EndOffset = b.EndOffset,
                        IsPersonNameContext = true
                    };
                    words[i] = (mergedWord, posA, lenA + lenB);
                    words.RemoveAt(i + 1);
                }
            }
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

                        var prefix = ConcatTokenTexts(sentence.Words, start, nounCount);
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

        private static async Task<List<(DeckWord? word, int? margin, List<FormCandidate>? candidates)>> ProcessWordsInBatches(
            List<(WordInfo wordInfo, int occurrences)> words,
            Deconjugator deconjugator,
            int batchSize = 1000,
            ParserDiagnostics? diagnostics = null,
            Dictionary<string, DeckDictionaryEntry>? dictionaryEntriesBySurface = null)
        {
            List<(DeckWord? word, int? margin, List<FormCandidate>? candidates)> allProcessedWords = new();

            for (int i = 0; i < words.Count; i += batchSize)
            {
                var batch = words.GetRange(i, Math.Min(batchSize, words.Count - i));
                var batchWordCache = new ConcurrentDictionary<int, JmDictWord>();

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

                var processBatch = batch.Select(word => ProcessWord(word, deconjugator, prefetchedCache, diagnostics, batchWordCache)).ToList();
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
                {
                    var result = batchResults[j];
                    var surface = batch[j].wordInfo.Text;

                    if (dictionaryEntriesBySurface != null &&
                        dictionaryEntriesBySurface.TryGetValue(surface, out var dictEntry))
                    {
                        bool shouldOverride = !IsRiskyNameEntry(surface) || batch[j].wordInfo.IsPersonNameContext;
                        var overrideWord = shouldOverride ? dictEntry.EntryType switch
                        {
                            DeckDictionaryEntryType.Name =>
                                await ResolveAsNameEntry(surface, batch[j].occurrences, batchWordCache),
                            _ => null
                        } : null;

                        if (overrideWord != null)
                        {
                            allProcessedWords.Add((overrideWord, int.MaxValue, null));
                            continue;
                        }
                    }

                    allProcessedWords.Add((result.Word, result.Margin, result.FirstPassCandidates));
                }
            }

            return allProcessedWords;
        }

        private static async Task<DeckWord?> ResolveAsNameEntry(string surface, int occurrences,
                                                                 ConcurrentDictionary<int, JmDictWord>? batchWordCache)
        {
            if (!_lookups.TryGetValue(surface, out var candidates) || candidates.Count == 0)
                return null;

            var wordCache = await GetWordsWithCache(candidates, batchWordCache);

            foreach (var id in candidates)
            {
                if (!wordCache.TryGetValue(id, out var word)) continue;
                if (word.CachedPOS.All(p => p is PartOfSpeech.Name or PartOfSpeech.Unknown))
                    return new DeckWord
                    {
                        WordId = word.WordId, OriginalText = surface, ReadingIndex = 0,
                        Occurrences = occurrences, PartsOfSpeech = word.CachedPOS, Origin = word.Origin
                    };
            }

            return null;
        }

        public static async Task<List<DeckWord>> ParseText(IDbContextFactory<JitenDbContext> contextFactory, string text,
                                                           bool preserveStopToken = false,
                                                           ParserDiagnostics? diagnostics = null)
        {
            await EnsureInitializedAsync(contextFactory);

            var (cleanText, furiganaHints) = FuriganaHintExtractor.Extract(text);

            var parser = new MorphologicalAnalyser { HasCompoundLookup = HasLookupForCompound, HasNonNameCompoundLookup = HasNonNameLookup, HasPrioritizedNonNameCompoundLookup = HasPrioritizedNonNameLookup, HasKanaAppropriateCompoundLookup = HasKanaAppropriateLookup };
            var (sentences, cleanedOriginal) = await parser.ParseWithCleanedOriginal(cleanText, preserveStopToken: preserveStopToken, diagnostics: diagnostics);

            // ComputeTokenOffsets strips \r\n — relocate against the same coordinate space
            var cleanedOriginalFlat = cleanedOriginal.Replace("\r", "").Replace("\n", "");
            FuriganaHint[]? relocatedHints = furiganaHints.Length > 0 && cleanedOriginalFlat.Length > 0
                ? FuriganaHintExtractor.RelocateToCleanedOriginal(cleanedOriginalFlat, furiganaHints, cleanText)
                : null;

            PreprocessSentences(sentences, diagnostics);
            MergeConfirmedNameSplits([sentences]);
            PropagatePersonNameContexts(sentences);
            var wordInfos = ExtractWordInfos(sentences);
            var wordsWithOccurrences = wordInfos.Select(w => (w, 0)).ToList();

            var processedWithMargins = await ProcessWordsInBatches(wordsWithOccurrences, Deconjugator.Instance, diagnostics: diagnostics);

            var marginMap = BuildMarginMap(sentences, processedWithMargins);
            var candidateLookup = new Dictionary<(string, PartOfSpeech, string, string, bool, bool), List<FormCandidate>>();

            if (ResegmentationEngine.TryResegmentLowConfidenceTokens(sentences, _lookups, _wordFrequencyRanks, marginMap,
                                                                    WordMeta, diagnostics))
            {
                diagnostics?.Results.Clear();

                // Build lookup from old results so we can reuse them
                var oldResultLookup = new Dictionary<(string, PartOfSpeech, string, string, bool, bool), (DeckWord? word, int? margin)>();
                for (int i = 0; i < wordInfos.Count; i++)
                {
                    var key = GetDedupKey(wordInfos[i]);
                    oldResultLookup.TryAdd(key, (processedWithMargins[i].word, processedWithMargins[i].margin));
                    if (processedWithMargins[i].candidates != null)
                        candidateLookup.TryAdd(key, processedWithMargins[i].candidates!);
                }

                wordInfos = ExtractWordInfos(sentences);

                // Only process tokens not already resolved
                var newTokens = new List<(WordInfo wordInfo, int occurrences)>();
                foreach (var wi in wordInfos)
                {
                    var key = GetDedupKey(wi);
                    if (!oldResultLookup.ContainsKey(key))
                        newTokens.Add((wi, 0));
                }

                if (newTokens.Count > 0)
                {
                    var newResults = await ProcessWordsInBatches(newTokens, Deconjugator.Instance, diagnostics: diagnostics);
                    for (int i = 0; i < newTokens.Count; i++)
                    {
                        var key = GetDedupKey(newTokens[i].wordInfo);
                        oldResultLookup.TryAdd(key, (newResults[i].word, newResults[i].margin));
                        if (newResults[i].candidates != null)
                            candidateLookup.TryAdd(key, newResults[i].candidates!);
                    }
                }

                // Rebuild flat list aligned to new token stream
                processedWithMargins = wordInfos.Select(wi =>
                {
                    var key = GetDedupKey(wi);
                    if (oldResultLookup.TryGetValue(key, out var r))
                        return (r.word, r.margin, candidateLookup.GetValueOrDefault(key));
                    return ((DeckWord?)null, (int?)null, (List<FormCandidate>?)null);
                }).ToList();
            }
            else
            {
                for (int i = 0; i < wordInfos.Count; i++)
                {
                    if (processedWithMargins[i].candidates != null)
                    {
                        var key = GetDedupKey(wordInfos[i]);
                        candidateLookup.TryAdd(key, processedWithMargins[i].candidates!);
                    }
                }
            }

            var corrected = await ApplyAdjacentScoring(sentences, processedWithMargins, candidateLookup, diagnostics, relocatedHints);
            corrected = await ApplyMisparseGates(sentences, corrected, diagnostics);
            return ExcludeFinalMisparses(corrected, diagnostics);
        }

        /// <summary>
        /// Parses a single text into a Deck. Delegates to ParseTextsToDeck for a single codepath.
        /// </summary>
        public static async Task<Deck> ParseTextToDeck(IDbContextFactory<JitenDbContext> contextFactory, string text,
                                                       bool storeRawText = false,
                                                       bool predictDifficulty = true,
                                                       MediaType mediatype = MediaType.Novel,
                                                       ParserDiagnostics? diagnostics = null,
                                                       BenchmarkTimings? timings = null,
                                                       List<DeckDictionaryEntry>? dictionaryEntries = null)
        {
            var results = await ParseTextsToDeck(contextFactory, [text], storeRawText, predictDifficulty, mediatype, diagnostics, timings, dictionaryEntries);
            return results.Count > 0 ? results[0] : new Deck();
        }

        public static async Task<List<Deck>> ParseTextsToDeck(IDbContextFactory<JitenDbContext> contextFactory,
                                                              List<string> texts,
                                                              bool storeRawText = false,
                                                              bool predictDifficulty = true,
                                                              MediaType mediatype = MediaType.Novel,
                                                              ParserDiagnostics? diagnostics = null,
                                                              BenchmarkTimings? timings = null,
                                                              List<DeckDictionaryEntry>? dictionaryEntries = null)
        {
            if (texts.Count == 0) return [];

            await EnsureInitializedAsync(contextFactory);

            var userDictCsv = BuildUserDictCsv(dictionaryEntries);
            var protectedSurfaces = dictionaryEntries is { Count: > 0 }
                ? new HashSet<string>(dictionaryEntries.Select(e => e.Surface.Trim()))
                : null;
            var dictionaryEntriesBySurface = dictionaryEntries is { Count: > 0 }
                ? dictionaryEntries.ToDictionary(e => e.Surface.Trim())
                : null;

            var cleanTexts = new List<string>(texts.Count);
            var hintsByText = new List<FuriganaHint[]>(texts.Count);
            foreach (var t in texts)
            {
                var (clean, hints) = FuriganaHintExtractor.Extract(t);
                cleanTexts.Add(clean);
                hintsByText.Add(hints);
            }

            var timer = new Stopwatch();
            timer.Start();

            // Batch morphological analysis
            var parser = new MorphologicalAnalyser { HasCompoundLookup = HasLookupForCompound, HasNonNameCompoundLookup = HasNonNameLookup, HasPrioritizedNonNameCompoundLookup = HasPrioritizedNonNameLookup, HasKanaAppropriateCompoundLookup = HasKanaAppropriateLookup };
            var cleanedOriginals = new List<string>();
            var rawCharCounts = new List<int>();
            var batchedSentences = await parser.ParseBatch(cleanTexts, diagnostics: diagnostics, timings: timings, userDictCsv: userDictCsv, cleanedOriginals: cleanedOriginals, rawContentCharCounts: rawCharCounts);

            timer.Restart();

            for (int textIndex = 0; textIndex < batchedSentences.Count; textIndex++)
                PreprocessSentences(batchedSentences[textIndex], diagnostics, protectedSurfaces, timings);

            MergeConfirmedNameSplits(batchedSentences);
            PropagatePersonNameContexts(batchedSentences);

            timer.Stop();
            if (timings != null) timings.PreprocessingMs = timer.Elapsed.TotalMilliseconds;

            // Pass 2: Process each preprocessed result through deconjugation/lookup pipeline
            var decks = new List<Deck>();
            Deconjugator deconjugator = Deconjugator.Instance;

            for (int textIndex = 0; textIndex < batchedSentences.Count; textIndex++)
            {
                var sentences = batchedSentences[textIndex];
                var text = texts[textIndex];

                var coFlat = cleanedOriginals[textIndex].Replace("\r", "").Replace("\n", "");
                FuriganaHint[]? relocated = hintsByText[textIndex].Length > 0 && coFlat.Length > 0
                    ? FuriganaHintExtractor.RelocateToCleanedOriginal(coFlat, hintsByText[textIndex], cleanTexts[textIndex])
                    : null;

                var deck = await ProcessSentencesToDeck(sentences, text, deconjugator, storeRawText, predictDifficulty, mediatype, timings, dictionaryEntriesBySurface, relocated, rawCharCounts[textIndex], diagnostics);
                decks.Add(deck);
                batchedSentences[textIndex] = null!;
            }

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
            MediaType mediatype,
            BenchmarkTimings? timings = null,
            Dictionary<string, DeckDictionaryEntry>? dictionaryEntriesBySurface = null,
            FuriganaHint[]? relocatedHints = null,
            int? rawContentCharCount = null,
            ParserDiagnostics? diagnostics = null)
        {
            var sw = timings != null ? Stopwatch.StartNew() : null;

            var wordInfos = ExtractWordInfos(sentences);
            var uniqueWords = CountUniqueWords(wordInfos);

            var allProcessedWithMargins = await ProcessWordsInBatches(uniqueWords, deconjugator, dictionaryEntriesBySurface: dictionaryEntriesBySurface);

            // Build lookup by direct 1:1 index mapping
            var resultLookup = new Dictionary<(string, PartOfSpeech, string, string, bool, bool), (DeckWord? word, int? margin)>();
            var candidateLookup = new Dictionary<(string, PartOfSpeech, string, string, bool, bool), List<FormCandidate>>();
            for (int i = 0; i < uniqueWords.Count; i++)
            {
                var key = GetDedupKey(uniqueWords[i].wordInfo);
                resultLookup.TryAdd(key, (allProcessedWithMargins[i].word, allProcessedWithMargins[i].margin));
                if (allProcessedWithMargins[i].candidates != null)
                    candidateLookup.TryAdd(key, allProcessedWithMargins[i].candidates!);
            }

            if (sw != null) { timings!.DeconjugationLookupMs += sw.Elapsed.TotalMilliseconds; sw.Restart(); }

            var marginMap = BuildMarginMapFromLookup(sentences, resultLookup);
            if (ResegmentationEngine.TryResegmentLowConfidenceTokens(sentences, _lookups, _wordFrequencyRanks, marginMap,
                                                                    WordMeta, diagnostics))
            {
                var newWordInfos = ExtractWordInfos(sentences);
                var newUniqueWords = new List<(WordInfo wordInfo, int occurrences)>();
                foreach (var wi in newWordInfos)
                {
                    var key = GetDedupKey(wi);
                    if (!resultLookup.ContainsKey(key))
                        newUniqueWords.Add((wi, 0));
                }

                if (newUniqueWords.Count > 0)
                {
                    var deduped = CountUniqueWords(newUniqueWords.Select(w => w.wordInfo).ToList());
                    var newResults = await ProcessWordsInBatches(deduped, deconjugator);
                    for (int i = 0; i < deduped.Count; i++)
                    {
                        var key = GetDedupKey(deduped[i].wordInfo);
                        resultLookup.TryAdd(key, (newResults[i].word, newResults[i].margin));
                        if (newResults[i].candidates != null)
                            candidateLookup.TryAdd(key, newResults[i].candidates!);
                    }
                }

                wordInfos = newWordInfos;
            }

            if (sw != null) { timings!.ResegmentationMs += sw.Elapsed.TotalMilliseconds; sw.Restart(); }

            var corrected = await ApplyAdjacentScoring(sentences, resultLookup, candidateLookup, relocatedHints: relocatedHints);
            corrected = await ApplyMisparseGates(sentences, corrected);

            if (sw != null) { timings!.AdjacentScoringMs += sw.Elapsed.TotalMilliseconds; sw.Restart(); }

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

            processedWords = ExcludeFinalMisparses(processedWords, diagnostics).ToArray();

            List<ExampleSentence>? exampleSentences = null;

            if (mediatype is MediaType.Novel or MediaType.NonFiction or MediaType.VideoGame or MediaType.VisualNovel or MediaType.WebNovel)
            {
                var wordIds = processedWords.Select(w => w.WordId).Distinct().ToList();
                await using var freqCtx = await _contextFactory.CreateDbContextAsync();
                var formFreqRanks = await freqCtx.WordFormFrequencies
                    .AsNoTracking()
                    .Where(wff => wordIds.Contains(wff.WordId))
                    .ToDictionaryAsync(
                        wff => (wff.WordId, (byte)wff.ReadingIndex),
                        wff => wff.FrequencyRank);

                exampleSentences = ExampleSentenceExtractor.ExtractSentences(
                    sentences, processedWords, formFreqRanks, _wordFrequencyRanks);
            }

            var totalWordCount = processedWords.Select(w => w.Occurrences).Sum();
            var characterCount = rawContentCharCount ?? wordInfos.Sum(x => x.Text.Length);

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
                           UniqueKanjiCount = wordInfos.SelectMany(w => w.Text).Distinct().Count(JapaneseTextHelper.IsKanji),
                           UniqueKanjiUsedOnceCount = wordInfos.SelectMany(w => w.Text).GroupBy(c => c)
                                                               .Count(g => g.Count() == 1 && JapaneseTextHelper.IsKanji(g.Key)),
                           SentenceCount = sentences.Count, DialoguePercentage = dialoguePercentage, DeckWords = processedWords,
                           RawText = storeRawText ? new DeckRawText(text) : null, ExampleSentences = exampleSentences
                       };

            if (sw != null) timings!.StatsBuildMs += sw.Elapsed.TotalMilliseconds;

            return deck;
        }

        public static async Task<List<DeckWord?>> ParseMorphenes(IDbContextFactory<JitenDbContext> contextFactory, string text,
                                                                 ParserDiagnostics? diagnostics = null)
        {
            await EnsureInitializedAsync(contextFactory);

            var parser = new MorphologicalAnalyser { HasCompoundLookup = HasLookupForCompound, HasNonNameCompoundLookup = HasNonNameLookup, HasPrioritizedNonNameCompoundLookup = HasPrioritizedNonNameLookup, HasKanaAppropriateCompoundLookup = HasKanaAppropriateLookup };
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

        private static async Task<Dictionary<int, JmDictWord>> GetWordsWithCache(
            IEnumerable<int> wordIds,
            ConcurrentDictionary<int, JmDictWord>? batchCache)
        {
            if (batchCache == null)
            {
                var words = await JmDictCache.GetWordsAsync(wordIds);
                PriorityOverrides.ApplyAll(words.Values);
                return words;
            }

            var distinct = wordIds.Distinct().ToList();
            var result = new Dictionary<int, JmDictWord>(distinct.Count);
            var needed = new List<int>();

            foreach (var id in distinct)
            {
                if (batchCache.TryGetValue(id, out var cached))
                    result[id] = cached;
                else
                    needed.Add(id);
            }

            if (needed.Count > 0)
            {
                var fetched = await JmDictCache.GetWordsAsync(needed);
                foreach (var (id, word) in fetched)
                {
                    PriorityOverrides.Apply(word);
                    result[id] = word;
                    batchCache.TryAdd(id, word);
                }
            }

            return result;
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
                                      int? margin = null, List<FormCandidate>? firstPassCandidates = null)
            {
                Status = status;
                Word = word;
                CacheKey = cacheKey;
                CacheWord = cacheWord;
                Margin = margin;
                FirstPassCandidates = firstPassCandidates;
            }

            public ProcessWordStatus Status { get; }
            public DeckWord? Word { get; }
            public DeckWordCacheKey? CacheKey { get; }
            public DeckWord? CacheWord { get; }
            public int? Margin { get; }
            public List<FormCandidate>? FirstPassCandidates { get; }

            public static ProcessWordResult FromResolved(DeckWord word, DeckWordCacheKey? cacheKey = null,
                                                         DeckWord? cacheWord = null, int? margin = null,
                                                         List<FormCandidate>? firstPassCandidates = null)
                => new(ProcessWordStatus.Resolved, word, cacheKey, cacheWord, margin, firstPassCandidates);

            public static ProcessWordResult FilteredOut { get; } = new(ProcessWordStatus.FilteredOut);
            public static ProcessWordResult TimedOut { get; } = new(ProcessWordStatus.TimedOut);
            public static ProcessWordResult Unresolved { get; } = new(ProcessWordStatus.Unresolved);
        }

        private static async Task<ProcessWordResult> ProcessWord((WordInfo wordInfo, int occurrences) wordData, Deconjugator deconjugator,
                                                                 Dictionary<DeckWordCacheKey, DeckWord?>? prefetchedCache = null,
                                                                 ParserDiagnostics? diagnostics = null,
                                                                 ConcurrentDictionary<int, JmDictWord>? batchWordCache = null)
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
                List<FormCandidate>? firstPassCandidates = null;

                // Captured before the escalation chain rewrites the POS — gates POS-relaxed lookups
                // for exclamations throughout this token's resolution.
                wordData.wordInfo.IsKanaExclamation =
                    wordData.wordInfo.PartOfSpeech is PartOfSpeech.Interjection or PartOfSpeech.Filler
                    && WanaKana.IsKana(wordData.wordInfo.Text);
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
                            var wordCache = await GetWordsWithCache([preMatchedWordId], batchWordCache);
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
                            var verbResult = await DeconjugateVerbOrAdjective(wordData, deconjugator, diagnostics, batchWordCache);
                            if (!verbResult.success || verbResult.word == null)
                            {
                                // The word might be a noun misparsed as a verb/adjective like お祭り
                                var nounResult = await DeconjugateWord(wordData, diagnostics, batchWordCache);
                                processedWord = nounResult.word;
                                resolvedMargin = nounResult.margin;
                                firstPassCandidates = nounResult.candidates;
                            }
                            else
                            {
                                processedWord = verbResult.word;
                                resolvedMargin = verbResult.margin;
                                firstPassCandidates = verbResult.candidates;
                            }
                        }
                        else
                        {
                            var nounResult = await DeconjugateWord(wordData, diagnostics, batchWordCache);
                            if (!nounResult.success || nounResult.word == null)
                            {
                                // The word might be a conjugated noun + suru
                                var verbResult = await DeconjugateVerbOrAdjective(wordData, deconjugator, diagnostics, batchWordCache);

                                var oldPos = wordData.wordInfo.PartOfSpeech;
                                // The word might be a verb or an adjective misparsed as a noun like らしく
                                if (!verbResult.success || verbResult.word == null)
                                {
                                    wordData.wordInfo.PartOfSpeech = PartOfSpeech.Verb;
                                    verbResult = await DeconjugateVerbOrAdjective(wordData, deconjugator, diagnostics, batchWordCache);
                                }

                                if (!verbResult.success || verbResult.word == null)
                                {
                                    wordData.wordInfo.PartOfSpeech = PartOfSpeech.IAdjective;
                                    verbResult = await DeconjugateVerbOrAdjective(wordData, deconjugator, diagnostics, batchWordCache);
                                }

                                if (!verbResult.success || verbResult.word == null)
                                {
                                    wordData.wordInfo.PartOfSpeech = PartOfSpeech.NaAdjective;
                                    // Prefer direct lookup for na-adjective stems that Sudachi may label as nouns/names (e.g., 朧気).
                                    var naDirect = await DeconjugateWord(wordData, diagnostics, batchWordCache);
                                    if (naDirect is { success: true, word: not null })
                                        verbResult = (true, naDirect.word, naDirect.margin, naDirect.candidates);
                                    else
                                        verbResult = await DeconjugateVerbOrAdjective(wordData, deconjugator, diagnostics, batchWordCache);
                                }

                                if (!verbResult.success || verbResult.word == null)
                                {
                                    // Interjections are frequently misclassified by Sudachi as proper-name nouns (e.g., おお…).
                                    // Try direct lookup as an interjection before giving up.
                                    wordData.wordInfo.PartOfSpeech = PartOfSpeech.Interjection;
                                    var interjectionDirect = await DeconjugateWord(wordData, diagnostics, batchWordCache);
                                    if (interjectionDirect is { success: true, word: not null })
                                        verbResult = (true, interjectionDirect.word, interjectionDirect.margin, interjectionDirect.candidates);
                                }

                                wordData.wordInfo.PartOfSpeech = oldPos;
                                processedWord = verbResult.word;
                                resolvedMargin = verbResult.margin;
                                firstPassCandidates = verbResult.candidates;
                            }
                            else if (wordData.wordInfo.PartOfSpeech is PartOfSpeech.Pronoun or PartOfSpeech.Conjunction
                                     or PartOfSpeech.Interjection or PartOfSpeech.Particle or PartOfSpeech.Adverb
                                     or PartOfSpeech.NaAdjective or PartOfSpeech.Suffix or PartOfSpeech.NounSuffix
                                     or PartOfSpeech.PrenounAdjectival)
                            {
                                processedWord = nounResult.word;
                                resolvedMargin = nounResult.margin;
                                firstPassCandidates = nounResult.candidates;
                            }
                            else
                            {
                                // Also try verb deconjugation: noun-tagged tokens may be verb stems
                                // (e.g., 抱え is both a rare noun "armful" and 連用形 of the common verb 抱える).
                                // Prefer the verb if it has strictly higher priority.
                                var savedPos = wordData.wordInfo.PartOfSpeech;
                                wordData.wordInfo.PartOfSpeech = PartOfSpeech.Verb;
                                var verbFallback = await DeconjugateVerbOrAdjective(wordData, deconjugator, diagnostics, batchWordCache);
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
                                        var ruWordCache = await GetWordsWithCache(ruIds.Distinct().ToList(), batchWordCache);
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
                                                }, (int?)null, (List<FormCandidate>?)null);
                                        }
                                    }
                                }

                                bool nounIsPureNameEntry = nounResult.word != null &&
                                                           nounResult.word.PartsOfSpeech.All(p => p is PartOfSpeech.Name
                                                                                                 or PartOfSpeech.Unknown);
                                if ((wordData.wordInfo.IsPersonNameContext || isNameLikeSudachiNoun) && nounIsPureNameEntry)
                                {
                                    processedWord = nounResult.word;
                                    resolvedMargin = nounResult.margin;
                                }
                                else if (verbFallback is { success: true, word: not null } && nounResult.word != null)
                                {
                                    var bothCache = await GetWordsWithCache(
                                                                                    [nounResult.word.WordId, verbFallback.word.WordId], batchWordCache);
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
                                            bool nounIsAdji = nounEntry.PartsOfSpeech.Contains("adj-i");
                                            if (nounIsAdji && ScoringPolicy.IsHighConfidence(nounResult.margin))
                                            {
                                                processedWord = nounResult.word;
                                                resolvedMargin = nounResult.margin;
                                            }
                                            else
                                            {
                                                processedWord = verbFallback.word;
                                                resolvedMargin = verbFallback.margin;
                                            }
                                        }
                                        else if (nounReadingMatch && verbReadingMatch && !verbExactReadingMatch)
                                        {
                                            // Verb only matches as a stem (e.g. できる's stem でき matches 出来).
                                            // Require a clear priority margin before overriding Sudachi's noun tag.
                                            // When form scoring already resolved the noun with high confidence (e.g. うえ → 上),
                                            // demand a much larger verb advantage to prevent spurious overrides (e.g. 飢える).
                                            int stemThreshold = ScoringPolicy.IsHighConfidence(nounResult.margin) ? 30 : 15;
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

                                if (firstPassCandidates == null && processedWord != null)
                                {
                                    firstPassCandidates = processedWord.WordId == nounResult.word?.WordId
                                        ? nounResult.candidates
                                        : verbFallback.candidates;
                                }
                            }
                        }

                        // Final safety net for exclamation tokens: whatever path produced the match
                        // (relaxed POS, escalation retries, DVOA dictionary-form fallback), an
                        // interjection must not resolve to a kanji-backed word through its reading key
                        // unless that word is directly attested for the surface (イエイ must never
                        // become 遺影; いえ → いえ "no" stays, あなた → 貴方's kana form stays).
                        if (processedWord != null && wordData.wordInfo.IsKanaExclamation
                            && !IsKanaAppropriateId(processedWord.WordId)
                            && !(_lookups.TryGetValue(wordData.wordInfo.Text, out var exclDirectIds)
                                 && exclDirectIds.Contains(processedWord.WordId)))
                        {
                            processedWord = null;
                            resolvedMargin = null;
                            firstPassCandidates = null;
                        }

                        // Same idea for matches the fallback text mutations invented on other pure-kana
                        // tokens: stripping ー/っ/small kana from ひゅーん until 庇陰's reading key bites
                        // is not a resolution. The original surface must attest the word, or the word
                        // must be one plausibly written in kana.
                        if (processedWord != null && !wordData.wordInfo.IsKanaExclamation
                            && wordData.wordInfo.Text != baseWord && WanaKana.IsKana(baseWord)
                            && !IsKanaAppropriateId(processedWord.WordId)
                            && !(_lookups.TryGetValue(baseWord, out var baseDirectIds)
                                 && baseDirectIds.Contains(processedWord.WordId)))
                        {
                            processedWord = null;
                            resolvedMargin = null;
                            firstPassCandidates = null;
                        }

                        if (processedWord != null)
                        {
                            if (wordData.wordInfo.Text != baseWord)
                                diagnostics?.LogParserEvent(
                                    "ProcessWord", "variant-resolved", [baseWord], [wordData.wordInfo.Text],
                                    $"resolved after fallback text mutation (WordId={processedWord.WordId})");

                            // Restore original text (before any stripping/modifications)
                            processedWord.OriginalText = baseWord;
                            break;
                        }

                        // We haven't found a match, let's try to remove the last character if it's a っ, a ー,
                        // an expressive small vowel (なんちゃってぇ) or a duplicate
                        if (wordData.wordInfo.Text.Length > 2 &&
                            (wordData.wordInfo.Text[^1] is 'っ' or 'ー' or 'ぁ' or 'ぃ' or 'ぅ' or 'ぇ' or 'ぉ'
                                 or 'ァ' or 'ィ' or 'ゥ' or 'ェ' or 'ォ' ||
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
                        // Sudachi fuses adv-to/adj-t words with their と particle (e.g. 凛と, 毅然と)
                        // but JMDict only has the base form — strip trailing と and retry
                        else if (wordData.wordInfo.PartOfSpeech == PartOfSpeech.Adverb &&
                                 wordData.wordInfo.Text.Length > 1 &&
                                 wordData.wordInfo.Text[^1] == 'と')
                        {
                            wordData.wordInfo.Text = wordData.wordInfo.Text[..^1];
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

                var candidatesToKeep = !ScoringPolicy.IsHighConfidence(resolvedMargin)
                    ? firstPassCandidates
                    : null;

                if (!UseCache)
                    return ProcessWordResult.FromResolved(processedWord, margin: resolvedMargin, firstPassCandidates: candidatesToKeep);

                var cacheWord = new DeckWord
                                {
                                    WordId = processedWord.WordId, OriginalText = processedWord.OriginalText,
                                    ReadingIndex = processedWord.ReadingIndex, Conjugations = processedWord.Conjugations,
                                    PartsOfSpeech = processedWord.PartsOfSpeech, Origin = processedWord.Origin,
                                    CachedMargin = resolvedMargin
                                };

                return ProcessWordResult.FromResolved(processedWord, cacheKey, cacheWord, resolvedMargin, candidatesToKeep);
            }
            finally
            {
                _processSemaphore.Release();
            }
        }

        // For mixed-script surfaces the kana tail is okurigana;
        // a Sudachi NormalizedForm that rewrites it into ADDITIONAL kanji is a different lexeme
        // (屈し must not become 屈指). Kanji-for-kanji variant normalization (敲き → 叩く) and
        // pure-kana surfaces (チックショー → 畜生) stay allowed.
        private static bool NormalizedFormIntroducesKanji(string text, string normalizedForm)
        {
            int surfaceKanji = 0, normalizedKanji = 0;
            foreach (var c in text)
                if (JapaneseTextHelper.IsKanji(c)) surfaceKanji++;
            if (surfaceKanji == 0)
                return false;

            foreach (var c in normalizedForm)
                if (JapaneseTextHelper.IsKanji(c)) normalizedKanji++;

            return normalizedKanji > surfaceKanji;
        }

        private static async Task<(bool success, DeckWord? word, int? margin, List<FormCandidate>? candidates)> DeconjugateWord(
            (WordInfo wordInfo, int occurrences) wordData,
            ParserDiagnostics? diagnostics = null,
            ConcurrentDictionary<int, JmDictWord>? batchWordCache = null)
        {
            string text = wordData.wordInfo.Text;

            // Exclude text that is primarily digits (with optional trailing ー) or single latin character
            var textWithoutBar = text.TrimEnd('ー');
            if ((textWithoutBar.Length > 0 && textWithoutBar.All(char.IsDigit)) || (text.Length == 1 && text.IsAsciiOrFullWidthLetter()))
            {
                return (false, null, null, null);
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
                // For pure-kana surfaces, candidates reachable only through long-vowel/kana rewrites
                // must be kana-appropriate words — otherwise いえー/イエーイ resolve to 遺影 via its
                // reading key. Kanji-containing surfaces are unaffected.
                Func<int, bool>? normalizedTierGate = WanaKana.IsKana(text) ? IsKanaAppropriateId : null;

                var collected = LookupCandidateCollector.CollectIds(_lookups, text,
                                                                    includeKanaNormalized: true, includeLongVowelStripped: true,
                                                                    normalizedTierGate: normalizedTierGate);

                // Also look up Sudachi's NormalizedForm when it differs from the surface (e.g., チックショー → チクショー for 畜生)
                if (!string.IsNullOrEmpty(wordData.wordInfo.NormalizedForm) &&
                    wordData.wordInfo.NormalizedForm != text &&
                    !NormalizedFormIntroducesKanji(text, wordData.wordInfo.NormalizedForm))
                {
                    var normalizedCollected = LookupCandidateCollector.CollectIds(_lookups, wordData.wordInfo.NormalizedForm,
                                                                                  includeKanaNormalized: true,
                                                                                  includeLongVowelStripped: true,
                                                                                  normalizedTierGate: normalizedTierGate);
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
                    wordCache = await GetWordsWithCache(candidates, batchWordCache);
                }
                catch (Exception ex)
                {
                    // If we hit an exception when retrieving from cache, return a failure
                    // but don't crash the entire process
                    Console.WriteLine($"Error retrieving word cache: {ex.Message}");
                    return (false, null, null, null);
                }

                // Early return if we got no words from cache to avoid NullReferenceException
                if (wordCache.Count == 0)
                {
                    return (false, null, null, null);
                }

                // A pure-katakana surface with a direct katakana lookup hit must not
                // resolve through the hiragana fold to a word attested only in kanji/hiragana
                // (カモン must not become 家紋, フル must not become 降る). Words with any katakana
                // form stay in the pool (懐炉/カイロ), so gairaigo homographs still compete on scoring.
                if (wordData.wordInfo.PreMatchedCandidateWordIds is not { Count: > 0 }
                    && KanaScoringHelpers.IsPureKatakanaToken(text)
                    && _lookups.TryGetValue(text, out var directKatakanaIds))
                {
                    bool hasUsableDirectCandidate = directKatakanaIds.Any(id =>
                        wordCache.TryGetValue(id, out var w)
                        && w.CachedPOS.Any(p => p is not (PartOfSpeech.Name or PartOfSpeech.Unknown)));

                    if (hasUsableDirectCandidate)
                    {
                        var directIdSet = new HashSet<int>(directKatakanaIds);
                        candidates = candidates.Where(id =>
                            directIdSet.Contains(id)
                            || (wordCache.TryGetValue(id, out var w)
                                && w.Forms.Any(f => KanaScoringHelpers.ContainsKatakana(f.Text)))).ToList();
                    }
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
                                                                              word.CachedPOS,
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
                    // Exception: a pure-kana interjection/filler relaxing into a kanji-backed word
                    // is a reading-key collision, not a real match (イエーイ/イエイ → 遺影).
                    bool requireKanaAppropriate = wordData.wordInfo.IsKanaExclamation;
                    foreach (var id in candidates)
                    {
                        if (!wordCache.TryGetValue(id, out var word)) continue;
                        // Exact-surface forms are real written forms, not collisions — keep them even
                        // when not kana-appropriate (あなた → 貴方's kana form, ファルマ → name entry).
                        if (requireKanaAppropriate && !IsKanaAppropriateId(id) &&
                            !word.Forms.Any(f => f.Text == text))
                            continue;
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
                    bool surfaceIsHiragana = WanaKana.IsHiragana(text);
                    foreach (var id in candidates)
                    {
                        if (!wordCache.TryGetValue(id, out var word))
                            continue;

                        // A pure-hiragana surface reaching a name entry only through the katakana fold
                        // is onomatopoeia/colloquial speech, not a name (ひゅーん must not become ヒューン).
                        // Names genuinely written in hiragana keep an exact-surface form and stay eligible.
                        if (surfaceIsHiragana &&
                            word.CachedPOS.All(p => p is PartOfSpeech.Name or PartOfSpeech.Unknown) &&
                            !word.Forms.Any(f => f.Text == text))
                            continue;

                        lastResortCandidates.Add(word);
                    }

                    if (lastResortCandidates.Count > 0)
                    {
                        candidatePool = lastResortCandidates;
                        isNameContext = true;
                    }
                    else
                    {
                        return (false, null, null, null);
                    }
                }

                // A token that still carries Suffix POS here is attached to a preceding noun
                // (ReclassifyOrphanedSuffixes converts all other suffixes to CommonNoun).
                // Pure-suffix entries (め=奴, ら, ども) are then the right reading; noun/suffix
                // hybrids like 目 win on raw frequency but are misparses in that position.
                if (wordData.wordInfo.PartOfSpeech == PartOfSpeech.Suffix && !isNameContext)
                {
                    var pureSuffixes = candidatePool.FindAll(w =>
                        (w.CachedPOSMask & (PosMask.Bit(PartOfSpeech.Suffix) | PosMask.Bit(PartOfSpeech.NounSuffix))) != 0 &&
                        (w.CachedPOSMask & PosMask.NounLike) == 0);
                    if (pureSuffixes.Count > 0)
                        candidatePool = pureSuffixes;
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
                                                               sudachiReading: wordData.wordInfo.Reading,
                                                               sudachiPOS: wordData.wordInfo.PartOfSpeech,
                                                               isSudachiPossibleDependant: wordData.wordInfo.HasPartOfSpeechSection(PartOfSpeechSection.PossibleDependant));

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
                    return (false, null, null, null);

                DeckWord deckWord = new()
                                    {
                                        WordId = bestPair.Word.WordId, OriginalText = wordData.wordInfo.Text,
                                        ReadingIndex = bestPair.ReadingIndex, Occurrences = wordData.occurrences,
                                        PartsOfSpeech = bestPair.Word.CachedPOS, Origin = bestPair.Word.Origin
                                    };
                return (true, deckWord, margin, allFormCandidates);
            }

            return (false, null, null, null);
        }

        private static async Task<(bool success, DeckWord? word, int? margin, List<FormCandidate>? candidates)> DeconjugateVerbOrAdjective(
            (WordInfo wordInfo, int occurrences) wordData, Deconjugator deconjugator,
            ParserDiagnostics? diagnostics = null,
            ConcurrentDictionary<int, JmDictWord>? batchWordCache = null)
        {
            // Early check for digits before WanaKana (which can't convert full-width digits)
            var textWithoutBar = wordData.wordInfo.Text.TrimEnd('ー');
            if (textWithoutBar.Length > 0 && textWithoutBar.All(char.IsDigit) ||
                (textWithoutBar.Length == 1 && textWithoutBar.IsAsciiOrFullWidthLetter()))
            {
                return (false, null, null, null);
            }

            var normalizedText = KanaNormalizer.Normalize(KanaConverter.ToHiragana(wordData.wordInfo.Text));

            // Exclude single latin character
            if (normalizedText.Length == 1 && normalizedText.IsAsciiOrFullWidthLetter())
            {
                return (false, null, null, null);
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
                if (dictFormLookupIds is not { Count: > 0 } && !string.IsNullOrEmpty(wordData.wordInfo.NormalizedForm)
                    && !NormalizedFormIntroducesKanji(wordData.wordInfo.Text, wordData.wordInfo.NormalizedForm))
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
                return (false, null, null, null);

            Dictionary<int, JmDictWord> wordCache;
            try
            {
                wordCache = await GetWordsWithCache(allCandidateIds, batchWordCache);

                // Check if we got any results
                if (wordCache.Count == 0)
                {
                    return (false, null, null, null);
                }
            }
            catch (Exception ex)
            {
                // If we hit an exception when retrieving from cache, return a failure
                // but don't crash the entire process
                Console.WriteLine($"Error retrieving verb/adjective word cache: {ex.Message}");
                return (false, null, null, null);
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
                if (!string.IsNullOrEmpty(wordData.wordInfo.DictionaryForm))
                {
                    await TryFallbackLookup(wordData.wordInfo.DictionaryForm.Replace("ゎ", "わ").Replace("ヮ", "わ"),
                        wordData, deconjugated, matches, batchWordCache);
                }

                if (matches.Count == 0 && !string.IsNullOrEmpty(wordData.wordInfo.NormalizedForm) &&
                    wordData.wordInfo.NormalizedForm != wordData.wordInfo.DictionaryForm &&
                    !NormalizedFormIntroducesKanji(wordData.wordInfo.Text, wordData.wordInfo.NormalizedForm))
                {
                    await TryFallbackLookup(wordData.wordInfo.NormalizedForm,
                        wordData, deconjugated, matches, batchWordCache, baseDictionaryWord);
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
                    return (false, null, null, null);
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
            // When CombineInflections merged multiple Sudachi tokens (e.g. い+ない→いない) and
            // deconjugation confirms the DictionaryForm (いない→いる matching 居る), the evidence
            // is very strong — exclude POS-incompatible direct-surface candidates (e.g. 以内)
            // entirely. Only for merged tokens: single-token ambiguity (e.g. いい as adj vs verb
            // いう) should still let the direct-surface candidate compete.
            bool hasMergeConfirmedDeconj = wordData.wordInfo.IsMergedInflection &&
                matches.Any(m => m.form.Text == baseDictionaryWord && m.form.Process.Count > 0);
            foreach (var id in directSurfaceIds)
            {
                if (matchedWordIds.Contains(id)) continue;
                if (filteredSuruNounIds != null && filteredSuruNounIds.Contains(id)) continue;
                if (!wordCache.TryGetValue(id, out var directWord)) continue;
                var posList = directWord.CachedPOS;
                if (!posList.Any(p => p is not (PartOfSpeech.Name or PartOfSpeech.Unknown))) continue;
                bool isPosIncompat = matches.Count > 0 &&
                                     !PosMapper.IsJmDictCompatibleWithSudachi(directWord.CachedPOS, wordData.wordInfo.PartOfSpeech);
                if (isPosIncompat && hasMergeConfirmedDeconj)
                    continue;
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
                                                           sudachiReading: wordData.wordInfo.Reading,
                                                           sudachiPOS: wordData.wordInfo.PartOfSpeech,
                                                           isSudachiPossibleDependant: wordData.wordInfo.HasPartOfSpeechSection(PartOfSpeechSection.PossibleDependant));

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
                    margin = ScoringPolicy.HighConfidenceThreshold;
                }
            }

            if (bestPair == null)
                return (false, null, null, null);

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

            return (true, deckWord, margin, allFormCandidates);
        }

        private static async Task TryFallbackLookup(
            string formText,
            (WordInfo wordInfo, int occurrences) wordData,
            List<DeconjugationForm> deconjugated,
            List<(JmDictWord word, DeconjugationForm form)> matches,
            ConcurrentDictionary<int, JmDictWord>? batchWordCache,
            string? extraProcessPrefix = null)
        {
            var formHiragana = KanaConverter.ToHiragana(formText, convertLongVowelMark: false);
            if (!_lookups.TryGetValue(formHiragana, out List<int>? lookupIds) &&
                !_lookups.TryGetValue(formText, out lookupIds))
                return;
            if (lookupIds is not { Count: > 0 })
                return;

            try
            {
                var wordCache = await GetWordsWithCache(lookupIds, batchWordCache);
                var recoveredProcess = deconjugated
                    .Where(d => d.Process.Count > 0 &&
                                (d.Text.StartsWith(formHiragana) ||
                                 (extraProcessPrefix != null && d.Text.StartsWith(extraProcessPrefix))))
                    .MinBy(d => d.Text.Length)?.Process
                    ?.Where(p => !string.IsNullOrEmpty(p)).ToList() ?? [];
                foreach (var word in wordCache.Values)
                {
                    if (word.CachedPOS.Contains(wordData.wordInfo.PartOfSpeech))
                    {
                        var form = new DeconjugationForm(formHiragana, wordData.wordInfo.Text,
                            new List<string>(), new HashSet<string>(), recoveredProcess);
                        matches.Add((word, form));
                    }
                }
            }
            catch
            {
            }
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
                PriorityOverrides.ApplyAll(wordCache.Values);

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

        private static void CombineCompounds(List<SentenceInfo> sentences)
        {
            foreach (var sentence in sentences)
            {
                if (sentence.Words.Count < 2)
                    continue;

                var words = sentence.Words;
                var wordInfos = new List<WordInfo>(words.Count);
                for (int w = 0; w < words.Count; w++)
                    wordInfos.Add(words[w].word);

                var tokenHashes = new long[wordInfos.Count];
                for (int w = 0; w < wordInfos.Count; w++)
                    tokenHashes[w] = HiraRollingHash(wordInfos[w].Text);

                var result = new List<(WordInfo word, int position, int length)>(sentence.Words.Count);

                for (int i = wordInfos.Count - 1; i >= 0; i--)
                {
                    var word = wordInfos[i];

                    if (word.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective or PartOfSpeech.Expression or PartOfSpeech.Suffix
                        or PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Name)
                    {
                        bool nounTrigger = word.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Name;
                        var match = TryMatchCompounds(wordInfos, i, tokenHashes, forceExpressionOnly: nounTrigger);
                        if (match.HasValue)
                        {
                            var (startIndex, dictForm, wordId) = match.Value;

                            var startPosition = sentence.Words[startIndex].position;
                            int combinedLength = 0;
                            for (int j = startIndex; j <= i; j++)
                            {
                                combinedLength += sentence.Words[j].length;
                            }

                            var originalText = ConcatWordInfoTexts(wordInfos, startIndex, i - startIndex + 1);
                            var combinedReading = ConcatWordInfoReadings(wordInfos, startIndex, i - startIndex + 1);

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

        // X+に/X+でも pairs lexicalized as adverbs in JMDict whose X is not a strong standalone
        // word. Hyper-common standalone nouns stay split even when a combined entry exists
        // (本当に, 絶対に, 外に, ように) — that's the project segmentation convention — so these
        // are curated rather than derived from entry existence.
        private static readonly HashSet<string> LexicalizedAdverbPairs = ["フルに", "無性に", "意地でも"];

        /// Merges X+に into a single token when the combined surface is a lexicalized adverb:
        /// any X的+に (always adverbial, JMDict entry required) or a curated pair (フルに, 無性に,
        /// 意地でも). Bare entry existence is deliberately NOT enough — それに/ことに/本当に would
        /// merge over genuine case-particle uses.
        private static void CombineLexicalAdverbs(List<SentenceInfo> sentences)
        {
            foreach (var sentence in sentences)
            {
                var words = sentence.Words;
                if (words.Count < 2)
                    continue;

                var result = new List<(WordInfo word, int position, int length)>(words.Count);
                bool changed = false;

                for (int i = 0; i < words.Count; i++)
                {
                    var (word, _, _) = words[i];

                    if (i + 1 < words.Count
                        && word.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                            or PartOfSpeech.NaAdjective or PartOfSpeech.NominalAdjective)
                    {
                        var next = words[i + 1];
                        bool isNiPair = next.word.Text == "に"
                                        && next.word.PartOfSpeech is PartOfSpeech.Particle or PartOfSpeech.Auxiliary
                                        && word.Text.EndsWith('的');
                        bool isCuratedPair = next.word.Text is "に" or "でも"
                                             && LexicalizedAdverbPairs.Contains(word.Text + next.word.Text);

                        if ((isNiPair || isCuratedPair) && HasLexicalAdverbEntry(word.Text + next.word.Text))
                        {
                            var combinedText = word.Text + next.word.Text;
                            var merged = new WordInfo(word)
                            {
                                Text = combinedText,
                                DictionaryForm = combinedText,
                                NormalizedForm = combinedText,
                                Reading = word.Reading + next.word.Reading,
                                PartOfSpeech = PartOfSpeech.Adverb,
                                EndOffset = next.word.EndOffset,
                            };

                            result.Add((merged, words[i].position, words[i].length + next.length));
                            changed = true;
                            i++;
                            continue;
                        }
                    }

                    result.Add(words[i]);
                }

                if (changed)
                    sentence.Words = result;
            }
        }

        private static bool HasLexicalAdverbEntry(string text)
        {
            if (HasAdverbIds(text))
                return true;
            try
            {
                var hira = KanaConverter.ToNormalizedHiragana(text);
                return hira != text && HasAdverbIds(hira);
            }
            catch
            {
                return false;
            }

            static bool HasAdverbIds(string key)
            {
                if (!_lookups.TryGetValue(key, out var ids)) return false;
                foreach (var id in ids)
                {
                    if (_nameOnlyWordIds.Contains(id)) continue;
                    if (WordMeta.TryGetValue(id, out var meta) && meta.Pos.Contains(PartOfSpeech.Adverb))
                        return true;
                }

                return false;
            }
        }

        private static void RepairLongVowels(List<SentenceInfo> sentences)
        {
            foreach (var sentence in sentences)
            {
                var words = sentence.Words;
                if (words.Count == 0)
                    continue;

                var result = new List<(WordInfo word, int position, int length)>(words.Count);
                int i = words.Count - 1;

                while (i >= 0)
                {
                    var word = words[i].word;

                    // --- Standalone ー tokens ---
                    if (word.Text == "ー" && word.PartOfSpeech == PartOfSpeech.SupplementarySymbol)
                    {
                        if (i == 0)
                        {
                            i--;
                            continue;
                        }

                        // Detached verb ending + ー: Sudachi splits e.g. あげる into あげ+る+ー
                        if (i >= 2)
                        {
                            var detached = words[i - 1].word;
                            var verbBefore = words[i - 2].word;
                            if (detached.Text is "る" or "す"
                                && detached.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.Verb or PartOfSpeech.Auxiliary
                                && verbBefore.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.Auxiliary)
                            {
                                verbBefore.Text += detached.Text;
                                int cLen = words[i - 2].length + words[i - 1].length;
                                result.Add((verbBefore, words[i - 2].position, cLen));
                                i -= 3;
                                continue;
                            }
                        }

                        bool found = false;
                        for (int combineCount = Math.Min(4, i); combineCount >= 1; combineCount--)
                        {
                            var combined = ConcatTokenTexts(words, i - combineCount, combineCount);

                            var withBar = combined + "ー";
                            string? withBarHiragana;
                            try { withBarHiragana = KanaConverter.ToHiragana(withBar); }
                            catch { withBarHiragana = null; }

                            if (withBarHiragana != null &&
                                _lookups.TryGetValue(withBarHiragana, out var idsWithBar) && idsWithBar.Count > 0)
                            {
                                var first = words[i - combineCount];
                                first.word.Text = withBar;
                                int cLen = 0;
                                for (int j = i - combineCount; j <= i; j++)
                                    cLen += words[j].length;
                                result.Add((first.word, first.position, cLen));
                                i -= combineCount + 1;
                                found = true;
                                break;
                            }

                            string? withoutBarHiragana;
                            try { withoutBarHiragana = KanaConverter.ToHiragana(combined); }
                            catch { withoutBarHiragana = null; }

                            if (withoutBarHiragana != null &&
                                _lookups.TryGetValue(withoutBarHiragana, out var idsWithout) && idsWithout.Count > 0)
                            {
                                var first = words[i - combineCount];
                                first.word.Text = combined;
                                int cLen = 0;
                                for (int j = i - combineCount; j < i; j++)
                                    cLen += words[j].length;
                                result.Add((first.word, first.position, cLen));
                                i -= combineCount + 1;
                                found = true;
                                break;
                            }
                        }

                        if (!found)
                        {
                            var prev = words[i - 1];
                            if (!prev.word.Text.EndsWith("ー"))
                            {
                                prev.word.Text += "ー";
                                words[i - 1] = (prev.word, prev.position, prev.length + words[i].length);
                            }
                            i--;
                        }

                        continue;
                    }

                    // --- Tokens ending with ー (over-segmented compounds) ---
                    if (word.Text.EndsWith("ー") && word.Text != "ー")
                    {
                        var withoutBar = word.Text.Replace("ー", "");
                        if (withoutBar.Length > 2 && WanaKana.IsKatakana(withoutBar))
                        {
                            result.Add(words[i]);
                            i--;
                            continue;
                        }

                        int maxBackward = Math.Min(5, i);
                        bool merged = false;

                        for (int k = maxBackward; k >= 1; k--)
                        {
                            bool hasParticle = false;
                            for (int j = i - k; j < i; j++)
                            {
                                var w = words[j].word;
                                if (w.PartOfSpeech == PartOfSpeech.Particle && w.DictionaryForm == w.Text)
                                {
                                    hasParticle = true;
                                    break;
                                }
                            }
                            if (hasParticle) continue;

                            var candidateSurface = ConcatTokenTexts(words, i - k, k + 1);
                            candidateSurface = MultiLongVowelRegex.Replace(candidateSurface, "ー");

                            if (TryLongVowelLookup(candidateSurface, useKanaNormalizer: false))
                            {
                                var first = words[i - k];
                                first.word.Text = candidateSurface;
                                int cLen = 0;
                                for (int j = i - k; j <= i; j++) cLen += words[j].length;
                                result.Add((first.word, first.position, cLen));
                                i -= k + 1;
                                merged = true;
                                break;
                            }

                            var candidateKey = candidateSurface.TrimEnd('ー');
                            if (candidateKey.Length > 0 && TryLongVowelLookup(candidateKey))
                            {
                                var first = words[i - k];
                                first.word.Text = candidateSurface;
                                int cLen = 0;
                                for (int j = i - k; j <= i; j++) cLen += words[j].length;
                                result.Add((first.word, first.position, cLen));
                                i -= k + 1;
                                merged = true;
                                break;
                            }

                        }

                        if (!merged && i > 0 && word.Text is "るー" or "すー")
                        {
                            var prev = words[i - 1];
                            var kana = word.Text[0].ToString();
                            var combined = prev.word.Text + kana;

                            bool shouldMerge =
                                TryLongVowelLookup(combined) || TryDeconjugatedLongVowelLookup(combined) ||
                                (word.Text == "るー" && prev.word.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.Auxiliary) ||
                                (word.Text == "すー" && prev.word.Text.EndsWith("で"));

                            if (shouldMerge)
                            {
                                prev.word.Text = combined;
                                int cLen = words[i - 1].length + words[i].length;
                                result.Add((prev.word, prev.position, cLen));
                                i -= 2;
                                merged = true;
                            }
                        }

                        if (!merged)
                        {
                            if (!TryLongVowelLookup(word.Text, useKanaNormalizer: false))
                            {
                                var singleKey = word.Text.TrimEnd('ー');

                                if (!(singleKey.Length == 2
                                      && word.PartOfSpeech is not PartOfSpeech.Auxiliary and not PartOfSpeech.Particle and not PartOfSpeech.Verb
                                      && !(word.PartOfSpeech == PartOfSpeech.Noun && !word.DictionaryForm.Contains('ー'))
                                      && TryResolveAdjStem(word, singleKey)) &&
                                    !TryLongVowelLookup(word.Text))
                                {
                                    if (singleKey.Length > 0 &&
                                        (TryLongVowelLookup(singleKey) || TryDeconjugatedLongVowelLookup(singleKey)))
                                    {
                                        if (string.IsNullOrEmpty(word.DictionaryForm) ||
                                            word.DictionaryForm == word.Text ||
                                            !TryLongVowelLookup(word.DictionaryForm))
                                        {
                                            word.Text = singleKey;
                                        }
                                    }
                                }
                            }

                            result.Add(words[i]);
                            i--;
                        }

                        continue;
                    }

                    // --- Normal token ---
                    result.Add(words[i]);
                    i--;
                }

                result.Reverse();
                sentence.Words = result;
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
                if (form.Tags.Any(t => t == "adj-i") && form.Text == hira + "い" && HasIAdjectiveLookup(form.Text))
                {
                    word.Text = stem + "い";
                    word.DictionaryForm = stem + "い";
                    word.PartOfSpeech = PartOfSpeech.IAdjective;
                    return true;
                }
            }

            return false;
        }

        /// The slang-elongation rescue (ヤバー→ヤバい) claims the token is an i-adjective, so the
        /// lookup target must actually be one — a bare existence check lets noun reading-keys
        /// hijack interjections (いえー → いえい → 遺影).
        private static bool HasIAdjectiveLookup(string text)
        {
            if (!_lookups.TryGetValue(text, out var ids)) return false;
            foreach (var id in ids)
                if (WordMeta.TryGetValue(id, out var meta) && meta.Pos.Contains(PartOfSpeech.IAdjective))
                    return true;
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


        private static void FilterOrphanedMisparses(List<SentenceInfo> sentences, ParserDiagnostics? diagnostics = null)
        {
            foreach (var sentence in sentences)
            {
                var words = sentence.Words;
                bool anyFiltered = false;
                var result = new List<(WordInfo word, int position, int length)>(words.Count);

                for (int i = words.Count - 1; i >= 0; i--)
                {
                    var word = words[i].word;

                    bool nextIsLongVowel = result.Count > 0 && result[^1].word.Text == "ー";

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
                        // Join-rescue before deleting: the shard may be the first mora of a real word
                        // that Sudachi cut off (１つ|ま|ねた → ま+ねた = 真似た). Joining wins when the
                        // combined surface deconjugates to a kana-appropriate verb/adjective.
                        if (result.Count > 0 && TryJoinShredWithFollowing(word, result[^1].word, out var joinedDictForm))
                        {
                            var following = result[^1];
                            var joined = new WordInfo(following.word)
                            {
                                Text = word.Text + following.word.Text,
                                DictionaryForm = joinedDictForm,
                                NormalizedForm = joinedDictForm,
                                Reading = word.Reading + following.word.Reading,
                                PartOfSpeech = PartOfSpeech.Verb,
                                StartOffset = word.StartOffset >= 0 ? word.StartOffset : following.word.StartOffset,
                            };
                            result[^1] = (joined, words[i].position, words[i].length + following.length);
                            diagnostics?.LogParserEvent(
                                "FilterOrphanedMisparses", "join", [word.Text, following.word.Text],
                                [joined.Text], $"shred joined to following token (dict form {joinedDictForm})");
                            anyFiltered = true;
                            continue;
                        }

                        diagnostics?.LogParserEvent(
                            "FilterOrphanedMisparses", "remove", [word.Text], null,
                            shouldFilter ? "in MisparsesRemove list" : "single-kana stutter / noise noun");
                        anyFiltered = true;
                        continue;
                    }

                    result.Add(words[i]);
                }

                if (anyFiltered)
                {
                    result.Reverse();
                    sentence.Words = result;
                }
            }
        }

        /// A dropped kana shard + its following kana token form a rescuable word when the joined
        /// surface deconjugates (with at least one real conjugation step) to a kana-appropriate
        /// verb or adjective in the lookups (まねた → 真似る uk). Nouns are excluded — joining into
        /// bare noun reading-keys would recreate the collision class this gate exists to prevent.
        private static bool TryJoinShredWithFollowing(WordInfo shred, WordInfo following, out string dictForm)
        {
            dictForm = "";

            if (!WanaKana.IsKana(shred.Text) || following.Text.Length == 0 || !WanaKana.IsKana(following.Text))
                return false;
            if (following.PartOfSpeech is PartOfSpeech.SupplementarySymbol or PartOfSpeech.Symbol
                or PartOfSpeech.Particle or PartOfSpeech.Auxiliary or PartOfSpeech.BlankSpace)
                return false;

            string joined;
            try
            {
                joined = KanaConverter.ToHiragana(shred.Text + following.Text, convertLongVowelMark: false);
            }
            catch
            {
                return false;
            }

            foreach (var form in Deconjugator.Instance.Deconjugate(joined))
            {
                if (form.Process.Count == 0 || form.Tags.Count == 0) continue;
                var lastTag = form.Tags[^1];
                if (!lastTag.StartsWith('v') && lastTag != "adj-i") continue;
                if (!_lookups.TryGetValue(form.Text, out var ids)) continue;
                // The conjugation-step requirement above is already a strong structural signal,
                // so common (prioritized) verbs qualify alongside usually-kana ones (まねた →
                // 真似る is ichi1 but not uk).
                if (!ids.Any(id => IsKanaAppropriateId(id)
                                   || (WordMeta.TryGetValue(id, out var meta)
                                       && !meta.IsTrueName && meta.GetPriorityScore(true) > 0)))
                    continue;

                dictForm = form.Text;
                return true;
            }

            return false;
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
        private static void SplitUnknownNounTokens(List<SentenceInfo> sentences, HashSet<string>? protectedSurfaces = null,
                                                   ParserDiagnostics? diagnostics = null)
        {
            foreach (var sentence in sentences)
            {
                if (sentence.Words.Count == 0)
                    continue;

                bool anySplit = false;
                var result = new List<(WordInfo word, int position, int length)>(sentence.Words.Count);

                foreach (var (word, position, length) in sentence.Words)
                {
                    if (protectedSurfaces != null && protectedSurfaces.Contains(word.Text))
                    {
                        result.Add((word, position, length));
                        continue;
                    }

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
                            diagnostics?.LogParserEvent(
                                "SplitUnknownNounTokens", "split", [word.Text], [baseText, suffixText],
                                "的-suffix split: full form has no lookup, base does");
                            result.Add((baseWord, position, baseText.Length));
                            result.Add((suffixWord, position + baseText.Length, suffixLen));
                            anySplit = true;
                            continue;
                        }
                    }

                    if (word.Text.Length >= 2 &&
                        PosMapper.IsNounForCompounding(word.PartOfSpeech) &&
                        word.Text.All(JapaneseTextHelper.IsKanji) &&
                        !(_lookups.TryGetValue(word.Text, out var ids) && ids.Count > 0))
                    {
                        diagnostics?.LogParserEvent(
                            "SplitUnknownNounTokens", "split", [word.Text],
                            word.Text.Select(c => c.ToString()).ToArray(),
                            "kanji-only noun without lookup split to single chars for recombination");

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

        private static bool HasLookup(string text)
        {
            if (_lookups.TryGetValue(text, out var ids) && ids.Count > 0)
                return true;
            try
            {
                var hira = KanaConverter.ToNormalizedHiragana(text);
                if (hira != text && _lookups.TryGetValue(hira, out ids) && ids.Count > 0)
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private static int GetBestLookupPriority(string text)
        {
            if (!_lookups.TryGetValue(text, out var ids) || ids.Count == 0)
                return -1;
            int best = -1;
            foreach (var id in ids)
            {
                if (WordMeta.TryGetValue(id, out var m))
                {
                    var score = m.GetPriorityScore(WanaKana.IsKana(text));
                    if (score > best) best = score;
                }
            }
            return best;
        }

        private static bool HasNonNameLookup(string text)
        {
            if (_lookups.TryGetValue(text, out var ids) && ids.Count > 0
                                                        && !ids.All(id => _nameOnlyWordIds.Contains(id)))
                return true;
            try
            {
                var hira = KanaConverter.ToNormalizedHiragana(text);
                if (hira != text && _lookups.TryGetValue(hira, out ids) && ids.Count > 0
                    && !ids.All(id => _nameOnlyWordIds.Contains(id)))
                    return true;
            }
            catch
            {
            }

            return false;
        }

        /// A word id is "kana-appropriate" when matching it from a pure-kana surface is plausible:
        /// the word is usually written in kana (uk), or it has no kanji written form at all.
        /// Kanji-backed non-uk words (配送, 童男, 遺影) reject — their kana lookup keys are readings,
        /// not attested written forms. Name-bank entries always reject (ウンマ, ヒューン).
        private static bool IsKanaAppropriateId(int id)
        {
            if (_nameOnlyWordIds.Contains(id)) return false;
            if (!WordMeta.TryGetValue(id, out var meta)) return false;
            if (meta.IsTrueName) return false;

            // uk is folded into the priority scores as ±10 at load time (kana = base + 10, kanji = base − 10)
            bool isUsuallyKana = meta.PriorityScoreKana > meta.PriorityScoreKanji;
            return isUsuallyKana || !_kanjiBackedWordIds.Contains(id);
        }

        private static bool HasKanaAppropriateLookup(string text)
        {
            if (_lookups.TryGetValue(text, out var ids) && ids.Any(IsKanaAppropriateId))
                return true;
            try
            {
                var hira = KanaConverter.ToNormalizedHiragana(text);
                if (hira != text && _lookups.TryGetValue(hira, out ids) && ids.Any(IsKanaAppropriateId))
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private static bool HasPrioritizedNonNameLookup(string text)
        {
            if (HasPrioritizedNonNameIds(text))
                return true;
            try
            {
                var hira = KanaConverter.ToNormalizedHiragana(text);
                if (hira != text && HasPrioritizedNonNameIds(hira))
                    return true;
            }
            catch
            {
            }
            return false;

            static bool HasPrioritizedNonNameIds(string key)
            {
                if (!_lookups.TryGetValue(key, out var ids) || ids.Count == 0)
                    return false;
                foreach (var id in ids)
                {
                    if (_nameOnlyWordIds.Contains(id)) continue;
                    if (WordMeta.TryGetValue(id, out var meta) && meta.GetPriorityScore(true) > 0)
                        return true;
                }
                return false;
            }
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
                        textHira = KanaConverter.ToNormalizedHiragana(text);
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
                                                                  (textHira.Length >= 5 &&
                                                                   Deconjugator.Instance.Deconjugate(textHira[..^1])
                                                                              .Any(f => HasLookup(f.Text))));
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

        // ・/＝ between noun tokens (ハーヴェイ・カイテル, サン＝テグジュペリ): Sudachi emits the
        // separator as its own SupplementarySymbol token, so dictionary compounds containing it can
        // only be recovered by allowing it as an inner connector. The lookup gate keeps this safe —
        // ad-hoc enumerations (リンゴ・ミカン) have no dictionary entry and never merge.
        private static bool IsNameConnectorSymbol(WordInfo w) =>
            w.PartOfSpeech == PartOfSpeech.SupplementarySymbol && w.Text is "・" or "＝";

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
                        // When an adjective stem or verb infinitive is followed by a noun-like token, try combining.
                        // Adjective: Sudachi splits kana compounds like でかぶつ into でか (形容詞) + ぶつ (接尾辞).
                        // Verb: Sudachi splits compounds like 飛び道具 into 飛び (動詞) + 道具 (名詞).
                        if (word.PartOfSpeech is PartOfSpeech.IAdjective or PartOfSpeech.Verb &&
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

                    // Try longest match first (up to 8 tokens, then 7, ..., then 2)
                    int bestMatch = 1;
                    string? matchedNounText = null;
                    for (int windowSize = Math.Min(8, sentence.Words.Count - i); windowSize >= 2; windowSize--)
                    {
                        bool allValid = true;
                        bool hasNameLikeToken = false;
                        for (int j = 0; j < windowSize; j++)
                        {
                            var w = sentence.Words[i + j].word;
                            // Counters (一本, 三年生, 数人) can continue a compound but not start one.
                            bool isNoun = PosMapper.IsNounForCompounding(w.PartOfSpeech) ||
                                          (j > 0 && w.PartOfSpeech == PartOfSpeech.Counter);
                            bool isInnerConnector = j > 0 && j < windowSize - 1 &&
                                                    (w is { PartOfSpeech: PartOfSpeech.Particle, Text: "の" } ||
                                                     IsNameConnectorSymbol(w));
                            if (!isNoun && !isInnerConnector)
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

                        var combinedText = ConcatTokenTexts(sentence.Words, i, windowSize);

                        if (NounCompoundExclusions.Contains(combinedText))
                            continue;

                        // Check if it exists in JMDict lookups — skip name-only matches
                        // when none of the constituent tokens are name-like
                        if (_lookups.TryGetValue(combinedText, out var wordIds) && wordIds.Count > 0 &&
                            (hasNameLikeToken || !wordIds.All(id => _nameOnlyWordIds.Contains(id))))
                        {
                            bestMatch = windowSize;
                            matchedNounText = combinedText;
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
                            matchedNounText = combinedText;
                            break;
                        }
                    }

                    if (bestMatch > 1)
                    {
                        int matchPriority = GetBestLookupPriority(matchedNounText!);
                        int afterMatchStart = i + bestMatch;
                        bool shouldSkip = false;

                        for (int overlap = 1; overlap < bestMatch && afterMatchStart - overlap >= i + 1; overlap++)
                        {
                            int altStart = afterMatchStart - overlap;
                            int remaining = sentence.Words.Count - altStart;
                            for (int altSize = Math.Min(8, remaining); altSize >= overlap + 1; altSize--)
                            {
                                if (altStart + altSize <= afterMatchStart)
                                    continue;

                                bool altAllValid = true;
                                for (int aj = 0; aj < altSize; aj++)
                                {
                                    var aw = sentence.Words[altStart + aj].word;
                                    bool altInnerConnector = aj > 0 && aj < altSize - 1 &&
                                                             (aw is { PartOfSpeech: PartOfSpeech.Particle, Text: "の" } ||
                                                              IsNameConnectorSymbol(aw));
                                    if (!PosMapper.IsNounForCompounding(aw.PartOfSpeech) &&
                                        !(aj > 0 && aw.PartOfSpeech == PartOfSpeech.Counter) && !altInnerConnector)
                                    {
                                        altAllValid = false;
                                        break;
                                    }
                                }
                                if (!altAllValid) continue;

                                var altText = ConcatTokenTexts(sentence.Words, altStart, altSize);
                                int altPriority = GetBestLookupPriority(altText);
                                if (altPriority <= 0)
                                {
                                    var altHiragana = KanaConverter.ToHiragana(altText, convertLongVowelMark: false);
                                    if (altHiragana != altText)
                                        altPriority = GetBestLookupPriority(altHiragana);
                                }

                                if (altPriority > matchPriority)
                                {
                                    bool orphanViable = true;
                                    for (int oi = i; oi < altStart; oi++)
                                    {
                                        if (GetBestLookupPriority(sentence.Words[oi].word.Text) <= 0)
                                        {
                                            orphanViable = false;
                                            break;
                                        }
                                    }
                                    if (orphanViable)
                                    {
                                        shouldSkip = true;
                                        break;
                                    }
                                }
                            }
                            if (shouldSkip) break;
                        }

                        if (shouldSkip)
                        {
                            result.Add(sentence.Words[i]);
                            i++;
                            continue;
                        }

                        var combinedText = matchedNounText!;
                        var combinedReading = ConcatTokenReadings(sentence.Words, i, bestMatch);
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

        private static readonly HashSet<string> ExpressionExclusions =
            ["このように", "そのように", "あのように", "どのように", "のように",
             "この様に", "その様に", "あの様に", "どの様に", "の様に",
             "何だと", "なんだと",
             // pronoun+particle: the JMDict entries are interjection/discourse senses that
             // rarely apply; the compositional reading is almost always correct
             "それが", "それは", "これが", "これは", "あれが", "あれは"];

        /// <summary>
        /// 2-token sequences combined into one expression. Curated: JMDict has entries for many
        /// compositional pairs (のです, それを, 外に) that must stay split, so 2-token combining
        /// can't be driven by dictionary POS alone. Surfaces are the concatenated token texts.
        /// </summary>
        private static readonly HashSet<string> TwoTokenExpressionWhitelist =
        [
            "どちらも", "どっちも", "いつでも", "何時でも", "一人で", "ひとりで", "独りで",
            "ものか", "もんか", "たまるか", "堪るか", "それとも", "至るまで", "いたるまで"
        ];

        /// <summary>
        /// Noun+verb idioms with an elided を: (noun surface, verb dictionary form) → JMDict key.
        /// Curated because JMDict also has compositional 〜を〜 entries (服を着る) whose bare
        /// noun+verb sequences must stay split.
        /// </summary>
        private static readonly Dictionary<(string, string), string> TwoTokenNounVerbIdioms = new()
        {
            [("カマ", "かける")] = "カマをかける",
            [("かま", "かける")] = "かまをかける",
            [("鎌", "かける")] = "鎌をかける",
            [("鎌", "掛ける")] = "鎌を掛ける",
        };

        private static void CombineExpressions(List<SentenceInfo> sentences)
        {
            foreach (var sentence in sentences)
                CombineExpressionsInSentence(sentence);
        }

        /// <summary>
        /// Chars whose HiraRollingHash katakana fold (c - 0x60) agrees with KanaConverter.ToHiragana,
        /// so a hash-set miss proves the hiragana lookup probe would also miss. Anything else
        /// (ヶ/ヵ/ゎ/ヮ, romaji, halfwidth kana, punctuation) must take the slow lookup path.
        /// </summary>
        private static bool IsHashFoldSafeChar(char c) =>
            (c >= 'ぁ' && c <= 'ゔ' && c != 'ゎ') ||  // hiragana ぁ..ゔ except ゎ
            (c >= 'ァ' && c <= 'ヴ' && c != 'ヮ') ||  // katakana ァ..ヴ except ヮ
            c == 'ー' || c == '々' ||                     // ー and 々
            (c >= '一' && c <= '鿿');                     // CJK unified ideographs

        private static bool IsHashFoldSafe(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (!IsHashFoldSafeChar(s[i]))
                    return false;
            return true;
        }

        private static readonly HashSet<long> ExpressionExclusionHashes = BuildExpressionExclusionHashes();

        private static HashSet<long> BuildExpressionExclusionHashes()
        {
            var set = new HashSet<long>();
            foreach (var text in ExpressionExclusions)
                set.Add(HiraRollingHash(text));
            return set;
        }

        private static void CombineExpressionsInSentence(SentenceInfo sentence)
        {
            {
                if (sentence.Words.Count < 3)
                    return;

                var words = sentence.Words;
                var tokenHashes = new long[words.Count];
                var tokenLens = new int[words.Count];
                var tokenSafe = new bool[words.Count];
                for (int w = 0; w < words.Count; w++)
                {
                    var text = words[w].word.Text;
                    tokenHashes[w] = HiraRollingHash(text);
                    tokenLens[w] = text.Length;
                    tokenSafe[w] = IsHashFoldSafe(text);
                }

                // Cumulative window hash/length over tokens i..i+k-1, rebuilt lazily per start index
                Span<long> cumHash = stackalloc long[9];
                Span<int> cumLen = stackalloc int[9];
                Span<bool> cumLenOk = stackalloc bool[9];

                var result = new List<(WordInfo word, int position, int length)>(sentence.Words.Count);
                int i = 0;

                while (i < sentence.Words.Count)
                {
                    int bestMatch = 1;
                    string? matchedText = null;

                    int maxWindow = Math.Min(8, sentence.Words.Count - i);
                    int cumBuilt = 0;
                    int safeUpTo = 0;
                    cumHash[0] = 0;
                    cumLen[0] = 0;
                    cumLenOk[0] = true;

                    for (int windowSize = maxWindow; windowSize >= 2; windowSize--)
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

                        if (hasSupplementarySymbol)
                            continue;

                        // 2-token combining is whitelist-only: JMDict has entries for countless
                        // compositional pairs (のです, それを, 外に, すると) that must stay split,
                        // and no POS gate separates those from semantic units (どちらも, ものか).
                        if (windowSize == 2)
                        {
                            var pairText = ConcatTokenTexts(sentence.Words, i, 2);
                            if (TwoTokenExpressionWhitelist.Contains(pairText))
                            {
                                if (!IsExpressionBoundaryTheft(sentence.Words, i, 2))
                                {
                                    bestMatch = 2;
                                    matchedText = pairText;
                                    break;
                                }
                            }
                            else if (TwoTokenNounVerbIdioms.TryGetValue(
                                         (sentence.Words[i].word.Text, sentence.Words[i + 1].word.DictionaryForm ?? ""),
                                         out var idiomDictForm)
                                     && !IsExpressionBoundaryTheft(sentence.Words, i, 2))
                            {
                                bestMatch = 2;
                                matchedText = idiomDictForm;
                                break;
                            }

                            continue;
                        }

                        if (!hasParticle)
                            continue;

                        if (cumBuilt < windowSize)
                        {
                            for (int k = cumBuilt + 1; k <= windowSize; k++)
                            {
                                int t = i + k - 1;
                                cumLen[k] = cumLen[k - 1] + tokenLens[t];
                                bool ok = cumLenOk[k - 1] && cumLen[k] <= _maxLookupKeyLength;
                                cumLenOk[k] = ok;
                                cumHash[k] = ok
                                    ? unchecked(cumHash[k - 1] * _hashBasePowers[tokenLens[t]] + tokenHashes[t])
                                    : 0;
                                if (safeUpTo == k - 1 && tokenSafe[t]) safeUpTo = k;
                            }

                            cumBuilt = windowSize;
                        }

                        bool windowSafe = safeUpTo >= windowSize;
                        bool surfaceMayMatch = !windowSafe ||
                                               (cumLenOk[windowSize] &&
                                                (_compoundHashSet!.Contains(cumHash[windowSize]) ||
                                                 ExpressionExclusionHashes.Contains(cumHash[windowSize])));

                        bool matched = false;
                        string? combinedText = null;

                        if (surfaceMayMatch)
                        {
                            combinedText = ConcatTokenTexts(sentence.Words, i, windowSize);

                            // Exclusions must also catch katakana variants (コレは → これは)
                            if (ExpressionExclusions.Contains(combinedText) ||
                                ExpressionExclusions.Contains(KanaConverter.ToHiragana(combinedText, convertLongVowelMark: false)))
                                continue;

                            matched = IsExpressionLookupMatch(combinedText);

                            if (!matched)
                                matched = IsMultiWordAdverbMatch(combinedText);

                            // Short function-word clusters tagged conj/prt/aux in JMDict
                            // (それとも as それ|と|も) — not exp, so the gates above miss them
                            if (!matched && windowSize == 3)
                                matched = IsFunctionClusterMatch(combinedText);
                        }

                        if (!matched)
                        {
                            var lastWord = sentence.Words[i + windowSize - 1].word;
                            if (lastWord.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective
                                && !string.IsNullOrEmpty(lastWord.DictionaryForm)
                                && lastWord.DictionaryForm != lastWord.Text)
                            {
                                var dictForm = lastWord.DictionaryForm;
                                bool dictMayMatch = true;
                                if (safeUpTo >= windowSize - 1 && IsHashFoldSafe(dictForm))
                                {
                                    int dictLen = cumLen[windowSize - 1] + dictForm.Length;
                                    dictMayMatch = cumLenOk[windowSize - 1] &&
                                                   dictForm.Length <= _maxLookupKeyLength &&
                                                   dictLen <= _maxLookupKeyLength &&
                                                   _compoundHashSet!.Contains(
                                                       unchecked(cumHash[windowSize - 1] * _hashBasePowers[dictForm.Length] +
                                                                 HiraRollingHash(dictForm)));
                                }

                                if (dictMayMatch)
                                {
                                    var prefix = ConcatTokenTexts(sentence.Words, i, windowSize - 1);
                                    var dictCandidate = prefix + dictForm;
                                    if (IsExpressionLookupMatch(dictCandidate))
                                    {
                                        matched = true;
                                        combinedText = dictCandidate;
                                    }
                                }

                                // Sudachi's DictionaryForm goes to the base verb (取られ → 取る), missing
                                // intermediate forms that ARE the JMDict entry (呆気に取られる). Complete the
                                // tail via single-step deconjugation instead.
                                if (!matched && windowSize <= 4)
                                {
                                    var prefix = ConcatTokenTexts(sentence.Words, i, windowSize - 1);
                                    foreach (var form in Deconjugator.Instance.Deconjugate(lastWord.Text))
                                    {
                                        if (form.Process.Count > 2 || form.Text == lastWord.Text || form.Text == dictForm)
                                            continue;
                                        var candidate = prefix + form.Text;
                                        if (IsExpressionLookupMatch(candidate))
                                        {
                                            matched = true;
                                            combinedText = candidate;
                                            break;
                                        }
                                    }
                                }
                            }

                        }

                        // Reject merges that behead the neighbourhood (様に言わせれば eating a name's
                        // honorific; そりゃそうだ leaving だろう's ろう stranded)
                        if (matched && IsExpressionBoundaryTheft(sentence.Words, i, windowSize))
                            continue;

                        if (matched)
                        {
                            bestMatch = windowSize;
                            matchedText = combinedText;
                            break;
                        }
                    }

                    if (bestMatch == 1 && i + 1 < sentence.Words.Count)
                    {
                        var nextW = sentence.Words[i + 1].word;
                        if (nextW is { Text: "と", PartOfSpeech: PartOfSpeech.Particle })
                        {
                            bool advMayMatch = true;
                            if (tokenSafe[i])
                            {
                                int advLen = tokenLens[i] + 1;
                                advMayMatch = tokenLens[i] <= _maxLookupKeyLength &&
                                              advLen <= _maxLookupKeyLength &&
                                              _compoundHashSet!.Contains(
                                                  unchecked(tokenHashes[i] * HASH_BASE + 'と'));
                            }

                            if (advMayMatch)
                            {
                                var adverbCandidate = sentence.Words[i].word.Text + "と";
                                var adverbHiragana = KanaConverter.ToHiragana(adverbCandidate, convertLongVowelMark: false);
                                List<int>? advWordIds = null;
                                if ((_lookups.TryGetValue(adverbCandidate, out advWordIds) || _lookups.TryGetValue(adverbHiragana, out advWordIds))
                                    && advWordIds.Count > 0
                                    && HasAdverbInMeta(advWordIds))
                                {
                                    bestMatch = 2;
                                    matchedText = adverbCandidate;
                                }
                            }
                        }
                    }

                    if (bestMatch > 1)
                    {
                        var dictForm = matchedText!;
                        var originalText = ConcatTokenTexts(sentence.Words, i, bestMatch);
                        var combinedReading = ConcatTokenReadings(sentence.Words, i, bestMatch);
                        int combinedLength = 0;
                        for (int j = 0; j < bestMatch; j++)
                            combinedLength += sentence.Words[i + j].length;

                        var combinedWord = new WordInfo
                                           {
                                               Text = originalText, DictionaryForm = dictForm, PartOfSpeech = PartOfSpeech.Expression,
                                               NormalizedForm = dictForm,
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

        private static bool IsExpressionLookupMatch(string text)
        {
            if (_lookups.TryGetValue(text, out var wordIds) && wordIds.Count > 0 &&
                ContainsAnyExpression(wordIds))
                return true;

            var hiragana = KanaConverter.ToHiragana(text, convertLongVowelMark: false);
            if (hiragana != text && _lookups.TryGetValue(hiragana, out wordIds) && wordIds.Count > 0 &&
                ContainsAnyExpression(wordIds))
                return true;

            return false;
        }

        private static bool ContainsAnyExpression(List<int> wordIds)
        {
            for (int i = 0; i < wordIds.Count; i++)
                if (_expressionWordIds.Contains(wordIds[i])) return true;
            return false;
        }

        private static bool IsMultiWordAdverbMatch(string text)
        {
            if (HasPosInMeta(text, PartOfSpeech.Adverb))
                return true;
            var hiragana = KanaConverter.ToHiragana(text, convertLongVowelMark: false);
            return hiragana != text && HasPosInMeta(hiragana, PartOfSpeech.Adverb);
        }

        /// <summary>
        /// Function-word clusters JMDict tags as prt/conj/aux rather than exp
        /// (それとも, ものか) — eligible for combining like expressions.
        /// </summary>
        private static bool IsFunctionClusterMatch(string text)
        {
            if (HasAnyPosInMeta(text)) return true;
            var hiragana = KanaConverter.ToHiragana(text, convertLongVowelMark: false);
            return hiragana != text && HasAnyPosInMeta(hiragana);

            static bool HasAnyPosInMeta(string key)
            {
                if (!_lookups.TryGetValue(key, out var ids) || ids.Count == 0) return false;
                foreach (var id in ids)
                {
                    if (!WordMeta.TryGetValue(id, out var meta)) continue;
                    foreach (var p in meta.Pos)
                        if (p is PartOfSpeech.Particle or PartOfSpeech.Conjunction or PartOfSpeech.Auxiliary)
                            return true;
                }

                return false;
            }
        }

        private static readonly HashSet<string> HonorificSuffixTexts = ["様", "さん", "くん", "ちゃん", "殿", "氏"];

        /// <summary>
        /// Rejects expression merges that steal a token from its real neighbour:
        /// an honorific belonging to the preceding name (ヴァレリア様|に言わせれば), or a window
        /// whose last token forms a cohesive function word with the NEXT token
        /// (そりゃそう[だ ろう], 耳にす[る する]) — merging would strand an unparseable fragment.
        /// </summary>
        private static bool IsExpressionBoundaryTheft(
            List<(WordInfo word, int position, int length)> words, int start, int windowSize)
        {
            var first = words[start].word;
            if (start > 0 && HonorificSuffixTexts.Contains(first.Text)
                && words[start - 1].word.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Pronoun)
                return true;

            int nextIdx = start + windowSize;
            if (nextIdx < words.Count)
            {
                var next = words[nextIdx].word;
                if (next.PartOfSpeech is not (PartOfSpeech.Particle or PartOfSpeech.Auxiliary
                    or PartOfSpeech.SupplementarySymbol))
                {
                    var concat = words[start + windowSize - 1].word.Text + next.Text;
                    if (IsCohesiveFunctionWord(concat))
                        return true;
                }
            }

            return false;
        }

        private static bool IsCohesiveFunctionWord(string text)
        {
            if (HasFunctionPos(text)) return true;
            var hiragana = KanaConverter.ToHiragana(text, convertLongVowelMark: false);
            return hiragana != text && HasFunctionPos(hiragana);

            static bool HasFunctionPos(string key)
            {
                if (!_lookups.TryGetValue(key, out var ids) || ids.Count == 0) return false;
                foreach (var id in ids)
                {
                    if (!WordMeta.TryGetValue(id, out var meta)) continue;
                    foreach (var p in meta.Pos)
                        if (p is PartOfSpeech.Auxiliary or PartOfSpeech.Adverb or PartOfSpeech.Conjunction
                            or PartOfSpeech.Particle or PartOfSpeech.Expression)
                            return true;
                }

                return false;
            }
        }

        private static bool HasAdverbInMeta(List<int> wordIds)
        {
            for (int i = 0; i < wordIds.Count; i++)
            {
                if (!WordMeta.TryGetValue(wordIds[i], out var meta)) continue;
                if (Array.IndexOf(meta.Pos, PartOfSpeech.Adverb) >= 0) return true;
            }
            return false;
        }

        private static bool HasPosInMeta(string text, PartOfSpeech targetPos)
        {
            if (!_lookups.TryGetValue(text, out var wordIds) || wordIds.Count == 0)
                return false;

            foreach (var id in wordIds)
            {
                if (!WordMeta.TryGetValue(id, out var meta)) continue;
                foreach (var p in meta.Pos)
                    if (p == targetPos) return true;
            }
            return false;
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

        private static async Task<List<DeckWord>> ApplyMisparseGates(
            List<SentenceInfo> sentences, List<DeckWord> corrected, ParserDiagnostics? diagnostics = null)
        {
            if (corrected.Count == 0) return corrected;

            var wordIds = corrected.Select(w => w.WordId).Distinct();
            var wordData = await JmDictCache.GetWordsAsync(wordIds);
            PriorityOverrides.ApplyAll(wordData.Values);

            var flatTokens = new List<WordInfo>();
            var sentenceInitial = new List<bool>();
            foreach (var sentence in sentences)
            {
                bool first = true;
                foreach (var (word, _, _) in sentence.Words)
                {
                    if (word.PartOfSpeech == PartOfSpeech.SupplementarySymbol)
                        continue;
                    flatTokens.Add(word);
                    sentenceInitial.Add(first);
                    first = false;
                }
            }

            var result = new List<DeckWord>(corrected.Count);
            int ci = 0;

            for (int i = 0; i < flatTokens.Count && ci < corrected.Count; i++)
            {
                var token = flatTokens[i];
                if (token.ResolvedWordId == null) continue;

                var deckWord = corrected[ci++];

                wordData.TryGetValue(deckWord.WordId, out var jmWord);
                var (isUk, hasKanji, readingIsIchi) = MisparseGates.GetWordFlags(jmWord, deckWord.ReadingIndex);

                var prev = i > 0 ? flatTokens[i - 1] : null;
                var next = i < flatTokens.Count - 1 ? flatTokens[i + 1] : null;
                var ctx = new MisparseGateContext(token, deckWord, prev, next, isUk, hasKanji, readingIsIchi,
                    sentenceInitial[i]);

                var decision = MisparseGates.Evaluate(in ctx);
                if (decision.IsMisparsed)
                {
                    diagnostics?.LogDroppedToken(token.Text, token.PartOfSpeech,
                        $"misparsed:{decision.GateId}");
                    continue;
                }

                result.Add(deckWord);
            }

            while (ci < corrected.Count)
                result.Add(corrected[ci++]);

            return result;
        }

        private static List<DeckWord> ExcludeFinalMisparses(IEnumerable<DeckWord> words, ParserDiagnostics? diagnostics = null)
        {
            if (ExcludedMisparses.Count == 0)
                return words.ToList();

            var kept = new List<DeckWord>();
            foreach (var w in words)
            {
                if (ExcludedMisparses.Contains((w.WordId, w.ReadingIndex)))
                {
                    diagnostics?.LogParserEvent(
                        "ExcludeFinalMisparses", "remove", [w.OriginalText], null,
                        $"(WordId={w.WordId}, ReadingIndex={w.ReadingIndex}) in ExcludedMisparses");
                    continue;
                }
                kept.Add(w);
            }

            return kept;
        }

        private static string ConcatSlice<T>(List<T> list, int start, int count, Func<T, string> selector)
        {
            if (count == 1) return selector(list[start]);
            if (count == 2) return string.Concat(selector(list[start]), selector(list[start + 1]));
            if (count == 3) return string.Concat(selector(list[start]), selector(list[start + 1]), selector(list[start + 2]));
            if (count == 4) return string.Concat(selector(list[start]), selector(list[start + 1]), selector(list[start + 2]), selector(list[start + 3]));
            var parts = new string[count];
            for (int idx = 0; idx < count; idx++)
                parts[idx] = selector(list[start + idx]);
            return string.Concat(parts);
        }

        private static string ConcatTokenTexts(List<(WordInfo word, int position, int length)> tokens, int start, int count) =>
            ConcatSlice(tokens, start, count, static t => t.word.Text);

        private static string ConcatTokenReadings(List<(WordInfo word, int position, int length)> tokens, int start, int count) =>
            ConcatSlice(tokens, start, count, static t => t.word.Reading);

        private static string ConcatWordInfoTexts(List<WordInfo> wordInfos, int start, int count) =>
            ConcatSlice(wordInfos, start, count, static w => w.Text);

        private static string ConcatWordInfoReadings(List<WordInfo> wordInfos, int start, int count) =>
            ConcatSlice(wordInfos, start, count, static w => w.Reading);

        private static (int startIndex, string dictionaryForm, int wordId)? TryMatchCompounds(
            List<WordInfo> wordInfos,
            int wordIndex,
            long[] tokenHashes,
            int lastConsumedIndex = -1,
            bool forceExpressionOnly = false)
        {
            var verb = wordInfos[wordIndex];
            var dictForm = verb.DictionaryForm;

            int maxPrefixTokens = Math.Min(4, wordIndex);
            Span<long> prefCumHash = stackalloc long[maxPrefixTokens + 1];
            Span<int> prefCumLen = stackalloc int[maxPrefixTokens + 1];
            prefCumHash[0] = 0;
            prefCumLen[0] = 0;
            for (int k = 1; k <= maxPrefixTokens; k++)
            {
                int tokenIdx = wordIndex - k;
                int prevLen = prefCumLen[k - 1];
                if (prevLen >= _hashBasePowers!.Length)
                {
                    maxPrefixTokens = k - 1;
                    break;
                }
                unchecked
                {
                    prefCumHash[k] = tokenHashes[tokenIdx] * _hashBasePowers[prevLen] + prefCumHash[k - 1];
                }
                prefCumLen[k] = wordInfos[tokenIdx].Text.Length + prevLen;
            }
            prefCumHash = prefCumHash[..(maxPrefixTokens + 1)];
            prefCumLen = prefCumLen[..(maxPrefixTokens + 1)];

            if (string.IsNullOrEmpty(dictForm))
            {
                var deconjugated = Deconjugator.Instance.Deconjugate(KanaConverter.ToHiragana(verb.Text));
                if (deconjugated.Count == 0) return null;
                var selectedForm = deconjugated[0];
                for (int d = 1; d < deconjugated.Count; d++)
                {
                    var f = deconjugated[d];
                    if (f.Text.Length < selectedForm.Text.Length ||
                        (f.Text.Length == selectedForm.Text.Length && string.Compare(f.Text, selectedForm.Text, StringComparison.Ordinal) < 0))
                        selectedForm = f;
                }
                dictForm = string.IsNullOrEmpty(selectedForm.Text) ? verb.Text : selectedForm.Text;
                return TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, dictForm, forceExpressionOnly,
                    HiraRollingHash(dictForm), prefCumHash, prefCumLen);
            }

            long dictFormHash = HiraRollingHash(dictForm);
            var result = TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, dictForm, forceExpressionOnly,
                dictFormHash, prefCumHash, prefCumLen);
            if (result.HasValue) return result;

            if (verb.Text != dictForm)
            {
                result = TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, verb.Text, forceExpressionOnly,
                    tokenHashes[wordIndex], prefCumHash, prefCumLen);
                if (result.HasValue) return result;
            }

            if (dictForm is "する" or "ある"
                && (verb.Text.Contains("ない") || verb.Text.Contains("なかっ") || verb.Text.Contains("なく")
                    || verb.Text.Contains("ねえ") || verb.Text.Contains("ねー") || verb.Text.Contains("ねぇ")
                    || verb.Text.Contains("ませ") || verb.Text.EndsWith("ん") || verb.Text.EndsWith("ず")))
            {
                var negForm = dictForm == "する" ? "しない" : "ない";
                result = TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, negForm, forceExpressionOnly,
                    HiraRollingHash(negForm), prefCumHash, prefCumLen);
                if (result.HasValue) return result;
            }

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

                var sortedIntermediates = SortByLengthDescending(intermediates);
                foreach (var intermediate in sortedIntermediates)
                {
                    result = TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, intermediate, forceExpressionOnly,
                        HiraRollingHash(intermediate), prefCumHash, prefCumLen);
                    if (result.HasValue) return result;
                }

                var shortBaseForms = new HashSet<string>(StringComparer.Ordinal);
                foreach (var form in deconj)
                {
                    if (form.Text.Length >= 3 && form.Text.Length < dictForm.Length
                                              && MorphologicalAnalyser.DictionaryVerbEndings.Contains(form.Text[^1]))
                        shortBaseForms.Add(form.Text);
                }

                var sortedShort = SortByLengthDescending(shortBaseForms);
                foreach (var shortForm in sortedShort)
                {
                    result = TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, shortForm, forceExpressionOnly,
                        HiraRollingHash(shortForm), prefCumHash, prefCumLen);
                    if (result.HasValue) return result;
                }

                var altBaseForms = new HashSet<string>(StringComparer.Ordinal);
                foreach (var form in deconj)
                {
                    if (!string.IsNullOrEmpty(form.Text) && form.Text != dictForm
                        && form.Text.Length <= dictForm.Length
                        && form.Text.Length >= 2
                        && MorphologicalAnalyser.DictionaryVerbEndings.Contains(form.Text[^1]))
                        altBaseForms.Add(form.Text);
                }

                var sortedAlt = SortByLengthDescending(altBaseForms);
                foreach (var altForm in sortedAlt)
                {
                    result = TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, altForm, forceExpressionOnly,
                        HiraRollingHash(altForm), prefCumHash, prefCumLen);
                    if (result.HasValue) return result;
                }
            }

            if (dictForm == verb.Text)
            {
                if (verb.PartOfSpeech == PartOfSpeech.Verb
                    && !string.IsNullOrEmpty(verb.NormalizedForm) && verb.NormalizedForm != dictForm)
                {
                    result = TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, verb.NormalizedForm, forceExpressionOnly,
                        HiraRollingHash(verb.NormalizedForm), prefCumHash, prefCumLen);
                    if (result.HasValue) return result;

                    var hiraganaNorm = KanaConverter.ToHiragana(verb.NormalizedForm, convertLongVowelMark: false);
                    if (hiraganaNorm != verb.NormalizedForm)
                    {
                        result = TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, hiraganaNorm, forceExpressionOnly,
                            HiraRollingHash(hiraganaNorm), prefCumHash, prefCumLen);
                        if (result.HasValue) return result;
                    }
                }

                // Nouns don't conjugate — skip deconjugation-based compound search
                if (verb.PartOfSpeech is not (PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Name))
                {
                    var verbKana = KanaConverter.ToHiragana(verb.Text, convertLongVowelMark: false);
                    bool hasKanji = false;
                    foreach (var c in verb.Text)
                        if (JapaneseTextHelper.IsKanji(c)) { hasKanji = true; break; }

                    if (hasKanji && !MorphologicalAnalyser.DictionaryVerbEndings.Contains(verbKana[^1]))
                    {
                        var deconj = Deconjugator.Instance.Deconjugate(KanaConverter.ToHiragana(verb.Text));
                        var uniqueTexts = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var d in deconj)
                        {
                            if (!string.IsNullOrEmpty(d.Text) && d.Text != dictForm)
                                uniqueTexts.Add(d.Text);
                        }

                        var sorted = SortByLengthAscending(uniqueTexts);
                        foreach (var form in sorted)
                        {
                            result = TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, form, forceExpressionOnly,
                                HiraRollingHash(form), prefCumHash, prefCumLen);
                            if (result.HasValue) return result;
                        }
                    }
                }
            }

            return null;
        }

        private static List<string> SortByLengthDescending(HashSet<string> set)
        {
            var list = new List<string>(set);
            list.Sort((a, b) => b.Length.CompareTo(a.Length));
            return list;
        }

        private static List<string> SortByLengthAscending(HashSet<string> set)
        {
            var list = new List<string>(set);
            list.Sort((a, b) =>
            {
                int cmp = a.Length.CompareTo(b.Length);
                return cmp != 0 ? cmp : string.Compare(a, b, StringComparison.Ordinal);
            });
            return list;
        }

        private static (int startIndex, string dictionaryForm, int wordId)? TryMatchCompoundWindow(
            List<WordInfo> wordInfos,
            int wordIndex,
            int lastConsumedIndex,
            string dictForm,
            bool forceExpressionOnly,
            long dictFormHash,
            Span<long> prefCumHash,
            Span<int> prefCumLen)
        {
            int dictFormLen = dictForm.Length;
            int maxKeyLen = _maxLookupKeyLength;
            Span<char> candidateBuf = maxKeyLen <= 256 ? stackalloc char[maxKeyLen] : new char[maxKeyLen];
            Span<char> hiraganaBuf = maxKeyLen <= 256 ? stackalloc char[maxKeyLen] : new char[maxKeyLen];

            for (int windowSize = Math.Min(5, wordIndex + 1); windowSize >= 2; windowSize--)
            {
                int startIndex = wordIndex - windowSize + 1;

                if (startIndex <= lastConsumedIndex)
                    continue;

                var firstWord = wordInfos[startIndex];
                if (firstWord.PartOfSpeech is PartOfSpeech.Particle or PartOfSpeech.Interjection)
                    continue;
                if (firstWord is { PartOfSpeech: PartOfSpeech.Conjunction, Text.Length: 1 })
                    continue;
                if (firstWord.WasReclassifiedFromSuffix)
                    continue;
                // An honorific belonging to a preceding name must not open an expression window
                // (ヴァレリア様|に言わせれば must not become 様|に言わせれば → ように言う)
                if (startIndex > 0 && HonorificSuffixTexts.Contains(firstWord.Text)
                    && wordInfos[startIndex - 1].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                        or PartOfSpeech.Pronoun or PartOfSpeech.Name)
                    continue;

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

                int k = windowSize - 1;
                if (k >= prefCumLen.Length)
                    continue;
                int prefixLen = prefCumLen[k];
                int totalLen = prefixLen + dictFormLen;
                if (totalLen > maxKeyLen)
                    continue;

                long candidateHash = unchecked(prefCumHash[k] * _hashBasePowers![dictFormLen] + dictFormHash);
                if (!_compoundHashSet!.Contains(candidateHash))
                    continue;

                bool expressionOnly = forceExpressionOnly || firstWord.PartOfSpeech is PartOfSpeech.Suffix or PartOfSpeech.Auxiliary or PartOfSpeech.Adverb;

                // Build candidate span on stack: [prefix tokens...][dictForm]
                var candidateSpan = candidateBuf[..totalLen];
                int pos = 0;
                for (int j = startIndex; j < startIndex + windowSize - 1; j++)
                {
                    var text = wordInfos[j].Text;
                    text.AsSpan().CopyTo(candidateSpan[pos..]);
                    pos += text.Length;
                }
                dictForm.AsSpan().CopyTo(candidateSpan[pos..]);

                if (windowSize == 2 && wordInfos[startIndex].Text == dictForm && wordInfos[wordIndex].Text != dictForm)
                    continue;

                int? exprWordId = null, compWordId = null;

                if (_lookupsAlt.TryGetValue(candidateSpan, out var wordIds) && wordIds.Count > 0)
                {
                    bool isKana = true;
                    foreach (var c in candidateSpan)
                        if (c is not ((>= 'ぁ' and <= 'ゟ') or (>= '゠' and <= 'ヿ'))) { isKana = false; break; }
                    (exprWordId, compWordId) = CompoundWordSelector.FindCompoundWordIds(wordIds, WordMeta, isKana);
                }

                if (exprWordId == null && compWordId == null)
                {
                    bool hasKatakana = false;
                    for (int c = 0; c < totalLen; c++)
                        if (candidateSpan[c] is >= '゠' and <= 'ヿ') { hasKatakana = true; break; }

                    if (hasKatakana)
                    {
                        var hiraganaSpan = hiraganaBuf[..totalLen];
                        candidateSpan.CopyTo(hiraganaSpan);
                        for (int c = 0; c < totalLen; c++)
                        {
                            char ch = hiraganaSpan[c];
                            if (ch >= 'ァ' && ch <= 'ヶ') hiraganaSpan[c] = (char)(ch - 0x60);
                        }
                        if (_lookupsAlt.TryGetValue(hiraganaSpan, out var hiraWordIds) && hiraWordIds.Count > 0)
                        {
                            (exprWordId, compWordId) = CompoundWordSelector.FindCompoundWordIds(hiraWordIds, WordMeta, isKana: true);
                        }
                    }
                }

                var matchId = expressionOnly ? exprWordId : (exprWordId ?? compWordId);
                if (matchId.HasValue)
                    return (startIndex, candidateSpan.ToString(), matchId.Value);
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
            string? sudachiReading = null,
            PartOfSpeech sudachiPOS = PartOfSpeech.Unknown,
            bool isSudachiPossibleDependant = false)
        {
            var context = FormScoringContext.Create(
                                                    surface,
                                                    dictionaryForm,
                                                    normalizedForm,
                                                    isNameContext,
                                                    sudachiReading,
                                                    sudachiPOS: sudachiPOS,
                                                    isSudachiPossibleDependant: isSudachiPossibleDependant);

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

        private static FuriganaHint? FindMatchingHint(FuriganaHint[]? hints, WordInfo token)
        {
            if (hints == null || token.StartOffset < 0) return null;
            int tokenEnd = token.StartOffset + token.Text.Length;
            foreach (var h in hints)
            {
                if (h.Offset < token.StartOffset || h.Offset >= tokenEnd) continue;
                int hintEnd = h.Offset + h.Length;
                if (hintEnd > tokenEnd) continue;

                if (h.Length == token.Text.Length) return h;

                // Partial hint (e.g. {開'あ}く) — extend reading with unhinted okurigana
                int suffixStart = h.Offset - token.StartOffset + h.Length;
                var suffix = token.Text.AsSpan(suffixStart);
                var extendedReading = h.Reading + suffix.ToString();
                return new FuriganaHint(token.StartOffset, token.Text.Length, extendedReading);
            }
            return null;
        }

        private static async Task<List<DeckWord>> ApplyAdjacentScoring(
            List<SentenceInfo> sentences,
            List<(DeckWord? word, int? margin, List<FormCandidate>? candidates)> processedResults,
            Dictionary<(string, PartOfSpeech, string, string, bool, bool), List<FormCandidate>>? candidateLookup = null,
            ParserDiagnostics? diagnostics = null,
            FuriganaHint[]? relocatedHints = null)
        {
            int pos = 0;
            var sentencePairs = BuildSentencePairs(sentences, wi =>
            {
                if (pos >= processedResults.Count) return (null, null);
                var r = processedResults[pos++];
                return (r.word, r.margin);
            });
            return await ApplyAdjacentScoringCore(sentencePairs, candidateLookup, diagnostics, relocatedHints);
        }

        private static async Task<List<DeckWord>> ApplyAdjacentScoring(
            List<SentenceInfo> sentences,
            Dictionary<(string, PartOfSpeech, string, string, bool, bool), (DeckWord? word, int? margin)> resultLookup,
            Dictionary<(string, PartOfSpeech, string, string, bool, bool), List<FormCandidate>>? candidateLookup = null,
            ParserDiagnostics? diagnostics = null,
            FuriganaHint[]? relocatedHints = null)
        {
            var sentencePairs = BuildSentencePairs(sentences, wi => LookupResult(wi, resultLookup));
            return await ApplyAdjacentScoringCore(sentencePairs, candidateLookup, diagnostics, relocatedHints);
        }

        private static bool IsInfinitiveResult(DeckWord? word) =>
            word?.Conjugations is { Count: > 0 } conj && conj[^1] is "(infinitive)" or "(unstressed infinitive)";

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
            Dictionary<(string, PartOfSpeech, string, string, bool, bool), List<FormCandidate>>? candidateLookup = null,
            ParserDiagnostics? diagnostics = null,
            FuriganaHint[]? relocatedHints = null)
        {
            // Pass 1: identify tokens needing rederivation and collect all word IDs
            var allWordIds = new HashSet<int>();
            var rederiveStates = new Dictionary<(int sentIdx, int tokIdx), RederivationHelper.RederiveState>();
            var cachedCandidates = new Dictionary<(int sentIdx, int tokIdx), List<FormCandidate>>();
            var rederiveCache = new Dictionary<(string, PartOfSpeech, string, string, bool, bool), RederivationHelper.RederiveState?>();

            for (int si = 0; si < sentencePairs.Count; si++)
            {
                var sentenceWords = sentencePairs[si];
                bool isArchaicPass1 = IsClassicalSentence(sentenceWords);
                for (int i = 0; i < sentenceWords.Count; i++)
                {
                    var (currentInfo, currentResult, currentMargin) = sentenceWords[i];
                    if (currentResult == null) continue;

                    WordInfo? nextInfo = i < sentenceWords.Count - 1 ? sentenceWords[i + 1].word : null;
                    bool nextIsCopula = nextInfo != null && TransitionRuleSets.CopulaForms.Contains(nextInfo.Text);

                    var cachedHint = FindMatchingHint(relocatedHints, currentInfo);
                    bool hasHint = cachedHint != null;
                    bool isPreMatched = currentInfo.PreMatchedWordId.HasValue && currentInfo.PreMatchedCandidateWordIds == null;
                    if (isPreMatched && !hasHint) continue;

                    WordInfo? prevInfo = i > 0 ? sentenceWords[i - 1].word : null;
                    var prevResult = i > 0 ? sentenceWords[i - 1].result : null;
                    var nextResult = i < sentenceWords.Count - 1 ? sentenceWords[i + 1].result : null;

                    // A standalone infinitive after の / a noun / another infinitive is grammatically
                    // suspect (子供たちの群れ、群れ — the noun homograph should win), so these get
                    // rescored even when the first pass was confident. The infinitive-after-infinitive
                    // trigger keeps enumeration chains rescorable as corrections cascade left-to-right.
                    bool infinitiveAfterNominal = IsInfinitiveResult(currentResult) && i > 0 &&
                        (prevInfo!.Text == "の"
                         || IsInfinitiveResult(prevResult)
                         || prevResult?.PartsOfSpeech.Any(p => PosMask.Has(PosMask.NounLike, PosMask.Bit(p))) == true);

                    if (!isArchaicPass1 && !nextIsCopula && !hasHint && !infinitiveAfterNominal
                        && ScoringPolicy.IsHighConfidence(currentMargin)) continue;

                    bool forceRederive = hasHint || ScoringPolicy.IsLowConfidence(currentMargin);

                    if (!forceRederive)
                        for (int k = Math.Max(0, i - 3); k < i; k++)
                        {
                            var kr = sentenceWords[k].result;
                            if (kr != null && TransitionRuleSets.NounVerbCollocations.ContainsKey(kr.WordId))
                            { forceRederive = true; break; }
                        }

                    // infinitiveAfterNominal bypasses the prefilter: its prev context may only become
                    // noun-like after an earlier token is corrected in pass 2 (群れ、群れ cascades).
                    if (!forceRederive && !nextIsCopula && !isArchaicPass1 && !infinitiveAfterNominal
                        && !TransitionRuleEngine.CouldAnySoftRuleApply(
                         currentResult.PartsOfSpeech, currentInfo.Text,
                         prevResult?.PartsOfSpeech, prevInfo?.Text,
                         nextResult?.PartsOfSpeech, nextInfo?.Text))
                        continue;

                    if (candidateLookup != null && !hasHint)
                    {
                        var key = GetDedupKey(currentInfo);
                        if (candidateLookup.TryGetValue(key, out var fpCandidates))
                        {
                            cachedCandidates[(si, i)] = fpCandidates;
                            continue;
                        }
                    }

                    var dedupKey = GetDedupKey(currentInfo);
                    RederivationHelper.RederiveState? state;
                    if (!rederiveCache.TryGetValue(dedupKey, out state))
                    {
                        state = RederivationHelper.CollectRederivationIds(currentInfo, _lookups, Deconjugator.Instance);
                        rederiveCache[dedupKey] = state;
                    }
                    if (state == null) continue;

                    rederiveStates[(si, i)] = state;
                    allWordIds.UnionWith(state.CandidateIds);
                }
            }

            // Single batch fetch for all needed words (only for tokens not covered by cached candidates)
            Dictionary<int, JmDictWord> wordCache;
            try
            {
                wordCache = allWordIds.Count > 0
                    ? await JmDictCache.GetWordsAsync(allWordIds)
                    : new Dictionary<int, JmDictWord>();
                PriorityOverrides.ApplyAll(wordCache.Values);
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
                var resolvedResults = new DeckWord?[sentenceWords.Count];
                for (int ri = 0; ri < sentenceWords.Count; ri++)
                    resolvedResults[ri] = sentenceWords[ri].result;

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

                    bool tokenHasHint = FindMatchingHint(relocatedHints, currentInfo) != null;

                    List<FormCandidate>? candidates = null;
                    bool fromFirstPassCache = false;
                    if (cachedCandidates.TryGetValue((si, i), out var cached))
                    {
                        candidates = cached;
                        fromFirstPassCache = true;
                    }
                    else if (rederiveStates.TryGetValue((si, i), out var state))
                    {
                        candidates = RederivationHelper.BuildCandidatesFromWords(state, wordCache, skipPosFilter: tokenHasHint);
                    }

                    if (candidates == null || candidates.Count == 0)
                    {
                        currentInfo.ResolvedWordId = currentResult.WordId;
                        corrected.Add(currentResult);
                        continue;
                    }

                    WordInfo? prevInfo = i > 0 ? sentenceWords[i - 1].word : null;
                    WordInfo? nextInfo = i < sentenceWords.Count - 1 ? sentenceWords[i + 1].word : null;
                    var prevResult = i > 0 ? resolvedResults[i - 1] : null;
                    var nextResult = i < sentenceWords.Count - 1 ? sentenceWords[i + 1].result : null;

                    var context = AdjacentWordScorer.AdjacentContext.Create(
                                                                         prevResult?.PartsOfSpeech,
                                                                         prevInfo?.Text,
                                                                         nextResult?.PartsOfSpeech,
                                                                         nextInfo?.Text);

                    // First-pass candidates already have valid ScoreTraces. The only context
                    // differences in adjacent scoring are IsArchaicSentence/IsSentenceInitial/IsSentenceFinal,
                    // which only affect WordPriorityScorer. Skip rescoring for interior non-archaic tokens.
                    bool skipRescore = fromFirstPassCache && !isArchaicSentence && i > 0 && i < sentenceWords.Count - 1;

                    var scoringContext = FormScoringContext.Create(
                        currentInfo.Text, currentInfo.DictionaryForm, currentInfo.NormalizedForm,
                        currentInfo.IsPersonNameContext, currentInfo.Reading,
                        isArchaicSentence,
                        isSentenceInitial: i == 0,
                        isSentenceFinal: i == sentenceWords.Count - 1,
                        sudachiPOS: currentInfo.PartOfSpeech,
                        isSudachiPossibleDependant: currentInfo.HasPartOfSpeechSection(PartOfSpeechSection.PossibleDependant));

                    bool anyNonZeroBonus = false;
                    var bonusCache = new Dictionary<FormCandidate, (int bonus, List<string>? rules, int rubyBonus)>();

                    Dictionary<int, int>? collocMap = null;
                    for (int k = Math.Max(0, i - 3); k < i; k++)
                    {
                        var rr = resolvedResults[k];
                        if (rr != null
                            && TransitionRuleSets.NounVerbCollocations.TryGetValue(rr.WordId, out var cv))
                        {
                            collocMap ??= new Dictionary<int, int>();
                            collocMap[cv.TargetWordId] = collocMap.GetValueOrDefault(cv.TargetWordId) + cv.Bonus;
                        }
                    }

                    // Copula can only follow nominals — clear the POS-incompatibility flag so
                    // noun candidates can receive their grammatical bonus from noun-copula-synergy.
                    if (nextInfo != null && TransitionRuleSets.CopulaForms.Contains(nextInfo.Text))
                        foreach (var c in candidates)
                            c.IsPosIncompatibleDirectSurface = false;

                    var leftDictForm = prevInfo?.DictionaryForm;
                    var rightDictForm = nextInfo?.DictionaryForm;
                    var left2DictForm = i >= 2 ? sentenceWords[i - 2].word.DictionaryForm : null;
                    var right2DictForm = i < sentenceWords.Count - 2 ? sentenceWords[i + 2].word.DictionaryForm : null;

                    FuriganaHint? matchingHint = FindMatchingHint(relocatedHints, currentInfo);
                    string? hintHiragana = matchingHint.HasValue
                        ? KanaScoringHelpers.ToNormalizedHiragana(matchingHint.Value.Reading, convertLongVowelMark: false)
                        : null;
                    foreach (var candidate in candidates)
                    {
                        if (!skipRescore)
                        {
                            var trace = FormCandidateScorer.Score(candidate, scoringContext, ArchaicPosTypes);
                            candidate.SetScoreTrace(trace);
                        }

                        int rubyContextBonus = RubyPriorsScorer.ScoreWithContext(candidate, scoringContext,
                                                 leftDictForm, rightDictForm, left2DictForm, right2DictForm)
                                             - candidate.RubyPriorsScore;

                        int collocBonus = collocMap != null && collocMap.TryGetValue(candidate.Word.WordId, out var cb) ? cb : 0;
                        int furiganaBonus = Scoring.FuriganaHintScorer.Score(candidate, hintHiragana);

                        List<string>? rules = diagnostics != null ? new List<string>() : null;
                        int effectiveRubyBonus = collocMap != null ? 0 : rubyContextBonus;
                        int bonus = AdjacentWordScorer.CalculateContextBonus(candidate, context, rules) + collocBonus + effectiveRubyBonus + furiganaBonus;
                        if (collocBonus != 0)
                            (rules ??= []).Add("noun-verb-collocation");
                        if (furiganaBonus != 0)
                            (rules ??= []).Add($"furigana-hint:{matchingHint!.Value.Reading}");
                        bonusCache[candidate] = (bonus, rules, rubyContextBonus);
                        if (bonus != 0) anyNonZeroBonus = true;
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

                    Func<FormCandidate, int> getBonusFunc = c => bonusCache.TryGetValue(c, out var b) ? b.bonus : 0;

                    var newBest = FormCandidateSelector.PickTopCandidatesWithBonus(candidates, getBonusFunc);
                    if (newBest != null)
                        newBest = WordFrequencyPriors.Apply(newBest, candidates, scoringContext) ?? newBest;
                    if (newBest != null)
                        newBest = FormCandidateSelector.ApplyKanjiHomographPriorityCap(newBest, candidates, scoringContext) ?? newBest;
                    int newBestBonus = newBest != null ? getBonusFunc(newBest) : 0;
                    int newBestAdjusted = newBest != null ? ScoringPolicy.EffectiveScore(newBest) + newBestBonus : int.MinValue;

                    bool changed = newBest != null &&
                                   (newBest.Word.WordId != currentResult.WordId || newBest.ReadingIndex != currentResult.ReadingIndex);

                    if (diagnostics != null && newBest != null &&
                        bonusCache.TryGetValue(newBest, out var dnb) && dnb.rules is { Count: > 0 })
                    {
                        var firstPassCandidate = candidates.FirstOrDefault(c =>
                                                                           c.Word.WordId == currentResult.WordId &&
                                                                           c.ReadingIndex == currentResult.ReadingIndex);
                        var firstPassScore = firstPassCandidate?.TotalScore ?? 0;
                        int firstPassRuby = firstPassCandidate != null && bonusCache.TryGetValue(firstPassCandidate, out var fpb) ? fpb.rubyBonus : 0;

                        diagnostics!.AdjacentScoring.Add(new AdjacentScoringEntry
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
                                                            RulesMatched = dnb.rules,
                                                            FirstPassWinner = new AdjacentCandidateInfo
                                                                              {
                                                                                  WordId = currentResult.WordId,
                                                                                  ReadingIndex = currentResult.ReadingIndex,
                                                                                  Score = firstPassScore, ContextBonus = 0,
                                                                                  AdjustedScore = firstPassScore,
                                                                                  RubyContextBonus = firstPassRuby
                                                                              },
                                                            AdjustedWinner = new AdjacentCandidateInfo
                                                                             {
                                                                                 WordId = newBest.Word.WordId,
                                                                                 ReadingIndex = newBest.ReadingIndex,
                                                                                 Score = newBest.TotalScore, ContextBonus = newBestBonus,
                                                                                 AdjustedScore = newBestAdjusted,
                                                                                 RubyContextBonus = dnb.rubyBonus
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

            return corrected;
        }

        #endregion

        #region Confidence resegmentation helpers

        private static Dictionary<(int sentenceIndex, int wordIndex), int?> BuildMarginMap(
            List<SentenceInfo> sentences,
            List<(DeckWord? word, int? margin, List<FormCandidate>? candidates)> processedWithMargins)
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

        private static byte[]? BuildUserDictCsv(List<DeckDictionaryEntry>? entries)
        {
            if (entries is not { Count: > 0 }) return null;

            var sb = new StringBuilder();
            foreach (var entry in entries.OrderByDescending(e => e.Surface.Trim().Length))
            {
                var surface = entry.Surface.Trim();
                if (surface.Length < 2 || surface.Contains(',') || surface.Contains('\n')) continue;
                if (IsRiskyNameEntry(surface)) continue;

                var cost = -9000 - surface.Length * 100;
                var pos = entry.EntryType switch
                {
                    DeckDictionaryEntryType.Name => "名詞,固有名詞,人名,一般,*,*",
                    _ => "名詞,普通名詞,一般,*,*,*"
                };
                sb.Append(surface).Append(",5146,5146,").Append(cost).Append(',')
                  .Append(surface).Append(',')
                  .Append(pos).Append(',')
                  .Append(surface).Append(',')
                  .Append(surface)
                  .AppendLine(",*,*,*,*,*");
            }

            return sb.Length > 0 ? Encoding.UTF8.GetBytes(sb.ToString()) : null;
        }

        private static bool IsRiskyNameEntry(string surface) =>
            surface.Length <= 2 && WanaKana.IsHiragana(surface);
    }
}