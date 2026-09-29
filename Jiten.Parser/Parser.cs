using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.SpeechBoundaries;
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
    /// <summary>One resolved token in text order, before per-deck deduplication into DeckWords.</summary>
    public sealed record ParsedOccurrence(int WordId, byte ReadingIndex, string Surface);

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

        private static readonly Regex TokenCleanRegex = new(
                                                            @"[^a-zA-Z0-9\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FAF\uFF21-\uFF3A\uFF41-\uFF5A\uFF10-\uFF19\u3005．]",
                                                            RegexOptions.Compiled);

        private static readonly Regex SmallTsuLongVowelRegex = new(@"ッー", RegexOptions.Compiled);
        private static readonly Regex MultiLongVowelRegex = new(@"ー{2,}", RegexOptions.Compiled);
        private static readonly Regex DialogueRegex = new(@"[「『].{0,200}?[」』]", RegexOptions.Compiled | RegexOptions.Singleline);

        // Opening quotes/brackets start a fresh utterance; the next content token counts as sentence-initial.
        private static readonly HashSet<char> OpeningQuoteChars =
            ['「', '『', '（', '〈', '《', '【', '〔', '｢', '(', '“', '‘', '"', '\''];

        // Removed after repair stages, not in MorphologicalAnalyser: RepairLongVowelMisparses needs them for backward merges.
        private static readonly HashSet<string> MisparsesRemove =
        [
            "そ", "る", "ま", "ふ", "ち", "ほ", "す", "じ", "なさ", "い", "ぴ", "ふあ", "ぷ", "ちゅ", "にっ", "じら", "タ", "け", "イ", "イッ", "ほっ", "そっ",
            "ウー", "うー", "ううう", "うう", "ウウウウ", "ウウ", "ううっ", "かー", "ぐわー", "違", "タ", "ッ", "ニヒヒ"
        ];

        // Narrower than IsNumeralChar: runs containing 万/億/兆 must still shatter so those survive as standalone words.
        private const string NumeralKanji = "一二三四五六七八九十百千〇零";

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
            (1578010,2), (2028930,1), (2028930,2), (1579350, 6), (2821500, 2), (2821500, 3),
            (1592150, 2), (1467040, 6), (1311350, 1), (1175280, 1), (1578150, 4), (2029650, 0),
            (1154770, 1), (1401940, 2), (2264280,0), (2264280, 1), (1323350, 2), (1950890, 2),
            (1429010, 1), (2871946, 0), (2871946, 1), (2872080, 0), (2872080, 1),
            (1583240, 5), (2619520, 0), (1319210, 8),
            // Rare kana forms that surface only as fragments: うほー (禹歩), はむっ (鱧), や (矢), お (尾), ハハッ, だもの (駄物), てし (手四)
            (2545990, 1), (1575020, 2), (1537760, 2), (1485770, 1), (2029740, 0), (1753980, 1), (2847163, 1)
        ];

        public static async Task WarmupAsync(IDbContextFactory<JitenDbContext> contextFactory, Action<string>? log = null)
        {
            await EnsureInitializedAsync(contextFactory, log);
            var settings = ParserRuntimeSettings.Current;
            SudachiInterop.WarmPool(settings.SudachiConfigPath, settings.DictionaryPath, log);
        }

        public static async Task WaitForJmDictPrefillAsync(IDbContextFactory<JitenDbContext> contextFactory)
        {
            var runtime = await _parserRuntime.EnsureInitializedAsync(contextFactory);
            await runtime.JmDictPrefillTask;
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

            ReclassifyLeftoverPrefixChars(sentences);
            DbgDump("ReclassifyLeftoverPrefixChars");

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

            PinCompoundVerbTails(sentences, diagnostics);

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

        // Sentence-level archaic context markers; ぬ and べき are excluded as too common in modern formal prose.
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

        private static readonly HashSet<string> PersonHonorifics =
            [..TransitionRuleSets.HonorificSuffixes, "たん", "先生", "課長", "部長", "姉さん", "兄さん"];

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

                    // Katakana noun + person honorific is a foreign name, which Sudachi doesn't tag as a proper noun.
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

        /// <summary>An honorific-confirmed name (七海さん) re-joins its splits (七|海) document-wide; else counters win (二|条).</summary>
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

        /// <summary>Splits Xして only when X completes a noun compound with preceding nouns (物々 + 交換して → 物々交換 + して).</summary>
        private static void SplitSuruInflectionsForNounCompounding(List<SentenceInfo> sentences)
        {
            static string? GetSuruSplitSuffixPrefix(string surface)
            {
                // Kept small to avoid standalone auxiliary chains; prefix match also covers trailing auxiliaries (交換しておかない).
                if (surface.StartsWith("して", StringComparison.Ordinal)) return "して";
                if (surface.StartsWith("した", StringComparison.Ordinal)) return "した";
                if (surface.StartsWith("し", StringComparison.Ordinal)) return "し";
                // Passive/causative chains (指名手配されている, 強制させる) recover their compound the same way.
                if (surface.StartsWith("され", StringComparison.Ordinal)) return "され";
                if (surface.StartsWith("させ", StringComparison.Ordinal)) return "させ";
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
                        baseNoun = word.DictionaryForm[..^2];
                    }
                    else if (word.HasPartOfSpeechSection(PartOfSpeechSection.PossibleSuru))
                    {
                        // CombineVerbDependantsSuru keeps the noun stem as DictionaryForm (交換してる → 交換).
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

                    // Longest window first: up to 4 preceding nouns + baseNoun.
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

                    // Sudachi readings aren't preserved: merged tokens carry only the accumulator reading.
                    var nounWord = new WordInfo(word)
                                   {
                                       Text = baseNoun, PartOfSpeech = PartOfSpeech.Noun, DictionaryForm = baseNoun,
                                       NormalizedForm = string.IsNullOrEmpty(word.NormalizedForm) ? baseNoun : word.NormalizedForm,
                                       Reading = KanaConverter.ToHiragana(baseNoun, convertLongVowelMark: false)
                                   };

                    var suruWord = new WordInfo
                                   {
                                       // The auxiliary chain (しておかない) stays one token.
                                       Text = suffixSurface, PartOfSpeech = PartOfSpeech.Verb, DictionaryForm = "する", NormalizedForm = "する",
                                       Reading = KanaConverter.ToHiragana(suffixSurface, convertLongVowelMark: false)
                                   };

                    int baseLen = nounWord.Text.Length;
                    int suffixLen = suruWord.Text.Length;

                    sentence.Words[i] = (nounWord, position, baseLen);
                    sentence.Words.Insert(i + 1, (suruWord, position + baseLen, suffixLen));
                    i++;
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

        // Occurrences aren't counted here: ProcessSentencesToDeck recounts them and other callers discard them.
        private static List<WordInfo> CollectUniqueWordInfos(List<WordInfo> wordInfos)
        {
            var uniqueWords = new List<WordInfo>();
            var seen = new HashSet<(string text, PartOfSpeech pos, string dictionaryForm, string reading, bool isPersonNameContext, bool
                isNameLikeSudachiNoun, int? preMatchedWordId)>();

            foreach (var word in wordInfos)
            {
                var isNameLikeSudachiNoun = PosMapper.IsNameLikeSudachiNoun(word.PartOfSpeech, word.PartOfSpeechSection1,
                                                                            word.PartOfSpeechSection2, word.PartOfSpeechSection3);
                if (seen.Add((word.Text, word.PartOfSpeech, word.DictionaryForm, word.Reading, word.IsPersonNameContext,
                              isNameLikeSudachiNoun, word.PreMatchedWordId)))
                    uniqueWords.Add(word);
            }

            return uniqueWords;
        }

        private static async Task<List<(DeckWord? word, int? margin, List<FormCandidate>? candidates)>> ProcessWordsInBatches(
            List<WordInfo> words,
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

                Dictionary<DeckWordCacheKey, DeckWord?>? prefetchedCache = null;
                if (UseCache && diagnostics == null)
                {
                    try
                    {
                        var cacheKeys = new List<DeckWordCacheKey>(batch.Count);
                        foreach (var word in batch)
                        {
                            var wi = word;
                            var textWithoutBar = wi.Text.TrimEnd('ー');
                            if (textWithoutBar.Length > 0 && textWithoutBar.All(char.IsDigit) ||
                                (textWithoutBar.Length == 1 && textWithoutBar.IsAsciiOrFullWidthLetter()))
                                continue;

                            // Pinned tokens bypass the cache entirely (see hasPin in ProcessWord).
                            if (wi.PreMatchedWordId != null || wi.PreMatchedCandidateWordIds != null)
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
                    var surface = batch[j].Text;

                    if (dictionaryEntriesBySurface != null &&
                        dictionaryEntriesBySurface.TryGetValue(surface, out var dictEntry))
                    {
                        bool shouldOverride = !IsRiskyNameEntry(surface) || batch[j].IsPersonNameContext;
                        var overrideWord = shouldOverride ? dictEntry.EntryType switch
                        {
                            DeckDictionaryEntryType.Name =>
                                await ResolveAsNameEntry(surface, batchWordCache),
                            _ => null
                        } : null;

                        if (overrideWord != null)
                        {
                            allProcessedWords.Add((overrideWord, int.MaxValue, null));
                            continue;
                        }
                    }

                    // An all-kanji-numeral surface is a number, never a JMnedict name (二千, 四万); unlike NumeralKanji, 万 counts.
                    if (result.Word != null && surface.Length >= 2
                        && surface.All(c => NumeralKanji.Contains(c) || c is '万' or '億' or '兆')
                        && WordMeta.TryGetValue(result.Word.WordId, out var numeralNameMeta)
                        && numeralNameMeta.Pos.Length > 0
                        && numeralNameMeta.Pos.All(p => p is PartOfSpeech.Name or PartOfSpeech.Unknown))
                    {
                        allProcessedWords.Add((null, null, null));
                        continue;
                    }

                    allProcessedWords.Add((result.Word, result.Margin, result.FirstPassCandidates));
                }
            }

            return allProcessedWords;
        }

        private static async Task<DeckWord?> ResolveAsNameEntry(string surface,
                                                                 ConcurrentDictionary<int, JmDictWord>? batchWordCache)
        {
            if (!_lookups.TryGetValue(surface, out var candidates) || candidates.Count == 0)
                return null;

            var wordCache = await GetWordsWithCache(candidates, batchWordCache);

            // Lookups fold hiragana and katakana (ひなた hits ヒナタ-only names), so an exact spelling beats lookup order.
            DeckWord? fallback = null;
            foreach (var id in candidates)
            {
                if (!wordCache.TryGetValue(id, out var word)) continue;
                if (!word.CachedPOS.All(p => p is PartOfSpeech.Name or PartOfSpeech.Unknown)) continue;

                var readingIndex = GetBestReadingIndex(word, surface);
                if (readingIndex == 255) readingIndex = 0;

                var deckWord = new DeckWord
                {
                    WordId = word.WordId, OriginalText = surface, ReadingIndex = readingIndex,
                    PartsOfSpeech = [..word.CachedPOS], Origin = word.Origin
                };
                if (word.Forms.Any(f => f.ReadingIndex == readingIndex && f.Text == surface))
                    return deckWord;

                fallback ??= deckWord;
            }

            return fallback;
        }

        public static async Task<List<DeckWord>> ParseText(IDbContextFactory<JitenDbContext> contextFactory, string text,
                                                           bool preserveStopToken = false,
                                                           ParserDiagnostics? diagnostics = null)
        {
            await EnsureInitializedAsync(contextFactory);

            var (cleanText, furiganaHints) = FuriganaHintExtractor.Extract(text);

            var parser = new MorphologicalAnalyser { Interactive = true, HasCompoundLookup = HasLookupForCompound, HasNonNameCompoundLookup = HasNonNameLookup, HasPrioritizedNonNameCompoundLookup = HasPrioritizedNonNameLookup, HasKanaAppropriateCompoundLookup = HasKanaAppropriateLookup, HasSuruVerbCompoundLookup = HasSuruVerbLookup, GetNonNameCompoundWordId = GetNonNameCompoundId, GetNonNameCompoundFrequencyRank = GetBestNonNameFrequencyRank, HasVerbOrAdjectiveLookup = HasVerbOrAdjectiveLookup, HasExpressionLookup = HasExpressionLookup, HasCounterSenseLookup = HasCounterSenseAvailable };
            var (sentences, cleanedOriginal) = await parser.ParseWithCleanedOriginal(cleanText, preserveStopToken: preserveStopToken, diagnostics: diagnostics);

            // ComputeTokenOffsets strips \r\n, so relocate in the same coordinate space.
            var cleanedOriginalFlat = cleanedOriginal.Replace("\r", "").Replace("\n", "");
            FuriganaHint[]? relocatedHints = furiganaHints.Length > 0 && cleanedOriginalFlat.Length > 0
                ? FuriganaHintExtractor.RelocateToCleanedOriginal(cleanedOriginalFlat, furiganaHints, cleanText)
                : null;

            PreprocessSentences(sentences, diagnostics);
            MergeConfirmedNameSplits([sentences]);
            PropagatePersonNameContexts(sentences);
            var wordInfos = ExtractWordInfos(sentences);
            var processedWithMargins = await ProcessWordsInBatches(wordInfos, Deconjugator.Instance, diagnostics: diagnostics);

            var marginMap = BuildMarginMap(sentences, processedWithMargins);
            var candidateLookup = new Dictionary<(string, PartOfSpeech, string, string, bool, bool, int?), List<FormCandidate>>();

            if (ResegmentationEngine.TryResegmentLowConfidenceTokens(sentences, _lookups, _wordFrequencyRanks, marginMap,
                                                                    WordMeta, diagnostics))
            {
                diagnostics?.Results.Clear();

                var oldResultLookup = new Dictionary<(string, PartOfSpeech, string, string, bool, bool, int?), (DeckWord? word, int? margin)>();
                for (int i = 0; i < wordInfos.Count; i++)
                {
                    var key = GetDedupKey(wordInfos[i]);
                    oldResultLookup.TryAdd(key, (processedWithMargins[i].word, processedWithMargins[i].margin));
                    if (processedWithMargins[i].candidates != null)
                        candidateLookup.TryAdd(key, processedWithMargins[i].candidates!);
                }

                wordInfos = ExtractWordInfos(sentences);

                var newTokens = new List<WordInfo>();
                foreach (var wi in wordInfos)
                {
                    var key = GetDedupKey(wi);
                    if (!oldResultLookup.ContainsKey(key))
                        newTokens.Add(wi);
                }

                if (newTokens.Count > 0)
                {
                    var newResults = await ProcessWordsInBatches(newTokens, Deconjugator.Instance, diagnostics: diagnostics);
                    for (int i = 0; i < newTokens.Count; i++)
                    {
                        var key = GetDedupKey(newTokens[i]);
                        oldResultLookup.TryAdd(key, (newResults[i].word, newResults[i].margin));
                        if (newResults[i].candidates != null)
                            candidateLookup.TryAdd(key, newResults[i].candidates!);
                    }
                }

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

        public static async Task<Deck> ParseTextToDeck(IDbContextFactory<JitenDbContext> contextFactory, string text,
                                                       bool storeRawText = false,
                                                       bool predictDifficulty = true,
                                                       MediaType mediatype = MediaType.Novel,
                                                       ParserDiagnostics? diagnostics = null,
                                                       BenchmarkTimings? timings = null,
                                                       List<DeckDictionaryEntry>? dictionaryEntries = null,
                                                       byte[]? speechBoundaries = null)
        {
            var results = await ParseTextsToDeck(contextFactory, [text], storeRawText, predictDifficulty, mediatype, diagnostics, timings, dictionaryEntries,
                                                 speechBoundaries: [speechBoundaries]);
            return results.Count > 0 ? results[0] : new Deck();
        }

        public static async Task<List<Deck>> ParseTextsToDeck(IDbContextFactory<JitenDbContext> contextFactory,
                                                              List<string> texts,
                                                              bool storeRawText = false,
                                                              bool predictDifficulty = true,
                                                              MediaType mediatype = MediaType.Novel,
                                                              ParserDiagnostics? diagnostics = null,
                                                              BenchmarkTimings? timings = null,
                                                              List<DeckDictionaryEntry>? dictionaryEntries = null,
                                                              List<List<ParsedOccurrence>>? occurrenceSink = null,
                                                              IReadOnlyList<byte[]?>? speechBoundaries = null)
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

            // Subtitle lines rarely end in punctuation, so they are rejoined into one sentence per line before parsing.
            var parseTexts = texts;
            byte[]?[]? boundariesByText = null;
            if (MediaTypes.IsSubtitleSpeech(mediatype))
            {
                parseTexts = new List<string>(texts.Count);
                boundariesByText = new byte[texts.Count][];
                for (int i = 0; i < texts.Count; i++)
                {
                    var stored = speechBoundaries != null && i < speechBoundaries.Count ? speechBoundaries[i] : null;
                    var (assembled, boundaries) = SpeechTextAssembler.Prepare(texts[i], stored, SpeechBoundaryModel.Default);
                    parseTexts.Add(assembled);
                    boundariesByText[i] = boundaries;
                }
            }
            else if (mediatype == MediaType.YouTube)
            {
                parseTexts = new List<string>(texts.Count);
                boundariesByText = new byte[texts.Count][];
                for (int i = 0; i < texts.Count; i++)
                {
                    var (prepared, boundaries) = YouTubeSpeechText.Prepare(texts[i], SpeechBoundaryModel.YouTube);
                    parseTexts.Add(prepared);
                    boundariesByText[i] = boundaries;
                }
            }

            var cleanTexts = new List<string>(texts.Count);
            var hintsByText = new List<FuriganaHint[]>(texts.Count);
            foreach (var t in parseTexts)
            {
                var (clean, hints) = FuriganaHintExtractor.Extract(t);
                cleanTexts.Add(clean);
                hintsByText.Add(hints);
            }

            var timer = new Stopwatch();
            timer.Start();

            var parser = new MorphologicalAnalyser { HasCompoundLookup = HasLookupForCompound, HasNonNameCompoundLookup = HasNonNameLookup, HasPrioritizedNonNameCompoundLookup = HasPrioritizedNonNameLookup, HasKanaAppropriateCompoundLookup = HasKanaAppropriateLookup, HasSuruVerbCompoundLookup = HasSuruVerbLookup, GetNonNameCompoundWordId = GetNonNameCompoundId, GetNonNameCompoundFrequencyRank = GetBestNonNameFrequencyRank, HasVerbOrAdjectiveLookup = HasVerbOrAdjectiveLookup, HasExpressionLookup = HasExpressionLookup, HasCounterSenseLookup = HasCounterSenseAvailable };
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

                var subtitleSpeech = boundariesByText?[textIndex] != null;
                // Manga text holds one speech bubble per line and bubbles rarely end in punctuation.
                var profileLineBreaks = mediatype == MediaType.Manga ? FlatLineBreakOffsets(cleanedOriginals[textIndex]) : null;
                var deck = await ProcessSentencesToDeck(sentences, text, deconjugator, storeRawText, predictDifficulty, mediatype, subtitleSpeech, timings, dictionaryEntriesBySurface, relocated, rawCharCounts[textIndex], diagnostics, occurrenceSink, profileLineBreaks);
                if (deck.RawText != null && boundariesByText != null)
                    deck.RawText.SpeechBoundaries = boundariesByText[textIndex];
                decks.Add(deck);
                batchedSentences[textIndex] = null!;
            }

            return decks;
        }

        /// <summary>Line break positions counted in the text with line breaks removed, which is what word offsets index.</summary>
        private static List<int> FlatLineBreakOffsets(string text)
        {
            var offsets = new List<int>();
            int flatPos = 0;
            foreach (char c in text)
            {
                if (c == '\n')
                {
                    if (flatPos > 0 && (offsets.Count == 0 || offsets[^1] != flatPos))
                        offsets.Add(flatPos);
                }
                else if (c != '\r')
                {
                    flatPos++;
                }
            }

            return offsets;
        }

        private static async Task<Deck> ProcessSentencesToDeck(
            List<SentenceInfo> sentences,
            string text,
            Deconjugator deconjugator,
            bool storeRawText,
            bool predictDifficulty,
            MediaType mediatype,
            bool subtitleSpeech,
            BenchmarkTimings? timings = null,
            Dictionary<string, DeckDictionaryEntry>? dictionaryEntriesBySurface = null,
            FuriganaHint[]? relocatedHints = null,
            int? rawContentCharCount = null,
            ParserDiagnostics? diagnostics = null,
            List<List<ParsedOccurrence>>? occurrenceSink = null,
            IReadOnlyList<int>? profileLineBreaks = null)
        {
            var sw = timings != null ? Stopwatch.StartNew() : null;

            var wordInfos = ExtractWordInfos(sentences);
            var uniqueWords = CollectUniqueWordInfos(wordInfos);

            var allProcessedWithMargins = await ProcessWordsInBatches(uniqueWords, deconjugator, dictionaryEntriesBySurface: dictionaryEntriesBySurface);

            var resultLookup = new Dictionary<(string, PartOfSpeech, string, string, bool, bool, int?), (DeckWord? word, int? margin)>();
            var candidateLookup = new Dictionary<(string, PartOfSpeech, string, string, bool, bool, int?), List<FormCandidate>>();
            for (int i = 0; i < uniqueWords.Count; i++)
            {
                var key = GetDedupKey(uniqueWords[i]);
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
                var newUniqueWords = new List<WordInfo>();
                foreach (var wi in newWordInfos)
                {
                    var key = GetDedupKey(wi);
                    if (!resultLookup.ContainsKey(key))
                        newUniqueWords.Add(wi);
                }

                if (newUniqueWords.Count > 0)
                {
                    var deduped = CollectUniqueWordInfos(newUniqueWords);
                    var newResults = await ProcessWordsInBatches(deduped, deconjugator);
                    for (int i = 0; i < deduped.Count; i++)
                    {
                        var key = GetDedupKey(deduped[i]);
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

            // First-appearance order, first instance carries the total; manual loop avoids GroupBy's key allocations.
            var dedup = new Dictionary<(int, byte), DeckWord>(corrected.Count);
            var processedList = new List<DeckWord>(corrected.Count);
            foreach (var x in corrected)
            {
                var key = (x.WordId, x.ReadingIndex);
                if (dedup.TryGetValue(key, out var first))
                {
                    first.Occurrences++;
                }
                else
                {
                    x.Occurrences = 1;
                    dedup[key] = x;
                    processedList.Add(x);
                }
            }
            var processedWords = processedList.ToArray();

            processedWords = ExcludeFinalMisparses(processedWords, diagnostics).ToArray();

            if (occurrenceSink != null)
            {
                var kept = new HashSet<(int, byte)>(processedWords.Select(w => (w.WordId, w.ReadingIndex)));
                occurrenceSink.Add(corrected.Where(x => kept.Contains((x.WordId, x.ReadingIndex)))
                                          .Select(x => new ParsedOccurrence(x.WordId, x.ReadingIndex, x.OriginalText))
                                          .ToList());
            }

            List<ExampleSentence>? exampleSentences = null;

            if (subtitleSpeech || mediatype is MediaType.Novel or MediaType.NonFiction or MediaType.VideoGame or MediaType.VisualNovel or MediaType.WebNovel)
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
                    sentences, processedWords, formFreqRanks, _wordFrequencyRanks, subtitleSpeech);
            }

            var totalWordCount = processedWords.Select(w => w.Occurrences).Sum();

            var charCounts = new Dictionary<char, int>();
            int wordInfoCharCount = 0;
            foreach (var w in wordInfos)
            {
                wordInfoCharCount += w.Text.Length;
                foreach (var c in w.Text)
                    charCounts[c] = charCounts.GetValueOrDefault(c) + 1;
            }

            int uniqueKanjiCount = 0, uniqueKanjiUsedOnceCount = 0;
            foreach (var (c, count) in charCounts)
            {
                if (!JapaneseTextHelper.IsKanji(c)) continue;
                uniqueKanjiCount++;
                if (count == 1) uniqueKanjiUsedOnceCount++;
            }

            var characterCount = rawContentCharCount ?? wordInfoCharCount;

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
                           UniqueKanjiCount = uniqueKanjiCount, UniqueKanjiUsedOnceCount = uniqueKanjiUsedOnceCount,
                           SentenceCount = sentences.Count, DialoguePercentage = dialoguePercentage, DeckWords = processedWords,
                           RawText = storeRawText ? new DeckRawText(text) : null, ExampleSentences = exampleSentences,
                           SentenceProfile = SentenceProfileBuilder.Build(sentences, processedWords, profileLineBreaks)
                       };

            if (sw != null) timings!.StatsBuildMs += sw.Elapsed.TotalMilliseconds;

            return deck;
        }

        public static async Task<List<DeckWord?>> ParseMorphenes(IDbContextFactory<JitenDbContext> contextFactory, string text,
                                                                 ParserDiagnostics? diagnostics = null)
        {
            await EnsureInitializedAsync(contextFactory);

            var parser = new MorphologicalAnalyser { HasCompoundLookup = HasLookupForCompound, HasNonNameCompoundLookup = HasNonNameLookup, HasPrioritizedNonNameCompoundLookup = HasPrioritizedNonNameLookup, HasKanaAppropriateCompoundLookup = HasKanaAppropriateLookup, HasSuruVerbCompoundLookup = HasSuruVerbLookup, GetNonNameCompoundWordId = GetNonNameCompoundId, GetNonNameCompoundFrequencyRank = GetBestNonNameFrequencyRank, HasVerbOrAdjectiveLookup = HasVerbOrAdjectiveLookup, HasExpressionLookup = HasExpressionLookup, HasCounterSenseLookup = HasCounterSenseAvailable };
            var sentences = await parser.Parse(text, morphemesOnly: true, diagnostics: diagnostics);
            var wordInfos = sentences.SelectMany(s => s.Words).Select(w => w.word).ToList();

            wordInfos.ForEach(x => x.Text = SmallTsuLongVowelRegex.Replace(x.Text, ""));
            var processedWithMargins = await ProcessWordsInBatches(wordInfos, Deconjugator.Instance, batchSize: 5000);

            return processedWithMargins
                   .Select(p => p.word)
                   .Where(w => w == null || !ExcludedMisparses.Contains((w.WordId, w.ReadingIndex)))
                   .ToList();
        }

        /// <summary>Raw Sudachi Mode A (no userdic) morphemes per text; no JMDict resolution or recombination.</summary>
        public static async Task<List<List<WordInfo>>> GetMorphemesBatch(
            IDbContextFactory<JitenDbContext> contextFactory, List<string> texts)
        {
            await EnsureInitializedAsync(contextFactory);
            // morphemesOnly skips RunPipeline, so the Has*CompoundLookup delegates are never invoked.
            var analyser = new MorphologicalAnalyser();
            var batches = await analyser.ParseBatch(texts, morphemesOnly: true);
            return batches
                   .Select(sentences => sentences.SelectMany(s => s.Words).Select(w => w.word).ToList())
                   .ToList();
        }

        private static async Task<Dictionary<int, JmDictWord>> GetWordsWithCache(
            IEnumerable<int> wordIds,
            ConcurrentDictionary<int, JmDictWord>? batchCache)
        {
            if (batchCache == null)
            {
                // PriorityOverrides are applied once at insert into the in-process JmDict cache.
                return await JmDictCache.GetWordsAsync(wordIds);
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
                    result[id] = word;
                    batchCache.TryAdd(id, word);
                }
            }

            return result;
        }

        /// <summary>First-seen order; `first` must already be duplicate-free (CollectIds output is).</summary>
        private static List<int> AppendDistinct(List<int> first, List<int> second)
        {
            var seen = new HashSet<int>(first);
            var merged = new List<int>(first.Count + second.Count);
            merged.AddRange(first);
            foreach (var id in second)
                if (seen.Add(id))
                    merged.Add(id);
            return merged;
        }

        private static readonly SemaphoreSlim _processSemaphore = new SemaphoreSlim(100, 100);

        private enum ProcessWordStatus
        {
            Resolved,
            FilteredOut,
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
            public static ProcessWordResult Unresolved { get; } = new(ProcessWordStatus.Unresolved);
        }

        private static async Task<ProcessWordResult> ProcessWord(WordInfo wordInfo, Deconjugator deconjugator,
                                                                 Dictionary<DeckWordCacheKey, DeckWord?>? prefetchedCache = null,
                                                                 ParserDiagnostics? diagnostics = null,
                                                                 ConcurrentDictionary<int, JmDictWord>? batchWordCache = null)
        {

            await _processSemaphore.WaitAsync();

            try
            {
                var textWithoutBar = wordInfo.Text.TrimEnd('ー');
                if (textWithoutBar.Length > 0 && textWithoutBar.All(char.IsDigit) ||
                    (textWithoutBar.Length == 1 && textWithoutBar.IsAsciiOrFullWidthLetter()))
                {
                    return ProcessWordResult.FilteredOut;
                }

                var isNameLikeSudachiNoun = PosMapper.IsNameLikeSudachiNoun(
                                                                            wordInfo.PartOfSpeech,
                                                                            wordInfo.PartOfSpeechSection1,
                                                                            wordInfo.PartOfSpeechSection2,
                                                                            wordInfo.PartOfSpeechSection3);

                var cacheKey = new DeckWordCacheKey(
                                                    wordInfo.Text,
                                                    wordInfo.PartOfSpeech,
                                                    wordInfo.DictionaryForm,
                                                    wordInfo.Reading,
                                                    wordInfo.IsPersonNameContext,
                                                    isNameLikeSudachiNoun
                                                   );

                // The cache key has no pin context (帽子のツバ→鍔 and ツバを飲む→唾 share a key), so pins skip the cache.
                bool hasPin = wordInfo.PreMatchedWordId != null
                              || wordInfo.PreMatchedCandidateWordIds != null;

                if (UseCache && diagnostics == null && !hasPin)
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
                            var resolved = new DeckWord
                                           {
                                               WordId = cachedWord.WordId, OriginalText = wordInfo.Text,
                                               ReadingIndex = cachedWord.ReadingIndex,
                                               PartsOfSpeech = cachedWord.PartsOfSpeech, Origin = cachedWord.Origin,
                                               SudachiReading = wordInfo.Reading,
                                               SudachiPartOfSpeech = wordInfo.PartOfSpeech
                                           };
                            resolved.CopyConjugationsFrom(cachedWord);
                            return ProcessWordResult.FromResolved(resolved, margin: cachedWord.CachedMargin);
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

                // Captured before the escalation chain rewrites the POS; gates POS-relaxed lookups for exclamations.
                wordInfo.IsKanaExclamation =
                    wordInfo.PartOfSpeech is PartOfSpeech.Interjection or PartOfSpeech.Filler
                    && JapaneseTextHelper.IsAllKana(wordInfo.Text);
                wordInfo.IsKatakanaNounSurface =
                    wordInfo.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                    && JapaneseTextHelper.IsAllKatakana(wordInfo.Text);
                bool isProcessed = false;
                int attemptCount = 0;
                const int maxAttempts = 3;

                var baseWord = wordInfo.Text;
                do
                {
                    attemptCount++;
                    try
                    {
                        // A resegmentation pin also carries PreMatchedCandidateWordIds and goes through the full scorer.
                        if (wordInfo.PreMatchedWordId.HasValue
                            && wordInfo.PreMatchedCandidateWordIds == null)
                        {
                            var preMatched = await TryResolvePreMatched(wordInfo, batchWordCache);
                            if (preMatched != null)
                            {
                                processedWord = preMatched;
                                resolvedMargin = ScoringPolicy.HighConfidenceThreshold;
                                break;
                            }
                        }

                        if (wordInfo.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective or PartOfSpeech.Auxiliary
                                or PartOfSpeech.NaAdjective or PartOfSpeech.Expression ||
                            wordInfo.PartOfSpeechSection1 is PartOfSpeechSection.Adjectival)
                        {
                            var verbResult = await DeconjugateVerbOrAdjective(wordInfo, deconjugator, diagnostics, batchWordCache);
                            if (!verbResult.success || verbResult.word == null)
                            {
                                // A noun misparsed as a verb/adjective (お祭り).
                                var nounResult = await DeconjugateWord(wordInfo, diagnostics, batchWordCache);
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
                            var nounResult = await DeconjugateWord(wordInfo, diagnostics, batchWordCache);
                            if (!nounResult.success || nounResult.word == null)
                            {
                                var verbResult = await ResolveByPosEscalation(wordInfo, deconjugator, diagnostics, batchWordCache);
                                processedWord = verbResult.word;
                                resolvedMargin = verbResult.margin;
                                firstPassCandidates = verbResult.candidates;
                            }
                            else if (wordInfo.PartOfSpeech is PartOfSpeech.Pronoun or PartOfSpeech.Conjunction
                                     or PartOfSpeech.Interjection or PartOfSpeech.Particle or PartOfSpeech.Adverb
                                     or PartOfSpeech.NaAdjective or PartOfSpeech.Suffix or PartOfSpeech.NounSuffix
                                     or PartOfSpeech.PrenounAdjectival or PartOfSpeech.Prefix)
                            {
                                processedWord = nounResult.word;
                                resolvedMargin = nounResult.margin;
                                firstPassCandidates = nounResult.candidates;
                            }
                            else
                            {
                                List<FormCandidate>? arbitrationCandidates;
                                (processedWord, resolvedMargin, arbitrationCandidates) = await ArbitrateNounVsVerbStem(
                                    wordInfo, nounResult, isNameLikeSudachiNoun, deconjugator, diagnostics, batchWordCache);
                                if (firstPassCandidates == null && processedWord != null)
                                    firstPassCandidates = arbitrationCandidates;
                            }
                        }

                        if (processedWord != null && IsKanaCollisionResolution(processedWord, wordInfo, baseWord))
                        {
                            processedWord = null;
                            resolvedMargin = null;
                            firstPassCandidates = null;
                        }

                        if (processedWord != null)
                        {
                            if (wordInfo.Text != baseWord)
                                diagnostics?.LogParserEvent(
                                    "ProcessWord", "variant-resolved", [baseWord], [wordInfo.Text],
                                    $"resolved after fallback text mutation (WordId={processedWord.WordId})");

                            processedWord.OriginalText = baseWord;
                            break;
                        }

                        var mutatedText = NextSurfaceMutation(wordInfo, baseWord);
                        if (mutatedText != null)
                            wordInfo.Text = mutatedText;
                        else
                            isProcessed = true;

                        if (attemptCount >= maxAttempts)
                        {
                            isProcessed = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Error] Failed to process word '{wordInfo.Text}': {ex.Message}");
                        isProcessed = true;
                    }
                } while (!isProcessed);

                // WordInfo is shared with example sentence extraction and diagnostics.
                wordInfo.Text = baseWord;

                if (processedWord != null)
                {
                    var geminationRetry = await TryGeminationCollapseRetry(wordInfo, processedWord, baseWord,
                                                                           deconjugator, diagnostics, batchWordCache);
                    if (geminationRetry != null)
                        (processedWord, resolvedMargin, firstPassCandidates) = geminationRetry.Value;
                }

                if (processedWord == null)
                {
                    diagnostics?.RunSummary.IncrementUnresolvedTokenCount();
                    return ProcessWordResult.Unresolved;
                }

                processedWord.SudachiReading = wordInfo.Reading;
                processedWord.SudachiPartOfSpeech = wordInfo.PartOfSpeech;

                var candidatesToKeep = !ScoringPolicy.IsHighConfidence(resolvedMargin)
                    ? firstPassCandidates
                    : null;

                if (!UseCache || hasPin)
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

        private static async Task<DeckWord?> TryResolvePreMatched(WordInfo wordInfo,
                                                                  ConcurrentDictionary<int, JmDictWord>? batchWordCache)
        {
            var preMatchedWordId = wordInfo.PreMatchedWordId!.Value;
            var wordCache = await GetWordsWithCache([preMatchedWordId], batchWordCache);
            if (!wordCache.TryGetValue(preMatchedWordId, out var preMatchedWord))
                return null;

            // Surface first: DictionaryForm may be a kanji homograph set only for cache-keying (いける→生ける).
            // DictionaryForm is the fallback when the surface isn't a form of the word (食べた→食べる).
            var surface = wordInfo.Text;
            byte readingIndex;
            if (wordInfo.PreMatchedReadingIndex.HasValue)
            {
                readingIndex = wordInfo.PreMatchedReadingIndex.Value;
            }
            else
            {
                readingIndex = GetBestReadingIndex(preMatchedWord, surface, wordInfo.Reading);
                if (readingIndex == 255 && !string.IsNullOrEmpty(wordInfo.DictionaryForm)
                    && wordInfo.DictionaryForm != surface)
                    readingIndex = GetBestReadingIndex(preMatchedWord, wordInfo.DictionaryForm, wordInfo.Reading);
                // Mixed-script pins (憎みあう) match no written form: try NormalizedForm (憎み合う), then reading 0, never 255.
                // Skipped when (WordId, 0) is an excluded misparse, which the filter would silently drop.
                if (readingIndex == 255 && !string.IsNullOrEmpty(wordInfo.NormalizedForm)
                    && wordInfo.NormalizedForm != surface
                    && wordInfo.NormalizedForm != wordInfo.DictionaryForm)
                    readingIndex = GetBestReadingIndex(preMatchedWord, wordInfo.NormalizedForm, wordInfo.Reading);
                if (readingIndex == 255 && !ExcludedMisparses.Contains((preMatchedWordId, (byte)0)))
                    readingIndex = 0;
            }

            return new DeckWord
                   {
                       WordId = preMatchedWordId, ReadingIndex = readingIndex,
                       OriginalText = wordInfo.Text,
                       Conjugations = wordInfo.PreMatchedConjugations ?? [],
                       PartsOfSpeech = [..preMatchedWord.CachedPOS], Origin = preMatchedWord.Origin
                   };
        }

        // Retry POS order when Sudachi's POS resolved nothing; its mistags (らしく as noun, 朧気 as name) resolve under another.
        private static readonly (PartOfSpeech pos, bool directLookupFirst, bool deconjugateAfter)[] PosEscalationLadder =
        [
            (PartOfSpeech.Verb, false, true),
            (PartOfSpeech.IAdjective, false, true),
            (PartOfSpeech.NaAdjective, true, true),
            (PartOfSpeech.Interjection, true, false),
        ];

        private static async Task<(bool success, DeckWord? word, int? margin, List<FormCandidate>? candidates)>
            ResolveByPosEscalation(WordInfo wordInfo, Deconjugator deconjugator,
                                   ParserDiagnostics? diagnostics, ConcurrentDictionary<int, JmDictWord>? batchWordCache)
        {
            var verbResult = await DeconjugateVerbOrAdjective(wordInfo, deconjugator, diagnostics, batchWordCache);

            var oldPos = wordInfo.PartOfSpeech;
            foreach (var (pos, directLookupFirst, deconjugateAfter) in PosEscalationLadder)
            {
                if (verbResult is { success: true, word: not null })
                    break;

                wordInfo.PartOfSpeech = pos;
                if (directLookupFirst)
                {
                    var direct = await DeconjugateWord(wordInfo, diagnostics, batchWordCache);
                    if (direct is { success: true, word: not null })
                    {
                        verbResult = (true, direct.word, direct.margin, direct.candidates);
                        continue;
                    }
                }

                if (deconjugateAfter)
                    verbResult = await DeconjugateVerbOrAdjective(wordInfo, deconjugator, diagnostics, batchWordCache);
            }

            wordInfo.PartOfSpeech = oldPos;
            return verbResult;
        }

        // Noun-tagged tokens may be verb stems (抱え: rare noun "armful" or 連用形 of 抱える), so both readings compete.
        private static async Task<(DeckWord? word, int? margin, List<FormCandidate>? candidates)> ArbitrateNounVsVerbStem(
            WordInfo wordInfo,
            (bool success, DeckWord? word, int? margin, List<FormCandidate>? candidates) nounResult,
            bool isNameLikeSudachiNoun, Deconjugator deconjugator,
            ParserDiagnostics? diagnostics, ConcurrentDictionary<int, JmDictWord>? batchWordCache)
        {
            DeckWord? processedWord;
            int? resolvedMargin;

            var savedPos = wordInfo.PartOfSpeech;
            wordInfo.PartOfSpeech = PartOfSpeech.Verb;
            var verbFallback = await DeconjugateVerbOrAdjective(wordInfo, deconjugator, diagnostics, batchWordCache);
            wordInfo.PartOfSpeech = savedPos;

            // Bare ichidan stems have no ending to deconjugate, so look up surface+る directly (抱え → 抱える).
            if (nounResult.word != null &&
                (!verbFallback.success || verbFallback.word == null ||
                 verbFallback.word.WordId == nounResult.word.WordId))
            {
                var surfaceHira = KanaConverter.ToHiragana(wordInfo.Text,
                                                           convertLongVowelMark: false);
                var ruText = surfaceHira + "る";
                if (_lookups.TryGetValue(ruText, out List<int>? ruIds))
                {
                    var ruWordCache = await GetWordsWithCache(ruIds, batchWordCache);
                    bool isKanaForStem = JapaneseTextHelper.IsAllKana(wordInfo.Text);
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
                        // The stem (抱え) isn't one of 抱える's forms, so match the kana form minus る.
                        var stemHira = KanaConverter.ToHiragana(wordInfo.Text, convertLongVowelMark: false);
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
                                WordId = bestV1.WordId, OriginalText = wordInfo.Text,
                                ReadingIndex = stemReadingIndex,
                                Conjugations = ["continuative"], PartsOfSpeech = [..bestV1.CachedPOS],
                                Origin = bestV1.Origin
                            }, (int?)null, (List<FormCandidate>?)null);
                    }
                }
            }

            bool nounIsPureNameEntry = nounResult.word != null &&
                                       nounResult.word.PartsOfSpeech.All(p => p is PartOfSpeech.Name
                                                                             or PartOfSpeech.Unknown);
            if ((wordInfo.IsPersonNameContext || isNameLikeSudachiNoun) && nounIsPureNameEntry)
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
                    bool isKana = JapaneseTextHelper.IsAllKana(wordInfo.Text);

                    // Same word-level scorers as the main scorer so archaic penalties and copula boosts agree.
                    int NounVerbScore(JmDictWord word, byte readingIndex)
                    {
                        var form = word.Forms.FirstOrDefault(f => (byte)f.ReadingIndex == readingIndex)
                                   ?? word.Forms.FirstOrDefault();
                        if (form == null) return 0;
                        var candidate = new FormCandidate(word, form, readingIndex, form.Text, null);
                        return WordPriorityScorer.Score(candidate, isNameContext: false, isArchaicSentence: false,
                                                        ArchaicPosTypes)
                               + EntryPriorityScorer.Score(candidate);
                    }

                    var reading = wordInfo.Reading;
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
                        // A function-word-only verb side can't realize a noun token (バッカ stays 馬鹿, not particle ばかり).
                        bool verbIsFunctionWordOnly = verbEntry.CachedPOS.All(
                            p => p is PartOfSpeech.Particle or PartOfSpeech.Conjunction
                                or PartOfSpeech.Auxiliary or PartOfSpeech.Unknown);
                        if (verbIsFunctionWordOnly && wordInfo.PartOfSpeech == PartOfSpeech.Noun)
                        {
                            processedWord = nounResult.word;
                            resolvedMargin = nounResult.margin;
                        }
                        else if (nounIsAdji && ScoringPolicy.IsHighConfidence(nounResult.margin))
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
                        // A stem-only verb (でき → 出来) needs a margin, larger over a confident noun (うえ → 上, not 飢える).
                        int stemThreshold = ScoringPolicy.IsHighConfidence(nounResult.margin) ? 30 : 15;
                        int nounEvidence = NounVerbScore(nounEntry, nounResult.word.ReadingIndex);
                        int verbEvidence = NounVerbScore(verbEntry, verbFallback.word.ReadingIndex);
                        // A standalone suru-noun is complete (真似+できない); its verb homograph 真似る needs more evidence.
                        if (nounEntry.PartsOfSpeech.Contains("vs"))
                            nounEvidence += 40;
                        // Sudachi's own-entry noun (備え) holds when nf <= 20; rarer nouns (抱え, nf23) yield to the verb.
                        if (wordInfo.DictionaryForm == wordInfo.Text)
                        {
                            var nounNf = (nounEntry.Priorities ?? [])
                                .FirstOrDefault(p => p.StartsWith("nf", StringComparison.Ordinal));
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

            var candidates = processedWord == null ? null
                : processedWord.WordId == nounResult.word?.WordId
                    ? nounResult.candidates
                    : verbFallback.candidates;
            return (processedWord, resolvedMargin, candidates);
        }

        // Reading-key collisions the surface can't support: イエイ → 遺影, ひゅーん → 庇陰, てりゃあ → テリア.
        private static bool IsKanaCollisionResolution(DeckWord processedWord, WordInfo wordInfo, string baseWord)
        {
            if (wordInfo.IsKanaExclamation
                && !IsKanaAppropriateId(processedWord.WordId)
                && !(_lookups.TryGetValue(wordInfo.Text, out var exclDirectIds)
                     && exclDirectIds.Contains(processedWord.WordId)))
                return true;

            if (!wordInfo.IsKanaExclamation
                && wordInfo.Text != baseWord && JapaneseTextHelper.IsAllKana(baseWord)
                && !IsKanaAppropriateId(processedWord.WordId)
                && !(_lookups.TryGetValue(baseWord, out var baseDirectIds)
                     && baseDirectIds.Contains(processedWord.WordId)))
                return true;

            if (wordInfo.Text != baseWord
                && IsHiraganaSurface(baseWord)
                && WordMeta.TryGetValue(processedWord.WordId, out var mutatedMeta)
                && mutatedMeta.Origin == WordOrigin.Gairaigo
                && !(_lookups.TryGetValue(baseWord, out var gairaigoDirectIds)
                     && gairaigoDirectIds.Contains(processedWord.WordId)))
                return true;

            return false;
        }

        // One mutation per attempt, in priority order; null when none applies.
        private static string? NextSurfaceMutation(WordInfo wordInfo, string baseWord)
        {
            // A trailing small vowel is stretching in hiragana (なんちゃってぇ) but identity in katakana (ソフィ, not ソフ).
            if (wordInfo.Text.Length > 2 &&
                (wordInfo.Text[^1] is 'っ' or 'ー' or 'ぁ' or 'ぃ' or 'ぅ' or 'ぇ' or 'ぉ' ||
                 (wordInfo.Text[^1] is 'ァ' or 'ィ' or 'ゥ' or 'ェ' or 'ォ'
                      && !JapaneseTextHelper.IsAllKatakana(wordInfo.Text)) ||
                 wordInfo.Text[^2] == wordInfo.Text[^1]))
            {
                return wordInfo.Text[..^1];
            }

            if (wordInfo.Text.StartsWith('お') || wordInfo.Text.StartsWith('御'))
            {
                return wordInfo.Text[1..];
            }

            // Before the blanket strips, which eat geminates: drop only echoing small vowels (たぁっぷり), not ふぁ or katakana ソフィ.
            if (!JapaneseTextHelper.IsAllKatakana(wordInfo.Text)
                && TryRemoveEchoedSmallVowels(wordInfo.Text, out var echoStripped))
            {
                return echoStripped;
            }

            if (wordInfo.Text.Contains('ー'))
            {
                return wordInfo.Text.Replace("ー", "");
            }

            if (wordInfo.Text.Contains('っ') || wordInfo.Text.Contains('ッ'))
            {
                return baseWord.Replace("っ", "").Replace("ッ", "");
            }

            if (wordInfo.Text.Contains('ゃ') || wordInfo.Text.Contains('ゅ') ||
                wordInfo.Text.Contains('ょ') || wordInfo.Text.Contains('ぁ') ||
                wordInfo.Text.Contains('ぃ') || wordInfo.Text.Contains('ぅ') ||
                wordInfo.Text.Contains('ぇ') || wordInfo.Text.Contains('ぉ'))
            {
                return baseWord.Replace("ゃ", "").Replace("ゅ", "")
                               .Replace("ょ", "").Replace("ぁ", "")
                               .Replace("ぃ", "").Replace("ぅ", "")
                               .Replace("ぇ", "").Replace("ぉ", "")
                               .Replace("っ", "").Replace("ッ", "");
            }

            // Sudachi fuses adv-to words with と (凛と, 毅然と); JMDict has only the base form.
            if (wordInfo.PartOfSpeech == PartOfSpeech.Adverb &&
                wordInfo.Text.Length > 1 &&
                wordInfo.Text[^1] == 'と')
            {
                return wordInfo.Text[..^1];
            }

            return null;
        }

        // A junk collision via emphatic sokuon (ばっかな → 幕下) retries without it; kana-appropriate hits (ばっか = ばかり) stay.
        private static async Task<(DeckWord word, int? margin, List<FormCandidate>? candidates)?> TryGeminationCollapseRetry(
            WordInfo wordInfo, DeckWord processedWord, string baseWord,
            Deconjugator deconjugator, ParserDiagnostics? diagnostics,
            ConcurrentDictionary<int, JmDictWord>? batchWordCache)
        {
            if (baseWord.Length < 3 || !JapaneseTextHelper.IsAllKana(baseWord)
                || IsKanaAppropriateId(processedWord.WordId) || HasPrioritizedMeta(processedWord.WordId))
                return null;

            var desokuon = RemoveInternalSokuon(baseWord);
            if (desokuon == baseWord || desokuon.Length < 2)
                return null;

            var retryInfo = new WordInfo(wordInfo) { Text = desokuon };
            var retry = await DeconjugateVerbOrAdjective(retryInfo, deconjugator, diagnostics, batchWordCache);
            if (retry.word == null)
                retry = await DeconjugateWord(retryInfo, diagnostics, batchWordCache);
            if (retry.word != null
                && (IsKanaAppropriateId(retry.word.WordId) || HasPrioritizedMeta(retry.word.WordId)))
            {
                retry.word.OriginalText = baseWord;
                return (retry.word, retry.margin, retry.candidates);
            }

            return null;
        }

        // Mirrors DeconjugateVerbOrAdjective's form fallback; when it can resolve, the reading channel must stay out.
        private static bool DictOrNormalizedFormHasLookup(WordInfo wordInfo)
        {
            if (!string.IsNullOrEmpty(wordInfo.DictionaryForm))
            {
                var dictHiragana = KanaConverter.ToHiragana(wordInfo.DictionaryForm.Replace("ゎ", "わ").Replace("ヮ", "わ"),
                                                            convertLongVowelMark: false);
                if (_lookups.ContainsKey(dictHiragana) || _lookups.ContainsKey(wordInfo.DictionaryForm))
                    return true;
            }

            if (!string.IsNullOrEmpty(wordInfo.NormalizedForm)
                && !NormalizedFormIntroducesKanji(wordInfo.Text, wordInfo.NormalizedForm))
            {
                var normalizedHiragana = KanaConverter.ToHiragana(wordInfo.NormalizedForm, convertLongVowelMark: false);
                if (_lookups.ContainsKey(normalizedHiragana) || _lookups.ContainsKey(wordInfo.NormalizedForm))
                    return true;
            }

            return false;
        }

        // Okurigana rewritten into extra kanji is another lexeme (屈し ≠ 屈指); 敲き → 叩く and チックショー → 畜生 are fine.
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
            WordInfo wordInfo,
            ParserDiagnostics? diagnostics = null,
            ConcurrentDictionary<int, JmDictWord>? batchWordCache = null)
        {
            string text = wordInfo.Text;

            var textWithoutBar = text.TrimEnd('ー');
            if ((textWithoutBar.Length > 0 && textWithoutBar.All(char.IsDigit)) || (text.Length == 1 && text.IsAsciiOrFullWidthLetter()))
            {
                return (false, null, null, null);
            }

            var textInHiragana = KanaConverter.ToHiragana(wordInfo.Text, convertLongVowelMark: false);
            List<int>? candidates;
            bool isStripped = false;
            string textStripped = "";
            string? desokuonText = null;

            if (wordInfo.PreMatchedCandidateWordIds is { Count: > 0 } constrainedIds)
            {
                candidates = new List<int>(constrainedIds);
            }
            else
            {
                // Kana surfaces reaching a word only via long-vowel rewrites need a kana-appropriate word (イエーイ ≠ 遺影).
                Func<int, bool>? normalizedTierGate = JapaneseTextHelper.IsAllKana(text) ? IsKanaAppropriateId : null;

                var collected = LookupCandidateCollector.CollectIds(_lookups, text,
                                                                    includeKanaNormalized: true, includeLongVowelStripped: true,
                                                                    normalizedTierGate: normalizedTierGate);

                // NormalizedForm catches variants: チックショー → チクショー (畜生).
                if (!string.IsNullOrEmpty(wordInfo.NormalizedForm) &&
                    wordInfo.NormalizedForm != text &&
                    !NormalizedFormIntroducesKanji(text, wordInfo.NormalizedForm))
                {
                    var normalizedCollected = LookupCandidateCollector.CollectIds(_lookups, wordInfo.NormalizedForm,
                                                                                  includeKanaNormalized: true,
                                                                                  includeLongVowelStripped: true,
                                                                                  normalizedTierGate: normalizedTierGate);
                    if (normalizedCollected.Count > 0)
                        collected = AppendDistinct(collected, normalizedCollected);
                }

                // Emphatic-sokuon kana (バッカ = 馬鹿) also tries the de-sokuon key; priority-less kanji collisions (麦価) then drop.
                if (text.Length >= 3 && JapaneseTextHelper.IsAllKana(text))
                {
                    var desokuon = RemoveInternalSokuon(text);
                    if (desokuon != text && desokuon.Length >= 2)
                    {
                        var desokuonCollected = LookupCandidateCollector.CollectIds(_lookups, desokuon,
                                                                                    includeKanaNormalized: true,
                                                                                    includeLongVowelStripped: false,
                                                                                    normalizedTierGate: IsKanaAppropriateId);
                        if (desokuonCollected.Count > 0)
                        {
                            desokuonText = desokuon;
                            collected = AppendDistinct(
                                collected.Where(id => IsKanaAppropriateId(id) || HasPrioritizedMeta(id)).ToList(),
                                desokuonCollected);
                        }
                    }
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
                candidates.Sort();

                Dictionary<int, JmDictWord> wordCache;
                try
                {
                    wordCache = await GetWordsWithCache(candidates, batchWordCache);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error retrieving word cache: {ex.Message}");
                    return (false, null, null, null);
                }

                if (wordCache.Count == 0)
                {
                    return (false, null, null, null);
                }

                // A katakana hit bars words with no katakana form (カモン ≠ 家紋, フル ≠ 降る); 懐炉/カイロ still competes.
                if (wordInfo.PreMatchedCandidateWordIds is not { Count: > 0 }
                    && KanaScoringHelpers.IsPureKatakanaToken(text)
                    && _lookups.TryGetValue(text, out var directKatakanaIds))
                {
                    // Script is evidence for content words only: katakana function words are colloquial spelling (部屋ン中 = 部屋の中).
                    bool isFunctionWordToken = wordInfo.PartOfSpeech
                        is PartOfSpeech.Particle or PartOfSpeech.Auxiliary;

                    bool hasUsableDirectCandidate = !isFunctionWordToken && directKatakanaIds.Any(id =>
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
                                                                             wordInfo.PartOfSpeech,
                                                                             wordInfo.PartOfSpeechSection1,
                                                                             wordInfo.PartOfSpeechSection2,
                                                                             wordInfo.PartOfSpeechSection3);

                bool isKanaSurfaceToken = JapaneseTextHelper.IsAllKana(text);

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

                    // Entries with both name and non-name tags count as non-name.
                    bool isPureNameEntry = !hasNonNamePos && posList.Contains(PartOfSpeech.Name);

                    // Stripped surfaces allow the interjection fallback (よー, こーら).
                    bool compatible = PosMapper.IsJmDictCompatibleWithSudachi(
                                                                              word.CachedPOS,
                                                                              wordInfo.PartOfSpeech,
                                                                              allowInterjectionFallback: isStripped,
                                                                              allowNounExpressionFallback: isKanaSurfaceToken
                                                                                  && isNameLikeSudachiNoun);

                    if (compatible && hasNonNamePos)
                    {
                        compatibleNonNameMatches.Add(word);
                        continue;
                    }

                    if (isPureNameEntry && (wordInfo.IsPersonNameContext || isNameLikeSudachiNoun))
                    {
                        nameCandidates.Add(word);
                    }
                }

                List<JmDictWord> candidatePool;
                bool isNameContext;

                if (wordInfo.IsPersonNameContext && nameCandidates.Count > 0)
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
                    // POS-relaxed fallback admits any non-name entry, except kana exclamations into kanji words (イエイ → 遺影).
                    bool requireKanaAppropriate = wordInfo.IsKanaExclamation;
                    foreach (var id in candidates)
                    {
                        if (!wordCache.TryGetValue(id, out var word)) continue;
                        // Exact-surface forms are real spellings, not collisions (あなた → 貴方, ファルマ → name).
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

                        // Hiragana reaching a name only via the katakana fold is onomatopoeia (ひゅーん ≠ ヒューン).
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

                // Surviving Suffix POS means attached to a noun: pure suffixes (め=奴, ら, ども) beat noun/suffix hybrids like 目.
                if (wordInfo.PartOfSpeech == PartOfSpeech.Suffix && !isNameContext)
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

                    // Gemination fallback words match only via the de-sokuon text (バッカ → バカ → 馬鹿).
                    if (desokuonText != null)
                    {
                        var desokuonHira = KanaConverter.ToHiragana(desokuonText, convertLongVowelMark: false);
                        if (desokuonHira != textInHiragana)
                            allFormCandidates.AddRange(
                                FormCandidateFactory.EnumerateCandidateForms(word, desokuonHira, allowLooseLvmMatch: true,
                                                                             surface: desokuonText));
                    }

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
                                                               wordInfo.DictionaryForm, wordInfo.NormalizedForm,
                                                               isNameContext,
                                                               diagnostics,
                                                               sudachiReading: wordInfo.Reading,
                                                               sudachiPOS: wordInfo.PartOfSpeech,
                                                               isSudachiPossibleDependant: wordInfo.HasPartOfSpeechSection(PartOfSpeechSection.PossibleDependant),
                                                               isSudachiNameGuess: isNameLikeSudachiNoun && !wordInfo.IsPersonNameContext);

                // Resegmented tokens break scorer ties with the frequency-best candidate chosen at resegmentation.
                if (margin == 0
                    && wordInfo.PreMatchedCandidateWordIds != null
                    && wordInfo.PreMatchedWordId is { } preferredId)
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
                                        WordId = bestPair.Word.WordId, OriginalText = wordInfo.Text,
                                        ReadingIndex = bestPair.ReadingIndex,
                                        PartsOfSpeech = [..bestPair.Word.CachedPOS], Origin = bestPair.Word.Origin
                                    };
                return (true, deckWord, margin, allFormCandidates);
            }

            return (false, null, null, null);
        }

        private static async Task<(bool success, DeckWord? word, int? margin, List<FormCandidate>? candidates)> DeconjugateVerbOrAdjective(
            WordInfo wordInfo, Deconjugator deconjugator,
            ParserDiagnostics? diagnostics = null,
            ConcurrentDictionary<int, JmDictWord>? batchWordCache = null)
        {
            // Before WanaKana, which can't convert full-width digits.
            var textWithoutBar = wordInfo.Text.TrimEnd('ー');
            if (textWithoutBar.Length > 0 && textWithoutBar.All(char.IsDigit) ||
                (textWithoutBar.Length == 1 && textWithoutBar.IsAsciiOrFullWidthLetter()))
            {
                return (false, null, null, null);
            }

            var normalizedText = KanaNormalizer.Normalize(KanaConverter.ToHiragana(wordInfo.Text));

            if (normalizedText.Length == 1 && normalizedText.IsAsciiOrFullWidthLetter())
            {
                return (false, null, null, null);
            }

            var (deconjugated, candidates, fromReadingChannel) =
                CollectVerbAdjectiveCandidates(wordInfo, deconjugator, normalizedText);

            var baseDictionaryWord = KanaConverter.ToHiragana(wordInfo.DictionaryForm.Replace("ゎ", "わ").Replace("ヮ", "わ"),
                                                              convertLongVowelMark: false);
            var (allCandidateIds, directSurfaceIds) =
                PrioritiseAndCollectCandidateIds(wordInfo, candidates, baseDictionaryWord);

            if (allCandidateIds.Count == 0)
                return (false, null, null, null);

            Dictionary<int, JmDictWord> wordCache;
            try
            {
                wordCache = await GetWordsWithCache(allCandidateIds, batchWordCache);

                if (wordCache.Count == 0)
                {
                    return (false, null, null, null);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error retrieving verb/adjective word cache: {ex.Message}");
                return (false, null, null, null);
            }

            var (matches, filteredSuruNounIds) = await FilterAndMatchCandidates(
                wordInfo, deconjugated, candidates, wordCache, fromReadingChannel, normalizedText,
                baseDictionaryWord, batchWordCache);

            if (matches.Count == 0)
            {
                return (false, null, null, null);
            }

            var allFormCandidates = EnumerateMatchAndSurfaceCandidates(
                wordInfo, matches, directSurfaceIds, filteredSuruNounIds, wordCache,
                normalizedText, baseDictionaryWord);

            var (bestPair, margin) = PickBestFormCandidate(allFormCandidates, wordInfo.Text,
                                                           wordInfo.DictionaryForm, wordInfo.NormalizedForm,
                                                           isNameContext: wordInfo.IsPersonNameContext,
                                                           diagnostics,
                                                           sudachiReading: wordInfo.Reading,
                                                           sudachiPOS: wordInfo.PartOfSpeech,
                                                           isSudachiPossibleDependant: wordInfo.HasPartOfSpeechSection(PartOfSpeechSection.PossibleDependant));

            (bestPair, margin) = ApplyConjugationChainOverrides(bestPair, margin, allFormCandidates, wordInfo);

            if (bestPair == null)
                return (false, null, null, null);

            DeckWord deckWord = new()
                                {
                                    WordId = bestPair.Word.WordId, OriginalText = wordInfo.Text,
                                    ReadingIndex = bestPair.ReadingIndex, Conjugations =
                                        bestPair.DeconjForm?.Process is ["casual kind request"] &&
                                        bestPair.Word.PartsOfSpeech.Contains("adj-na")
                                            ? []
                                            : bestPair.DeconjForm?.Process.ToList() ?? [],
                                    PartsOfSpeech = [..bestPair.Word.CachedPOS], Origin = bestPair.Word.Origin
                                };

            return (true, deckWord, margin, allFormCandidates);
        }

        private static (List<DeconjugationForm> deconjugated, List<(DeconjugationForm form, List<int> ids)> candidates,
            bool fromReadingChannel) CollectVerbAdjectiveCandidates(WordInfo wordInfo, Deconjugator deconjugator,
                                                                    string normalizedText)
        {
            var deconjugated = deconjugator.Deconjugate(normalizedText).ToList();

            // Deconjugating a katakana noun into a kanji word fabricates vocabulary (ハガナ → 剥ぐ); ヤバイ → やばい stays.
            bool katakanaNounSurface = wordInfo.IsKatakanaNounSurface;

            List<(DeconjugationForm form, List<int> ids)> candidates = new();
            foreach (var form in deconjugated)
            {
                if (_lookups.TryGetValue(form.Text, out List<int>? lookup))
                {
                    // A suru-noun stem (tagged n: アクシュシヨウ → あくしゅ+しよう) is the noun itself, not fabricated.
                    if (katakanaNounSurface && form.Process.Length > 0 && !form.Tags.Contains("n"))
                    {
                        var kanaIds = lookup.Where(IsKanaAppropriateId).ToList();
                        if (kanaIds.Count == 0)
                            continue;
                        candidates.Add((form, kanaIds));
                        continue;
                    }

                    candidates.Add((form, lookup));
                }
            }

            // Kana-for-kanji spellings (帰りつけた) deconjugate to non-keys, so deconjugate the reading when nothing else can.
            // Excludes single kanji and digit tokens, whose readings hit homophones (髀 → 蒲桃, ３万 → 散漫).
            bool fromReadingChannel = false;
            if (candidates.Count == 0 && wordInfo.Text.Length >= 2 &&
                wordInfo.Text.Any(JapaneseTextHelper.IsKanji) &&
                !wordInfo.Text.Any(c => char.IsDigit(c) || c is >= '０' and <= '９') &&
                !string.IsNullOrEmpty(wordInfo.Reading) &&
                !DictOrNormalizedFormHasLookup(wordInfo))
            {
                var readingHiragana = KanaNormalizer.Normalize(KanaConverter.ToHiragana(wordInfo.Reading));
                if (readingHiragana != normalizedText && WanaKana.IsHiragana(readingHiragana))
                {
                    foreach (var form in deconjugator.Deconjugate(readingHiragana))
                    {
                        if (_lookups.TryGetValue(form.Text, out List<int>? lookup))
                        {
                            candidates.Add((form, lookup));
                            fromReadingChannel = true;
                        }
                    }
                }
            }

            return (deconjugated, candidates, fromReadingChannel);
        }

        private static (List<int> allCandidateIds, List<int> directSurfaceIds) PrioritiseAndCollectCandidateIds(
            WordInfo wordInfo, List<(DeconjugationForm form, List<int> ids)> candidates, string baseDictionaryWord)
        {
            bool tryDictionaryFormFallback = candidates.Count == 0 && !string.IsNullOrEmpty(wordInfo.DictionaryForm);

            var baseDictionaryWordIndex = candidates.FindIndex(c => c.form.Text == baseDictionaryWord);
            if (baseDictionaryWordIndex != -1)
            {
                var baseDictionaryWordCandidate = candidates[baseDictionaryWordIndex];
                candidates.RemoveAt(baseDictionaryWordIndex);
                candidates.Insert(0, baseDictionaryWordCandidate);
            }

            var baseWord = KanaConverter.ToHiragana(wordInfo.Text);
            var baseWordIndex = candidates.FindIndex(c => c.form.Text == baseWord);
            if (baseWordIndex != -1 && candidates[0].form.Text != baseDictionaryWord)
            {
                var baseWordCandidate = candidates[baseWordIndex];
                candidates.RemoveAt(baseWordIndex);
                candidates.Insert(0, baseWordCandidate);
            }

            var seenIds = new HashSet<int>();
            var allCandidateIds = new List<int>();
            foreach (var c in candidates)
                foreach (var id in c.ids)
                    if (seenIds.Add(id))
                        allCandidateIds.Add(id);

            if (tryDictionaryFormFallback)
            {
                if (_lookups.TryGetValue(baseDictionaryWord, out List<int>? dictFormLookupIds) ||
                    _lookups.TryGetValue(wordInfo.DictionaryForm, out dictFormLookupIds))
                {
                    if (dictFormLookupIds is { Count: > 0 })
                    {
                        foreach (var id in dictFormLookupIds)
                            if (seenIds.Add(id))
                                allCandidateIds.Add(id);
                    }
                }

                // 多き: DictionaryForm 多し, NormalizedForm 多い.
                if (dictFormLookupIds is not { Count: > 0 } && !string.IsNullOrEmpty(wordInfo.NormalizedForm)
                    && !NormalizedFormIntroducesKanji(wordInfo.Text, wordInfo.NormalizedForm))
                {
                    var normalizedHiragana = KanaConverter.ToHiragana(wordInfo.NormalizedForm,
                                                                      convertLongVowelMark: false);
                    if (_lookups.TryGetValue(normalizedHiragana, out dictFormLookupIds) ||
                        _lookups.TryGetValue(wordInfo.NormalizedForm, out dictFormLookupIds))
                    {
                        if (dictFormLookupIds is { Count: > 0 })
                        {
                            foreach (var id in dictFormLookupIds)
                                if (seenIds.Add(id))
                                    allCandidateIds.Add(id);
                        }
                    }
                }
            }

            // Exact-surface entries (adverb 悪しからず, not deconjugated 悪しい); keep the full set, matches were POS-filtered.
            var directSurfaceIds = LookupCandidateCollector.CollectIds(_lookups, wordInfo.Text, includeKanaNormalized: false);
            foreach (var id in directSurfaceIds)
                if (seenIds.Add(id))
                    allCandidateIds.Add(id);

            return (allCandidateIds, directSurfaceIds);
        }

        private static async Task<(List<(JmDictWord word, DeconjugationForm form)> matches, HashSet<int>? filteredSuruNounIds)>
            FilterAndMatchCandidates(WordInfo wordInfo,
                                     List<DeconjugationForm> deconjugated,
                                     List<(DeconjugationForm form, List<int> ids)> candidates,
                                     Dictionary<int, JmDictWord> wordCache, bool fromReadingChannel,
                                     string normalizedText, string baseDictionaryWord,
                                     ConcurrentDictionary<int, JmDictWord>? batchWordCache)
        {
            // Reading-derived candidates must share a kanji with the token (帰りつけた/帰り着く), or any homophone wins.
            if (fromReadingChannel)
            {
                var surfaceKanji = wordInfo.Text.Where(JapaneseTextHelper.IsKanji).ToArray();
                for (int ci = candidates.Count - 1; ci >= 0; ci--)
                {
                    var kept = candidates[ci].ids.Where(id => wordCache.TryGetValue(id, out var w)
                                   && w.Forms.Any(f => f.Text.IndexOfAny(surfaceKanji) >= 0)).ToList();
                    if (kept.Count > 0)
                        candidates[ci] = (candidates[ci].form, kept);
                    else
                        candidates.RemoveAt(ci);
                }
            }

            var matchResults = DeconjugationMatcher.FilterMatches(candidates, wordCache, wordInfo.PartOfSpeech);
            List<(JmDictWord word, DeconjugationForm form)> matches = matchResults.Select(m => (m.Word, m.Form)).ToList();

            // When DictionaryForm differs (いかん → いく), drop vs-only identity matches so 移管 can't beat 行かん/行く.
            HashSet<int>? filteredSuruNounIds = null;
            if (matches.Count > 1 && normalizedText != baseDictionaryWord && !string.IsNullOrEmpty(baseDictionaryWord))
            {
                bool hasDictFormMatch = matches.Any(m => m.form.Text == baseDictionaryWord && m.form.Process.Length > 0);
                if (hasDictFormMatch)
                {
                    filteredSuruNounIds = matches
                                          .Where(m => m.form.Process.Length == 0 &&
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
                if (!string.IsNullOrEmpty(wordInfo.DictionaryForm))
                {
                    await TryFallbackLookup(wordInfo.DictionaryForm.Replace("ゎ", "わ").Replace("ヮ", "わ"),
                        wordInfo, deconjugated, matches, batchWordCache);
                }

                if (matches.Count == 0 && !string.IsNullOrEmpty(wordInfo.NormalizedForm) &&
                    wordInfo.NormalizedForm != wordInfo.DictionaryForm &&
                    !NormalizedFormIntroducesKanji(wordInfo.Text, wordInfo.NormalizedForm))
                {
                    await TryFallbackLookup(wordInfo.NormalizedForm,
                        wordInfo, deconjugated, matches, batchWordCache, baseDictionaryWord);
                }

                // POS-relaxed fallback: any non-name entry, still validating deconjugation tags.
                if (matches.Count == 0)
                {
                    foreach (var m in DeconjugationMatcher.FilterMatches(candidates, wordCache, wordInfo.PartOfSpeech,
                                                                         strictPosCheck: false))
                        matches.Add((m.Word, m.Form));
                }
            }

            return (matches, filteredSuruNounIds);
        }

        private static List<FormCandidate> EnumerateMatchAndSurfaceCandidates(
            WordInfo wordInfo, List<(JmDictWord word, DeconjugationForm form)> matches,
            List<int> directSurfaceIds, HashSet<int>? filteredSuruNounIds,
            Dictionary<int, JmDictWord> wordCache, string normalizedText, string baseDictionaryWord)
        {
            // Each match's deconjForm.Text is the targetHiragana for phonetic gating.
            var allFormCandidates = new List<FormCandidate>();
            foreach (var match in matches)
            {
                var formCandidates = FormCandidateFactory.EnumerateCandidateForms(match.word, match.form.Text,
                                                                                  allowLooseLvmMatch: true, deconjForm: match.form,
                                                                                  surface: wordInfo.Text);
                allFormCandidates.AddRange(formCandidates);
            }

            // Words already in matches are skipped (a duplicate fakes margin=0); POS-incompatible ones are marked for penalty.
            var matchedWordIds = new HashSet<int>(matches.Select(m => m.word.WordId));
            // A merged inflection whose deconjugation confirms DictionaryForm (いない → 居る) drops POS-incompatible 以内.
            // Merged tokens only: single-token ambiguity (いい as adj vs verb いう) still competes.
            bool hasMergeConfirmedDeconj = wordInfo.IsMergedInflection &&
                matches.Any(m => m.form.Text == baseDictionaryWord && m.form.Process.Length > 0);
            var pastFormProcess = matches.FirstOrDefault(m => m.form.Process.Contains("past")).form?.Process;
            foreach (var id in directSurfaceIds)
            {
                if (matchedWordIds.Contains(id)) continue;
                if (filteredSuruNounIds != null && filteredSuruNounIds.Contains(id)) continue;
                if (!wordCache.TryGetValue(id, out var directWord)) continue;
                var posList = directWord.CachedPOS;
                if (!posList.Any(p => p is not (PartOfSpeech.Name or PartOfSpeech.Unknown))) continue;
                bool isPosIncompat = matches.Count > 0 &&
                                     !PosMapper.IsJmDictCompatibleWithSudachi(directWord.CachedPOS, wordInfo.PartOfSpeech);
                if (isPosIncompat && hasMergeConfirmedDeconj)
                    continue;
                if (pastFormProcess != null
                    && FormCandidateSelector.IsUnattestedInterjectionOverPastForm(directWord, pastFormProcess))
                    continue;
                var forms = FormCandidateFactory.EnumerateCandidateForms(directWord, normalizedText, allowLooseLvmMatch: true,
                                                                         surface: wordInfo.Text);
                if (isPosIncompat)
                    foreach (var f in forms)
                        f.IsPosIncompatibleDirectSurface = true;
                allFormCandidates.AddRange(forms);
            }

            return allFormCandidates;
        }

        private static (FormCandidate? bestPair, int? margin) ApplyConjugationChainOverrides(
            FormCandidate? bestPair, int? margin, List<FormCandidate> allFormCandidates, WordInfo wordInfo)
        {
            // 行けよ is almost always 行く's imperative, rarely potential 行ける's.
            if (bestPair != null
                && wordInfo.IsImperative
                && !string.IsNullOrEmpty(wordInfo.NormalizedForm)
                && wordInfo.NormalizedForm != wordInfo.DictionaryForm)
            {
                var normalizedHira = KanaConverter.ToHiragana(wordInfo.NormalizedForm,
                                                              convertLongVowelMark: false);
                var normalizedFormHira = KanaNormalizer.Normalize(normalizedHira);

                var baseVerbCandidate = allFormCandidates
                                        .Where(c => c.Form.Text == wordInfo.NormalizedForm
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

            // Sudachi's ん was negative ぬ, but the shorter slurred-る chain wins (認められん as non-negative), so swap chains.
            // margin stays: the word is unchanged, and lowering it would trigger a resegmentation re-cut.
            if (bestPair != null && wordInfo.IsSlurredNegative
                && bestPair.DeconjForm?.Process.Any(p => p.Contains("negative")) != true)
            {
                var negativeChain = allFormCandidates
                                    .Where(c => c.Word.WordId == bestPair.Word.WordId
                                                && c.DeconjForm?.Process.Any(p => p.Contains("negative")) == true)
                                    .OrderByDescending(c => ScoringPolicy.EffectiveScore(c))
                                    .FirstOrDefault();
                if (negativeChain != null)
                    bestPair = negativeChain;
            }

            return (bestPair, margin);
        }

        private static async Task TryFallbackLookup(
            string formText,
            WordInfo wordInfo,
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
                    .Where(d => d.Process.Length > 0 &&
                                (d.Text.StartsWith(formHiragana, StringComparison.Ordinal) ||
                                 (extraProcessPrefix != null && d.Text.StartsWith(extraProcessPrefix, StringComparison.Ordinal))))
                    .MinBy(d => d.Text.Length)?.Process
                    ?.Where(p => !string.IsNullOrEmpty(p)).ToList() ?? [];
                foreach (var word in wordCache.Values)
                {
                    if (word.CachedPOS.Contains(wordInfo.PartOfSpeech))
                    {
                        var form = new DeconjugationForm(formHiragana, wordInfo.Text,
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

            var (candidates, formFrequencies) = await ResolveDirectLookupCandidates(words);

            var matchedWords = new List<DeckWord>();
            foreach (var word in words)
            {
                if (!candidates.TryGetValue(word, out var matches)) continue;
                var best = PickBestDirectLookupMatch(matches, formFrequencies, readingHint: null);
                if (best == null) continue;
                matchedWords.Add(BuildDirectLookupWord(word, best.Value));
            }

            return ExcludeFinalMisparses(matchedWords);
        }

        /// <summary>Readings may list alternatives split by ; , 、 or /; any dictionary hit adds to surfacesInDictionary.</summary>
        public static async Task<Dictionary<(string Word, string Reading), DeckWord>> GetWordsDirectLookupByReading(
            IDbContextFactory<JitenDbContext> contextFactory, List<(string Word, string Reading)> pairs,
            ISet<string>? surfacesInDictionary = null)
        {
            await EnsureInitializedAsync(contextFactory);

            var (candidates, formFrequencies) = await ResolveDirectLookupCandidates(pairs.Select(p => p.Word));

            var result = new Dictionary<(string Word, string Reading), DeckWord>();
            foreach (var (word, reading) in pairs)
            {
                var key = (word, reading ?? "");
                if (result.ContainsKey(key)) continue;
                if (!candidates.TryGetValue(word, out var matches)) continue;
                surfacesInDictionary?.Add(word);

                var best = PickBestDirectLookupMatch(matches, formFrequencies, reading, requireReadingMatch: true);
                if (best == null) continue;
                if (ExcludedMisparses.Contains((best.Value.match.WordId, (byte)best.Value.readingIndex))) continue;

                result[key] = BuildDirectLookupWord(word, best.Value);
            }

            return result;
        }

        private static async Task<(Dictionary<string, List<(JmDictWord match, int readingIndex)>> candidates,
                                   Dictionary<(int, short), JmDictWordFormFrequency> frequencies)>
            ResolveDirectLookupCandidates(IEnumerable<string> surfaces)
        {
            var candidates = new Dictionary<string, List<(JmDictWord match, int readingIndex)>>();
            var allCandidateWordIds = new HashSet<int>();

            foreach (var word in surfaces)
            {
                if (candidates.ContainsKey(word)) continue;

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

                candidates[word] = matchesWithReading;
                foreach (var m in matchesWithReading)
                    allCandidateWordIds.Add(m.match.WordId);
            }

            Dictionary<(int, short), JmDictWordFormFrequency> formFrequencies = new();
            if (allCandidateWordIds.Count > 0)
            {
                await using var context = await _contextFactory.CreateDbContextAsync();
                formFrequencies = await context.WordFormFrequencies
                                               .AsNoTracking()
                                               .Where(wff => allCandidateWordIds.Contains(wff.WordId))
                                               .ToDictionaryAsync(wff => (wff.WordId, wff.ReadingIndex));
            }

            return (candidates, formFrequencies);
        }

        private static (JmDictWord match, int readingIndex)? PickBestDirectLookupMatch(
            List<(JmDictWord match, int readingIndex)> matches,
            Dictionary<(int, short), JmDictWordFormFrequency> formFrequencies,
            string? readingHint,
            bool requireReadingMatch = false)
        {
            if (matches.Count == 0)
                return null;

            int FreqRank((JmDictWord match, int readingIndex) m) =>
                formFrequencies.TryGetValue((m.match.WordId, (short)m.readingIndex), out var wff) ? wff.FrequencyRank : int.MaxValue;

            if (!string.IsNullOrWhiteSpace(readingHint))
            {
                foreach (var hint in readingHint.Split(ReadingHintSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var hintHira = KanaNormalizer.Normalize(KanaConverter.ToHiragana(hint, convertLongVowelMark: false));
                    var byReading = matches.Where(m => WordHasKanaReading(m.match, hintHira)).ToList();
                    if (byReading.Count > 0)
                        return byReading.OrderBy(FreqRank).First();
                }

                if (requireReadingMatch)
                    return null;
            }

            return matches.OrderBy(FreqRank).First();
        }

        private static readonly char[] ReadingHintSeparators = [';', '；', ',', '，', '、', '/', '／', '|'];

        private static bool WordHasKanaReading(JmDictWord word, string hintHira) =>
            word.Forms.Any(f => f.FormType == JmDictFormType.KanaForm &&
                                KanaNormalizer.Normalize(KanaConverter.ToHiragana(f.Text, convertLongVowelMark: false)) == hintHira);

        private static DeckWord BuildDirectLookupWord(string word, (JmDictWord match, int readingIndex) best) =>
            new DeckWord
            {
                WordId = best.match.WordId, ReadingIndex = (byte)best.readingIndex, OriginalText = word,
                SudachiReading = GetKatakanaReading(best.match, (byte)best.readingIndex)
            };

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

        // The length sort's tie-break relies on iteration order after Clear being insertion order.
        [ThreadStatic] private static HashSet<string>? _compoundScratchSet;

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

                // Built right-to-left and only once a compound is found; untouched sentences keep their list.
                List<(WordInfo word, int position, int length)>? result = null;

                for (int i = wordInfos.Count - 1; i >= 0; i--)
                {
                    var word = wordInfos[i];

                    // Na-adjectives anchor expression windows only (見るも無残), like nouns; plain compounds would over-merge.
                    if (word.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective or PartOfSpeech.Expression or PartOfSpeech.Suffix
                        or PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Name or PartOfSpeech.NaAdjective)
                    {
                        bool nounTrigger = word.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Name
                            or PartOfSpeech.NaAdjective;
                        var match = TryMatchCompounds(wordInfos, i, tokenHashes, forceExpressionOnly: nounTrigger);

                        // Hard pins are never swallowed (ぶん in ４分の１ ≠ ４分); soft pins stay absorbable (臆病風に吹かれる).
                        if (match.HasValue)
                        {
                            for (int j = match.Value.startIndex; j <= i && match.HasValue; j++)
                                if (wordInfos[j] is { HardPinned: true, PreMatchedWordId: not null })
                                    match = null;
                        }

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

                            if (result == null)
                            {
                                result = new List<(WordInfo word, int position, int length)>(sentence.Words.Count);
                                for (int j = wordInfos.Count - 1; j > i; j--)
                                    result.Add(sentence.Words[j]);
                            }
                            result.Add((combinedWordInfo, startPosition, combinedLength));
                            i = startIndex;
                            continue;
                        }
                    }

                    result?.Add(sentence.Words[i]);
                }

                if (result == null)
                    continue;
                result.Reverse();
                sentence.Words = result;
            }
        }

        // Curated: most particles over-merge (とは, には); しか only ever heads しかない/しかねぇ.
        private static readonly HashSet<string> ParticleExpressionOpeners = ["しか"];

        // Curated, not entry-derived: many X+に pairs are genuine case particles (それに気づいた, 外に出る).
        private static readonly HashSet<string> LexicalizedAdverbPairs = ["フルに", "無性に", "意地でも", "絶対に", "徐々に"];

        /// <summary>Merges X的+に, curated X+に pairs and lexicalized Noun+ほど (山ほど; 三日ほど stays split).</summary>
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

                    // としたら is suppositional (ためだとしたら) except after a volitional, where it's とする "try to" (押そうとしたら).
                    if (i + 1 < words.Count
                        && word is { Text: "と", PartOfSpeech: PartOfSpeech.Particle }
                        && words[i + 1].word.Text is "したら" or "すれば" or "すると"
                        && words[i + 1].word.DictionaryForm is "する" or "為る"
                        && !(i > 0 && IsVolitionalSurface(words[i - 1].word.Text))
                        && HasConjunctionEntry(word.Text + words[i + 1].word.Text))
                    {
                        var next = words[i + 1];
                        var combinedText = word.Text + next.word.Text;
                        var merged = new WordInfo(word)
                        {
                            Text = combinedText,
                            DictionaryForm = combinedText,
                            NormalizedForm = combinedText,
                            Reading = word.Reading + next.word.Reading,
                            PartOfSpeech = PartOfSpeech.Conjunction,
                            EndOffset = next.word.EndOffset,
                        };
                        result.Add((merged, words[i].position, words[i].length + next.length));
                        changed = true;
                        i++;
                        continue;
                    }

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
                        bool isHodoPair = next.word.Text == "ほど"
                                          && next.word.PartOfSpeech == PartOfSpeech.Particle;
                        // 先に tails always merge (一足先に); には only clause-initially, since mid-clause it's case+topic (その時には).
                        bool isSakiNiPair = next.word.Text == "先に";
                        bool isNiwaPair = next.word.Text == "には"
                                          && next.word.PartOfSpeech == PartOfSpeech.Particle
                                          && (i == 0 || words[i - 1].word.PartOfSpeech
                                              is PartOfSpeech.Symbol or PartOfSpeech.SupplementarySymbol or PartOfSpeech.BlankSpace);

                        if ((isNiPair || isCuratedPair || isHodoPair || isSakiNiPair || isNiwaPair)
                            && HasLexicalAdverbEntry(word.Text + next.word.Text))
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

        private static bool HasLexicalAdverbEntry(string text) =>
            HasLookupWhere(text, KanaFallback.ToNormalizedHiragana,
                           static ids => AnyNonNameWithPos(ids, PartOfSpeech.Adverb));

        private static bool HasConjunctionEntry(string text) =>
            HasLookupWhere(text, KanaFallback.None,
                           static ids => AnyNonNameWithPos(ids, PartOfSpeech.Conjunction));

        private static bool IsVolitionalSurface(string text)
        {
            if (text.Length < 2 || text[^1] != 'う') return false;
            return "おこそとのほもよろごぞどぼぽょ".IndexOf(text[^2]) >= 0;
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

                    if (word.Text == "ー" && word.PartOfSpeech == PartOfSpeech.SupplementarySymbol)
                    {
                        if (i == 0)
                        {
                            i--;
                            continue;
                        }

                        // Sudachi splits あげるー into あげ+る+ー.
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
                            if (!prev.word.Text.EndsWith('ー'))
                            {
                                prev.word.Text += "ー";
                                words[i - 1] = (prev.word, prev.position, prev.length + words[i].length);
                            }
                            i--;
                        }

                        continue;
                    }

                    if (word.Text.EndsWith('ー') && word.Text != "ー")
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
                                (word.Text == "すー" && prev.word.Text.EndsWith('で'));

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

                    result.Add(words[i]);
                    i--;
                }

                result.Reverse();
                sentence.Words = result;
            }
        }

        private static bool HasLookupForCompound(string dictForm) =>
            _lookups == null || HasLookupWhere(dictForm, KanaFallback.NormalizeToHiragana,
                                               static ids => ids.Count > 0);

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

        /// <summary>Slang elongation as stem+い: ヤバー → ヤバい, スゴー → スゴい.</summary>
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

        /// <summary>Must be a real i-adjective; bare existence lets noun keys hijack interjections (いえー → 遺影).</summary>
        private static bool HasIAdjectiveLookup(string text) =>
            HasLookupWhere(text, KanaFallback.None, static ids => AnyWithPos(ids, PartOfSpeech.IAdjective));

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

                    // A pin is a repair stage's word decision, never noise (ソ in 親ソ; す as する, not すんだ=済んだ).
                    bool shouldFilter = MisparsesRemove.Contains(word.Text) &&
                                        word.PreMatchedWordId == null &&
                                        !(nextIsLongVowel && word.Text.Length == 1 && JapaneseTextHelper.IsAllKana(word.Text)) &&
                                        !word.Text.EndsWith('ー');

                    bool isSingleKanaStutter = !nextIsLongVowel && word.Text.Length == 1 && JapaneseTextHelper.IsAllKana(word.Text)
                                               && word.PreMatchedWordId == null;
                    if (shouldFilter ||
                        word.PartOfSpeech == PartOfSpeech.Noun && !nextIsLongVowel && (
                            isSingleKanaStutter ||
                            word.Text is "エナ" or "えな"
                        ) ||
                        word.PartOfSpeech == PartOfSpeech.Symbol && isSingleKanaStutter)
                    {
                        // The shard may be a cut-off first mora (１つ|ま|ねた → 真似た), so try joining before deleting.
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

        /// <summary>The join must deconjugate to a verb/adjective (まねた → 真似る); noun keys bring back collisions.</summary>
        private static bool TryJoinShredWithFollowing(WordInfo shred, WordInfo following, out string dictForm)
        {
            dictForm = "";

            if (!JapaneseTextHelper.IsAllKana(shred.Text) || following.Text.Length == 0 || !JapaneseTextHelper.IsAllKana(following.Text))
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
                if (form.Process.Length == 0 || form.Tags.Length == 0) continue;
                var lastTag = form.Tags[^1];
                if (!lastTag.StartsWith('v') && lastTag != "adj-i") continue;
                if (!_lookups.TryGetValue(form.Text, out var ids)) continue;
                // The entry must be a verb/adjective (not ひむ "hymn"), uk or prioritized (真似る is ichi1), never archaic.
                if (!ids.Any(id => WordMeta.TryGetValue(id, out var meta)
                                   && !meta.IsTrueName
                                   && Array.Exists(meta.Pos, p => p is PartOfSpeech.Verb or PartOfSpeech.IAdjective)
                                   && meta.GetPriorityScore(true) > (IsKanaAppropriateId(id) ? ArchaicOnlyPriorityFloor : 0)))
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

        /// <summary>Shatters lookup-less nouns so CombineNounCompounds can re-cut them (各党 + 幹部 → 各 + 党幹部).</summary>
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

                    // 母性的な → 母性 + 的な when only the base has a lookup.
                    if (word.Text.Length >= 3 &&
                        (word.Text.EndsWith('的') || word.Text.EndsWith("的な", StringComparison.Ordinal)) &&
                        !HasLookup(word.Text))
                    {
                        var suffixLen = word.Text.EndsWith("的な", StringComparison.Ordinal) ? 2 : 1;
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

                    // Digit runs (四十七) stay whole or the tail bleeds into a counter (四十 + 七分); 五万 shatters to keep 万.
                    if (word.Text.Length >= 2 &&
                        PosMapper.IsNounForCompounding(word.PartOfSpeech) &&
                        word.Text.All(JapaneseTextHelper.IsKanji) &&
                        !word.Text.All(c => NumeralKanji.Contains(c)) &&
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

        // Curated: free-noun kanji also carry n-pref entries yet read as the noun (前準備 → まえ, not ぜん).
        private static readonly HashSet<char> BoundPrefixKanji =
            ['総', '諸', '各', '準', '副', '反', '超', '非', '未', '汎', '准', '旧'];

        /// <summary>A leftover bound-prefix kanji before a noun takes its prefix reading (総本部: 総 そう, not ふさ).</summary>
        private static void ReclassifyLeftoverPrefixChars(List<SentenceInfo> sentences)
        {
            foreach (var sentence in sentences)
            {
                var words = sentence.Words;
                for (int i = 0; i < words.Count - 1; i++)
                {
                    var word = words[i].word;
                    if (word.PartOfSpeech is not (PartOfSpeech.Noun or PartOfSpeech.CommonNoun)
                        || word.Text.Length != 1
                        || !BoundPrefixKanji.Contains(word.Text[0]))
                        continue;

                    var next = words[i + 1].word;
                    if (next.PartOfSpeech is not (PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                        or PartOfSpeech.NaAdjective or PartOfSpeech.NominalAdjective))
                        continue;

                    if (!_lookups.TryGetValue(word.Text, out var ids))
                        continue;

                    int? prefixId = null;
                    foreach (var id in ids)
                        if (WordMeta.TryGetValue(id, out var meta) && meta.GetPrimaryPos() == PartOfSpeech.Prefix)
                        { prefixId = id; break; }

                    if (prefixId == null)
                        continue;

                    word.PartOfSpeech = PartOfSpeech.Prefix;
                    word.PreMatchedWordId = prefixId;
                }
            }
        }

        // Other nouns after a bare 連用形 usually start a new clause (緩み周りの, 向け呪いを), so only productive V2 heads qualify.
        private static readonly HashSet<string> CompoundVerbHeads =
        [
            "回す", "回る", "合う", "合わせる", "切る", "切れる", "掛ける", "掛かる", "始める", "出す",
            "続ける", "終える", "終わる", "過ぎる", "直す", "返す", "込む", "尽くす", "通す", "抜く",
            "忘れる", "損なう", "損ねる", "残す", "遅れる", "慣れる", "飽きる"
        ];

        /// <summary>A noun-tagged V2 stem glued to a bare 連用形 is a verb (呼び回し → 回す, not the sumo belt 回し).</summary>
        private static void PinCompoundVerbTails(List<SentenceInfo> sentences, ParserDiagnostics? diagnostics)
        {
            foreach (var sentence in sentences)
            {
                var words = sentence.Words;
                for (int i = 1; i < words.Count; i++)
                {
                    var (word, position, _) = words[i];
                    var (prev, prevPosition, prevLength) = words[i - 1];
                    if (word.PartOfSpeech is not (PartOfSpeech.Noun or PartOfSpeech.CommonNoun)
                        || word.PreMatchedWordId != null
                        || prevPosition + prevLength != position
                        || !IsBareRenyoukei(prev))
                        continue;

                    if (VerbStemLookup.Find(word.Text, word.Reading, _lookups, WordMeta, _wordFrequencyRanks) is not { } verb
                        || !CompoundVerbHeads.Contains(verb.DictionaryForm))
                        continue;

                    VerbStemLookup.Pin(word, verb);
                    diagnostics?.LogParserEvent("PinCompoundVerbTails", "pin", [prev.Text, word.Text], [word.Text],
                                                $"verb stem after 連用形 (WordId={verb.WordId})");
                }
            }
        }

        private static bool IsBareRenyoukei(WordInfo word) =>
            word.PartOfSpeech == PartOfSpeech.Verb
            && (MorphologicalAnalyser.TryGodanDictForm(word.Text) == word.DictionaryForm
                || word.Text + "る" == word.DictionaryForm);

        private static readonly HashSet<char> TrailingParticles =
        [
            'か', 'よ', 'ね', 'な', 'ぞ', 'ぜ', 'わ', 'さ', 'の', 'に', 'と', 'も', 'で', 'は', 'が'
        ];

        private static bool HasLookup(string text) =>
            HasLookupWhere(text, KanaFallback.ToNormalizedHiragana, static ids => ids.Count > 0);

        private static int GetBestLookupPriority(string text)
        {
            if (!_lookups.TryGetValue(text, out var ids) || ids.Count == 0)
                return -1;
            int best = -1;
            foreach (var id in ids)
            {
                if (WordMeta.TryGetValue(id, out var m))
                {
                    var score = m.GetPriorityScore(JapaneseTextHelper.IsAllKana(text));
                    if (score > best) best = score;
                }
            }
            return best;
        }

        /// <summary>Rejects DictionaryForm homographs the surface can't conjugate from; no bases means allow.</summary>
        private static bool DeconjBasesAllow(string surface, string dictForm)
        {
            var forms = Deconjugator.Instance.Deconjugate(surface);
            bool anyRealBase = false;
            foreach (var f in forms)
            {
                if (f.Process.Length == 0) continue;
                anyRealBase = true;
                if (f.Text == dictForm) return true;
            }
            return !anyRealBase;
        }

        /// <summary>Non-edge positions only: バッカ → バカ, うるっさい → うるさい.</summary>
        private static string RemoveInternalSokuon(string text)
        {
            if (text.Length < 3) return text;
            Span<char> buf = stackalloc char[text.Length];
            int n = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (i > 0 && i < text.Length - 1 && text[i] is 'っ' or 'ッ') continue;
                buf[n++] = text[i];
            }
            return n == text.Length ? text : new string(buf[..n]);
        }

        private static bool HasPrioritizedMeta(int id) =>
            WordMeta.TryGetValue(id, out var meta) && meta.GetPriorityScore(true) > 0;

        // Unmarked archaic entries score about -350, all else above the name floor (-50); した+もの must not become 下物.
        private const int ArchaicOnlyPriorityFloor = -100;

        // One id must be both non-name and non-archaic; script-blind since the uk delta (10) never crosses the floor.
        private static bool HasUsableCompoundEntry(List<int> ids)
        {
            foreach (var id in ids)
                if (!_nameOnlyWordIds.Contains(id)
                    && WordMeta.TryGetValue(id, out var meta) && meta.GetPriorityScore(false) > ArchaicOnlyPriorityFloor)
                    return true;
            return false;
        }

        private static bool PrefixBreaksFollowingCompound(WordInfo prefix, WordInfo head, WordInfo following)
        {
            if (!PosMapper.IsNounForCompounding(following.PartOfSpeech) || following.Text.Length == 0)
                return false;

            return TryLookupUsableCompound(head.Text + following.Text, out _)
                   && !_lookups.ContainsKey(prefix.Text + head.Text + following.Text);
        }

        // matchedKey becomes the dictionary form: a uk compound only resolves under its hiragana spelling.
        private static bool TryLookupUsableCompound(string text, out string matchedKey)
        {
            if (_lookups.TryGetValue(text, out var ids) && ids.Count > 0
                && HasUsableCompoundEntry(ids))
            {
                matchedKey = text;
                return true;
            }

            var hiragana = KanaConverter.ToHiragana(text, convertLongVowelMark: false);
            if (hiragana != text && _lookups.TryGetValue(hiragana, out ids) && ids.Count > 0
                && HasUsableCompoundEntry(ids))
            {
                matchedKey = hiragana;
                return true;
            }

            matchedKey = text;
            return false;
        }

        private static int? LookupWithKanaFallback(string text, Func<string, int?> query)
        {
            var direct = query(text);
            if (direct != null) return direct;
            try
            {
                var hira = KanaConverter.ToNormalizedHiragana(text);
                if (hira != text) return query(hira);
            }
            catch
            {
            }
            return null;
        }

        // Tiers aren't interchangeable; each keeps the exact conversion its call sites rely on.
        private enum KanaFallback
        {
            None,
            ToNormalizedHiragana,
            ToHiraganaKeepLongVowelMark,
            NormalizeToHiragana,
        }

        private static bool HasLookupWhere(string text, KanaFallback fallback, Func<List<int>, bool> match)
        {
            if (_lookups.TryGetValue(text, out var ids) && match(ids))
                return true;

            string hira;
            switch (fallback)
            {
                case KanaFallback.ToHiraganaKeepLongVowelMark:
                    hira = KanaConverter.ToHiragana(text, convertLongVowelMark: false);
                    break;
                case KanaFallback.ToNormalizedHiragana:
                    try
                    {
                        hira = KanaConverter.ToNormalizedHiragana(text);
                    }
                    catch
                    {
                        return false;
                    }

                    break;
                case KanaFallback.NormalizeToHiragana:
                    try
                    {
                        hira = KanaNormalizer.Normalize(KanaConverter.ToHiragana(text));
                    }
                    catch
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }

            return hira != text && _lookups.TryGetValue(hira, out ids) && match(ids);
        }

        private static bool AnyWithPos(List<int> ids, PartOfSpeech pos)
        {
            foreach (var id in ids)
                if (WordMeta.TryGetValue(id, out var meta) && Array.IndexOf(meta.Pos, pos) >= 0)
                    return true;
            return false;
        }

        private static bool AnyNonNameWithPos(List<int> ids, PartOfSpeech pos)
        {
            foreach (var id in ids)
                if (!_nameOnlyWordIds.Contains(id) && WordMeta.TryGetValue(id, out var meta)
                                                   && Array.IndexOf(meta.Pos, pos) >= 0)
                    return true;
            return false;
        }

        // Pins synthesised tokens (collapsed ごろごろごろ) to an entry without re-segmentation.
        private static int? GetNonNameCompoundId(string text) =>
            LookupWithKanaFallback(text, static key =>
            {
                if (!_lookups.TryGetValue(key, out var ids) || ids.Count == 0) return null;
                int? bestId = null;
                long bestRank = long.MaxValue;
                foreach (var id in ids)
                {
                    if (_nameOnlyWordIds.Contains(id)) continue;
                    long rank = _wordFrequencyRanks.TryGetValue(id, out var r) ? r : int.MaxValue;
                    // Ties break to the lowest WordId so the pin doesn't depend on _lookups' array_agg order.
                    if (bestId == null || rank < bestRank || (rank == bestRank && id < bestId))
                    {
                        bestId = id;
                        bestRank = rank;
                    }
                }
                return bestId;
            });

        private static int? GetBestNonNameFrequencyRank(string text) =>
            LookupWithKanaFallback(text, static key =>
            {
                if (!_lookups.TryGetValue(key, out var ids) || ids.Count == 0) return null;
                int? best = null;
                foreach (var id in ids)
                {
                    if (_nameOnlyWordIds.Contains(id)) continue;
                    if (_wordFrequencyRanks.TryGetValue(id, out var rank) && (best == null || rank < best))
                        best = rank;
                }
                return best;
            });

        private static bool HasNonNameLookup(string text) =>
            HasLookupWhere(text, KanaFallback.ToNormalizedHiragana,
                           static ids => ids.Exists(static id => !_nameOnlyWordIds.Contains(id)));

        // Separates a real conjugating lexeme from a homographic noun a deconjugation lands on (明日/あす).
        private static bool HasVerbOrAdjectiveLookup(string text) =>
            HasLookupWhere(text, KanaFallback.None,
                           static ids => AnyWithPos(ids, PartOfSpeech.Verb) || AnyWithPos(ids, PartOfSpeech.IAdjective));

        private static bool HasExpressionLookup(string text) =>
            HasLookupWhere(text, KanaFallback.None, static ids => AnyWithPos(ids, PartOfSpeech.Expression));

        /// <summary>Strips small vowels echoing the preceding vowel row (きゃぁ → きゃ); foreign-mora smalls (ふぁ) stay.</summary>
        private static bool TryRemoveEchoedSmallVowels(string text, out string result)
        {
            System.Text.StringBuilder? sb = null;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                char vowel = SmallVowelKanaVowel(c);
                if (i > 0 && vowel != '\0' && KanaEndsWithVowel(text[i - 1], vowel))
                {
                    sb ??= new System.Text.StringBuilder(text[..i]);
                    continue;
                }

                sb?.Append(c);
            }

            result = sb?.ToString() ?? text;
            return sb != null;
        }

        private static char SmallVowelKanaVowel(char c) => c switch
        {
            'ぁ' or 'ァ' => 'a',
            'ぃ' or 'ィ' => 'i',
            'ぅ' or 'ゥ' => 'u',
            'ぇ' or 'ェ' => 'e',
            'ぉ' or 'ォ' => 'o',
            _ => '\0',
        };

        private static bool KanaEndsWithVowel(char kana, char vowel)
        {
            var romaji = WanaKana.ToRomaji(kana.ToString());
            return romaji.Length > 0 && char.ToLowerInvariant(romaji[^1]) == vowel;
        }

        /// <summary>Contains hiragana and no katakana: a script a katakana-only loanword is never written in.</summary>
        private static bool IsHiraganaSurface(string text)
        {
            bool hasHiragana = false;
            foreach (var c in text)
            {
                if (c is >= 'ぁ' and <= 'ゖ') hasHiragana = true;
                else if (c is >= 'ァ' and <= 'ヺ') return false;
            }

            return hasHiragana;
        }

        /// <summary>Primary POS only (話, 回); a minor counter sense (何, 差し) must not make a token counter-rescorable.</summary>
        private static bool HasCounterLookup(string text) =>
            HasLookupWhere(text, KanaFallback.None, static ids =>
                ids.Exists(static id => WordMeta.TryGetValue(id, out var meta)
                                        && meta.GetPrimaryPos() == PartOfSpeech.Counter));

        /// <summary>Any counter sense (度, 発); looser than HasCounterLookup, as mora re-cuts only need it to exist.</summary>
        private static bool HasCounterSenseAvailable(string text) =>
            HasLookupWhere(text, KanaFallback.None, static ids => AnyWithPos(ids, PartOfSpeech.Counter));

        /// <summary>uk or kanji-less words; kanji-backed non-uk words (遺影) and name entries (ヒューン) reject.</summary>
        private static bool IsKanaAppropriateId(int id)
        {
            if (_nameOnlyWordIds.Contains(id)) return false;
            if (!WordMeta.TryGetValue(id, out var meta)) return false;
            if (meta.IsTrueName) return false;

            // uk is folded into priority scores at load: kana = base + 10, kanji = base - 10.
            bool isUsuallyKana = meta.PriorityScoreKana > meta.PriorityScoreKanji;
            return isUsuallyKana || !_kanjiBackedWordIds.Contains(id);
        }

        private static bool HasKanaAppropriateLookup(string text) =>
            HasLookupWhere(text, KanaFallback.ToNormalizedHiragana, static ids => ids.Exists(IsKanaAppropriateId));

        /// <summary>Whether a noun+する merge resolves through deconjugation (密着 yes, 大怪我 no).</summary>
        private static bool HasSuruVerbLookup(string text) =>
            HasLookupWhere(text, KanaFallback.None, static ids => AnyNonNameWithPos(ids, PartOfSpeech.Verb));

        private static bool HasPrioritizedNonNameLookup(string text) =>
            HasLookupWhere(text, KanaFallback.ToNormalizedHiragana, static ids =>
                ids.Exists(static id => !_nameOnlyWordIds.Contains(id)
                                        && WordMeta.TryGetValue(id, out var meta) && meta.GetPriorityScore(true) > 0));

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

                    // MA-merged tokens (遠慮しないで) aren't lookup keys but deconjugate fine in ProcessWord.
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

                    // A name-only remainder doesn't count: ブリタニカ is not ブリタニ+か.
                    char lastChar = textHira[^1];
                    bool hasTrailingParticle = textHira.Length >= 3 && TrailingParticles.Contains(lastChar);
                    bool remainderKnown = hasTrailingParticle &&
                                          (HasNonNameLookup(text[..^1]) ||
                                           (textHira.Length >= 5 &&
                                            Deconjugator.Instance.Deconjugate(textHira[..^1])
                                                       .Any(f => HasNonNameLookup(f.Text))));
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

        // 雪の下: the botanical ユキノシタ must not swallow prose 雪+の+下 "under the snow".
        private static readonly HashSet<string> NounCompoundExclusions = ["おつもり", "ものたち", "雪の下"];

        // Sudachi emits ・/＝ as symbols (サン＝テグジュペリ); enumerations (リンゴ・ミカン) have no entry and never merge.
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
                        // Sudachi splits でか|ぶつ, 飛び|道具, 一向|一揆 and mis-tagged kanji leads (偶|然); lookups gate the merge.
                        // Hiragana adverbs are excluded: そう+なん would hit 遭難 through its reading key.
                        bool misparsedKanjiLead = word.Text.Length == 1 && JapaneseTextHelper.IsKanji(word.Text[0]);
                        bool kanjiAdverbLead = word.PartOfSpeech == PartOfSpeech.Adverb &&
                                               word.Text.Any(c => JapaneseTextHelper.IsKanji(c) || JapaneseTextHelper.IsKatakana(c));
                        if ((word.PartOfSpeech is PartOfSpeech.IAdjective or PartOfSpeech.Verb || kanjiAdverbLead || misparsedKanjiLead) &&
                            i + 1 < sentence.Words.Count &&
                            PosMapper.IsNounForCompounding(sentence.Words[i + 1].word.PartOfSpeech) &&
                            !word.HardPinned && !sentence.Words[i + 1].word.HardPinned)
                        {
                            var combinedText = word.Text + sentence.Words[i + 1].word.Text;
                            // uk compounds key on hiragana (出べそ, not 出ベソ); DictionaryForm keeps the surface for form scoring.
                            if (TryLookupUsableCompound(combinedText, out var matchedKey))
                            {
                                var combinedReading = word.Reading + sentence.Words[i + 1].word.Reading;
                                int combinedLength = length + sentence.Words[i + 1].length;
                                var combinedWord = new WordInfo(word)
                                                   {
                                                       Text = combinedText, DictionaryForm = combinedText, PartOfSpeech = PartOfSpeech.Noun,
                                                       NormalizedForm = matchedKey, Reading = KanaConverter.ToHiragana(combinedReading,
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

                    // A prefix mustn't eat the head below it (お|母上, not お母|上), unless the whole span is a word (お手紙).
                    if (word.PartOfSpeech == PartOfSpeech.Prefix && i + 2 < sentence.Words.Count
                        && PrefixBreaksFollowingCompound(word, sentence.Words[i + 1].word, sentence.Words[i + 2].word))
                    {
                        result.Add(sentence.Words[i]);
                        i++;
                        continue;
                    }

                    int bestMatch = 1;
                    string? matchedNounText = null;
                    for (int windowSize = Math.Min(8, sentence.Words.Count - i); windowSize >= 2; windowSize--)
                    {
                        bool allValid = true;
                        bool hasNameLikeToken = false;
                        for (int j = 0; j < windowSize; j++)
                        {
                            var w = sentence.Words[i + j].word;
                            // Counters only continue a compound, prefixes only start one (さん+なん must not become 三男).
                            bool isNoun = (PosMapper.IsNounForCompounding(w.PartOfSpeech)
                                           && (j == 0 || w.PartOfSpeech != PartOfSpeech.Prefix)) ||
                                          (j > 0 && w.PartOfSpeech == PartOfSpeech.Counter);
                            bool isInnerConnector = j > 0 && j < windowSize - 1 &&
                                                    (w is { PartOfSpeech: PartOfSpeech.Particle, Text: "の" } ||
                                                     IsNameConnectorSymbol(w));
                            if (!isNoun && !isInnerConnector)
                            {
                                allValid = false;
                                break;
                            }

                            // Hard pins (三つ目's ordinal 目) are never re-absorbed; soft pins are.
                            if (w is { HardPinned: true, PreMatchedWordId: not null })
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

                        // Name-only matches need a name-like constituent; a fully-archaic entry can't absorb the span.
                        if (_lookups.TryGetValue(combinedText, out var wordIds) && wordIds.Count > 0 &&
                            (hasNameLikeToken || HasUsableCompoundEntry(wordIds)))
                        {
                            bestMatch = windowSize;
                            matchedNounText = combinedText;
                            break;
                        }

                        var hiraganaText = KanaConverter.ToHiragana(combinedText,
                                                                    convertLongVowelMark: false);
                        if (hiraganaText != combinedText &&
                            _lookups.TryGetValue(hiraganaText, out wordIds) && wordIds.Count > 0 &&
                            (hasNameLikeToken || HasUsableCompoundEntry(wordIds)))
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

                        // Sudachi splits proper names into parts; the merged token must stay name-like.
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
                    else if (TryCombineNounVerbStemCompound(sentence.Words, i, word, length, out var nounVerbWord,
                                                            out var nounVerbLength))
                    {
                        result.Add((nounVerbWord, position, nounVerbLength));
                        i += 2;
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

        /// <summary>Rejoins noun + terminal 連用形 nominalizations Sudachi splits (目|眩まし, 気|晴らし) into a JMDict noun.</summary>
        private static bool TryCombineNounVerbStemCompound(List<(WordInfo word, int position, int length)> words, int i,
                                                           WordInfo word, int length,
                                                           out WordInfo combinedWord, out int combinedLength)
        {
            combinedWord = null!;
            combinedLength = 0;

            if (i + 1 >= words.Count || words[i + 1].word.PartOfSpeech != PartOfSpeech.Verb)
                return false;

            // A following auxiliary or verb means the stem is a real predicate (朝起き|た).
            if (i + 2 < words.Count &&
                words[i + 2].word.PartOfSpeech is PartOfSpeech.Auxiliary or PartOfSpeech.Verb)
                return false;

            var nextWord = words[i + 1].word;
            var combinedText = word.Text + nextWord.Text;
            if (!_lookups.TryGetValue(combinedText, out var ids) || ids.Count == 0 ||
                ids.All(id => _nameOnlyWordIds.Contains(id)) ||
                !ids.Any(id => WordMeta.TryGetValue(id, out var meta) && meta.Pos.Any(PosMapper.IsNounForCompounding)))
                return false;

            combinedLength = length + words[i + 1].length;
            combinedWord = new WordInfo(word)
                           {
                               Text = combinedText, DictionaryForm = combinedText, PartOfSpeech = PartOfSpeech.Noun,
                               NormalizedForm = combinedText,
                               Reading = KanaConverter.ToHiragana(word.Reading + nextWord.Reading, convertLongVowelMark: false),
                               PreMatchedWordId = null
                           };
            return true;
        }

        private static readonly HashSet<string> ExpressionExclusions =
            ["このように", "そのように", "あのように", "どのように", "のように",
             "この様に", "その様に", "あの様に", "どの様に", "の様に",
             "何だと", "なんだと",
             // Their JMDict interjection/discourse senses rarely apply.
             "それが", "それは", "これが", "これは", "あれが", "あれは",
             // The temper idiom is rare; fiction means literal insect-killing (小の虫を殺す).
             "虫を殺す", "むしをころす", "虫を殺し", "むしをころし",
             // こと is almost always the nominalizer (関与することを好まない).
             "ことを好む", "事を好む", "ことをこのむ",
             // The idiom is rare; prose means "know that man" (この男を知っているか).
             "男を知る", "男を知って", "男を知った", "男を知ってる", "男を知っている",
             "男を知らない", "男を知り"];

        /// <summary>Curated: JMDict also lists compositional pairs (のです, それを, 外に) that must stay split.</summary>
        private static readonly HashSet<string> TwoTokenExpressionWhitelist =
        [
            "どちらも", "どっちも", "いつでも", "何時でも", "一人で", "ひとりで", "独りで",
            "ものか", "もんか", "たまるか", "堪るか", "それとも", "至るまで", "いたるまで",
            "史上初", "綺麗さっぱり", "きれいさっぱり", "とはいえ", "とは言え", "あいよ", "いいや"
        ];

        /// <summary>Idioms with elided を; curated because compositional 〜を〜 entries (服を着る) must stay split.</summary>
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

        /// <summary>Hash fold (c - 0x60) matches ToHiragana here, so a hash miss proves a lookup miss; ヶ/ゎ go slow.</summary>
        private static bool IsHashFoldSafeChar(char c) =>
            (c >= 'ぁ' && c <= 'ゔ' && c != 'ゎ') ||
            (c >= 'ァ' && c <= 'ヴ' && c != 'ヮ') ||
            c == 'ー' || c == '々' ||
            (c >= '一' && c <= '鿿');                     // CJK unified ideographs

        private static bool IsHashFoldSafe(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (!IsHashFoldSafeChar(s[i]))
                    return false;
            return true;
        }

        /// <summary>False only when the hash proves no lookup key equals prefix + tail; unsafe chars always pass.</summary>
        private static bool PrefixTailMayMatch(bool prefixSafe, bool prefixLenOk, long prefixHash, int prefixLen, string tail)
        {
            if (!prefixSafe || !IsHashFoldSafe(tail))
                return true;

            return prefixLenOk
                   && tail.Length <= _maxLookupKeyLength
                   && prefixLen + tail.Length <= _maxLookupKeyLength
                   && _compoundHashSet!.Contains(unchecked(prefixHash * _hashBasePowers![tail.Length] + HiraRollingHash(tail)));
        }

        private static readonly HashSet<long> ExpressionExclusionHashes = BuildExpressionExclusionHashes();

        private static HashSet<long> BuildExpressionExclusionHashes()
        {
            var set = new HashSet<long>();
            foreach (var text in ExpressionExclusions)
                set.Add(HiraRollingHash(text));
            return set;
        }

        private static readonly HashSet<long> TwoTokenExpressionWhitelistHashes = BuildTwoTokenWhitelistHashes();

        private static HashSet<long> BuildTwoTokenWhitelistHashes()
        {
            var set = new HashSet<long>();
            foreach (var text in TwoTokenExpressionWhitelist)
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

                Span<long> cumHash = stackalloc long[9];
                Span<int> cumLen = stackalloc int[9];
                Span<bool> cumLenOk = stackalloc bool[9];

                List<(WordInfo word, int position, int length)>? result = null;
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

                        // Whitelist-only: no POS gate separates compositional pairs (すると) from units (どちらも).
                        if (windowSize == 2)
                        {
                            // A hash miss proves a whitelist miss, skipping the concat allocation.
                            string? pairText = null;
                            if (tokenLens[i + 1] >= _hashBasePowers!.Length ||
                                TwoTokenExpressionWhitelistHashes.Contains(
                                    unchecked(tokenHashes[i] * _hashBasePowers[tokenLens[i + 1]] + tokenHashes[i + 1])))
                                pairText = ConcatTokenTexts(sentence.Words, i, 2);

                            if (pairText != null && TwoTokenExpressionWhitelist.Contains(pairText))
                            {
                                // The conjunction とはいえ never continues into ない (とは言えない).
                                bool blockedByNegation = pairText is "とはいえ" or "とは言え"
                                    && i + 2 < sentence.Words.Count
                                    && sentence.Words[i + 2].word.Text is "ない" or "ません" or "ん" or "なかった";
                                // いいや = "nope" only clause-initially; 天気もいいや is いい+や.
                                bool blockedNonInitial = pairText == "いいや" && i > 0;
                                // Rhetorical ものか follows a predicate (負ける、ものか); after a nominal it's もの+か (どのようなものか).
                                bool blockedMonoKa = false;
                                if (pairText is "ものか" or "もんか")
                                {
                                    int p = i - 1;
                                    while (p >= 0 && sentence.Words[p].word.PartOfSpeech
                                           is PartOfSpeech.SupplementarySymbol or PartOfSpeech.Symbol
                                              or PartOfSpeech.BlankSpace)
                                        p--;
                                    blockedMonoKa = !(p >= 0 && sentence.Words[p].word.PartOfSpeech
                                        is PartOfSpeech.Verb or PartOfSpeech.IAdjective or PartOfSpeech.Auxiliary);
                                }

                                if (!blockedByNegation && !blockedNonInitial && !blockedMonoKa
                                    && !IsExpressionBoundaryTheft(sentence.Words, i, 2))
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

                            // Katakana variants too (コレは → これは).
                            if (ExpressionExclusions.Contains(combinedText) ||
                                ExpressionExclusions.Contains(KanaConverter.ToHiragana(combinedText, convertLongVowelMark: false)))
                                continue;

                            matched = IsExpressionLookupMatch(combinedText);

                            if (!matched)
                                matched = IsMultiWordAdverbMatch(combinedText);

                            // Clusters tagged conj/prt/aux, not exp (それ|と|も), which the gates above miss.
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
                                bool prefixSafe = safeUpTo >= windowSize - 1;
                                bool prefixLenOk = cumLenOk[windowSize - 1];
                                long prefixHash = cumHash[windowSize - 1];
                                int prefixLen = cumLen[windowSize - 1];

                                if (PrefixTailMayMatch(prefixSafe, prefixLenOk, prefixHash, prefixLen, dictForm))
                                {
                                    var prefix = ConcatTokenTexts(sentence.Words, i, windowSize - 1);
                                    var dictCandidate = prefix + dictForm;
                                    if (IsExpressionLookupMatch(dictCandidate))
                                    {
                                        matched = true;
                                        combinedText = dictCandidate;
                                    }
                                }

                                // DictionaryForm skips to the base (取られ → 取る), missing entries like 呆気に取られる.
                                if (!matched && windowSize <= 4)
                                {
                                    string? prefix = null;
                                    foreach (var form in Deconjugator.Instance.Deconjugate(lastWord.Text))
                                    {
                                        if (form.Process.Length > 2 || form.Text == lastWord.Text || form.Text == dictForm)
                                            continue;
                                        if (!PrefixTailMayMatch(prefixSafe, prefixLenOk, prefixHash, prefixLen, form.Text))
                                            continue;
                                        prefix ??= ConcatTokenTexts(sentence.Words, i, windowSize - 1);
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
                            // Sudachi lexicalises なくなる as its own verb, so 元も子もなくなる needs its ない tail rebuilt.
                            else if (lastWord.PartOfSpeech == PartOfSpeech.Verb && lastWord.DictionaryForm == "なくなる")
                            {
                                var naiCandidate = ConcatTokenTexts(sentence.Words, i, windowSize - 1) + "ない";
                                if (IsExpressionLookupMatch(naiCandidate))
                                {
                                    matched = true;
                                    combinedText = naiCandidate;
                                }
                            }
                        }

                        // The completion paths rebuild combinedText (好まない → ことを好む) past the surface exclusion check.
                        if (matched
                            && (ExpressionExclusions.Contains(combinedText!)
                                || ExpressionExclusions.Contains(KanaConverter.ToHiragana(combinedText!, convertLongVowelMark: false))))
                            continue;

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
                                    && AnyWithPos(advWordIds, PartOfSpeech.Adverb))
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

                        if (result == null)
                        {
                            result = new List<(WordInfo word, int position, int length)>(sentence.Words.Count);
                            for (int j = 0; j < i; j++)
                                result.Add(sentence.Words[j]);
                        }
                        result.Add((combinedWord, sentence.Words[i].position, combinedLength));
                        i += bestMatch;
                    }
                    else
                    {
                        result?.Add(sentence.Words[i]);
                        i++;
                    }
                }

                if (result != null)
                    sentence.Words = result;
            }
        }

        private static bool IsExpressionLookupMatch(string text) =>
            HasLookupWhere(text, KanaFallback.ToHiraganaKeepLongVowelMark,
                           static ids => ids.Exists(static id => _expressionWordIds.Contains(id)));

        private static bool IsMultiWordAdverbMatch(string text) =>
            HasLookupWhere(text, KanaFallback.ToHiraganaKeepLongVowelMark,
                           static ids => AnyWithPos(ids, PartOfSpeech.Adverb));

        /// <summary>Clusters JMDict tags prt/conj/aux rather than exp (それとも, ものか).</summary>
        private static bool IsFunctionClusterMatch(string text) =>
            HasLookupWhere(text, KanaFallback.ToHiraganaKeepLongVowelMark,
                           static ids => AnyWithPos(ids, PartOfSpeech.Particle)
                                         || AnyWithPos(ids, PartOfSpeech.Conjunction)
                                         || AnyWithPos(ids, PartOfSpeech.Auxiliary));

        /// <summary>Rejects merges stealing a neighbour's token: a name's honorific (ヴァレリア様|に言わせれば) or だ of だろう.</summary>
        private static bool IsExpressionBoundaryTheft(
            List<(WordInfo word, int position, int length)> words, int start, int windowSize)
        {
            var first = words[start].word;
            if (start > 0 && TransitionRuleSets.HonorificSuffixes.Contains(first.Text)
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

        private static bool IsCohesiveFunctionWord(string text) =>
            HasLookupWhere(text, KanaFallback.ToHiraganaKeepLongVowelMark,
                           static ids => AnyWithPos(ids, PartOfSpeech.Auxiliary)
                                         || AnyWithPos(ids, PartOfSpeech.Adverb)
                                         || AnyWithPos(ids, PartOfSpeech.Conjunction)
                                         || AnyWithPos(ids, PartOfSpeech.Particle)
                                         || AnyWithPos(ids, PartOfSpeech.Expression));

        private static void CleanSentenceTokens(List<SentenceInfo> sentences)
        {
            foreach (var sentence in sentences)
            {
                foreach (var (word, _, _) in sentence.Words)
                {
                    // Symbols stay as boundary markers.
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

            var flatTokens = new List<WordInfo>();
            var sentenceInitial = new List<bool>();
            // Symbols in the gap before each token, so gates see its frame; whitespace is transparent, sentence ends are hard.
            var symbolsBefore = new List<string>();
            string pendingSymbols = "";
            foreach (var sentence in sentences)
            {
                bool first = true;
                // After an opening quote the next content token is initial, even after a speaker tag (【テオドール】「ああ).
                bool afterOpeningQuote = false;
                foreach (var (word, _, _) in sentence.Words)
                {
                    if (word.PartOfSpeech == PartOfSpeech.SupplementarySymbol)
                    {
                        if (word.Text.Length > 0 && OpeningQuoteChars.Contains(word.Text[^1]))
                            afterOpeningQuote = true;
                        pendingSymbols += word.Text;
                        continue;
                    }
                    if (word.PartOfSpeech == PartOfSpeech.BlankSpace)
                    {
                        // Whitespace keeps no gap of its own, or neighbour walks would double-count symbols.
                        flatTokens.Add(word);
                        sentenceInitial.Add(first || afterOpeningQuote);
                        symbolsBefore.Add("");
                        continue;
                    }
                    flatTokens.Add(word);
                    sentenceInitial.Add(first || afterOpeningQuote);
                    symbolsBefore.Add(pendingSymbols);
                    pendingSymbols = "";
                    first = false;
                    afterOpeningQuote = false;
                }
                pendingSymbols += "\n";
            }
            string trailingSymbols = pendingSymbols;

            var droppedByGate = new Dictionary<int, string>();
            int ci = 0;

            MisparseDecision EvaluateAt(int i, DeckWord deckWord)
            {
                var token = flatTokens[i];

                // Rewrite-rule pins (え|っつった) are never SFX; PreMatchedWordId alone isn't enough (ぐすっ → 具す would return).
                if (token.PinnedByRewriteRule && token.PreMatchedWordId != null)
                    return default;

                if (!MisparseGates.MayBeKanaFragment(token.Text)
                    && (deckWord.OriginalText.Length == 0
                        || !MisparseGates.MayBeKanaFragment(deckWord.OriginalText)))
                    return default;

                wordData.TryGetValue(deckWord.WordId, out var jmWord);
                var (isUk, hasKanji, readingIsIchi, attestsForm, attestsLiterally) = MisparseGates.GetWordFlags(
                    jmWord, deckWord.ReadingIndex,
                    deckWord.OriginalText.Length > 0 ? deckWord.OriginalText : token.Text);

                // Empty remnants are skipped as neighbours, but their symbol gaps still count.
                int pi = i - 1;
                bool spaceBefore = false;
                while (pi >= 0 && (flatTokens[pi].Text.Length == 0
                                   || flatTokens[pi].PartOfSpeech == PartOfSpeech.BlankSpace))
                {
                    if (flatTokens[pi].PartOfSpeech == PartOfSpeech.BlankSpace) spaceBefore = true;
                    pi--;
                }
                var prev = pi >= 0 ? flatTokens[pi] : null;

                int ni = i + 1;
                string symAfter = "";
                bool spaceAfter = false;
                while (ni < flatTokens.Count)
                {
                    symAfter += symbolsBefore[ni];
                    bool niIsBlank = flatTokens[ni].PartOfSpeech == PartOfSpeech.BlankSpace;
                    if (niIsBlank) spaceAfter = true;
                    if (flatTokens[ni].Text.Length > 0 && !niIsBlank) break;
                    ni++;
                }
                if (ni >= flatTokens.Count) symAfter += trailingSymbols;
                var next = ni < flatTokens.Count ? flatTokens[ni] : null;

                // A stutter-dedup drop's surviving twin is vocabulary (はいはい), not burst material.
                bool prevDropped = pi >= 0 && droppedByGate.TryGetValue(pi, out var prevGate)
                                   && prevGate != "kana-stutter-before-word";
                bool nextDropped = ni < flatTokens.Count && droppedByGate.TryGetValue(ni, out var nextGate)
                                   && nextGate != "kana-stutter-before-word";

                // Scraps flanking a token form one blob (ず|がんっ); a non-word blob has no word fragments.
                // Symbols split blobs (って、くっさ), and a vowel after a verb is its elongation (わかる|う).
                bool prevIsShard = (MisparseGates.IsKanaShardNeighbour(prev) || prevDropped)
                                   && symbolsBefore[i].Length == 0 && !spaceBefore;
                bool nextIsShard = (MisparseGates.IsKanaShardNeighbour(next) || nextDropped)
                                   && symAfter.Length == 0 && !spaceAfter;
                if (nextIsShard && next != null
                    && token.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective
                    && next.Text.All(c => "あいうえおぁぃぅぇぉーっッ".Contains(c)))
                    nextIsShard = false;
                bool blobUnattested = false;
                if (prevIsShard || nextIsShard)
                {
                    string blob = (prevIsShard ? prev!.Text : "")
                                  + (deckWord.OriginalText.Length > 0 ? deckWord.OriginalText : token.Text)
                                  + (nextIsShard ? next!.Text : "");
                    blobUnattested = !_lookups.ContainsKey(blob);
                }

                var ctx = new MisparseGateContext(token, deckWord, prev, next, isUk, hasKanji, readingIsIchi,
                    sentenceInitial[i],
                    symbolsBefore[i],
                    symAfter,
                    attestsForm,
                    attestsLiterally,
                    blobUnattested,
                    prevDropped,
                    nextDropped);

                return MisparseGates.Evaluate(in ctx);
            }

            var kept = new List<(int flatIdx, DeckWord word)>(corrected.Count);
            for (int i = 0; i < flatTokens.Count && ci < corrected.Count; i++)
            {
                if (flatTokens[i].ResolvedWordId == null) continue;

                var deckWord = corrected[ci++];
                var decision = EvaluateAt(i, deckWord);
                if (decision.IsMisparsed)
                {
                    droppedByGate[i] = decision.GateId ?? "";
                    diagnostics?.LogDroppedToken(flatTokens[i].Text, flatTokens[i].PartOfSpeech,
                        $"misparsed:{decision.GateId}");
                    continue;
                }

                kept.Add((i, deckWord));
            }

            // A drop can orphan its neighbour (ず|がんっ: がん falls, then ず), so re-check until stable.
            for (int pass = 0; pass < 3; pass++)
            {
                bool changed = false;
                for (int k = kept.Count - 1; k >= 0; k--)
                {
                    var (i, deckWord) = kept[k];
                    var decision = EvaluateAt(i, deckWord);
                    if (decision.IsMisparsed)
                    {
                        droppedByGate[i] = decision.GateId ?? "";
                        diagnostics?.LogDroppedToken(flatTokens[i].Text, flatTokens[i].PartOfSpeech,
                            $"misparsed:{decision.GateId}");
                        kept.RemoveAt(k);
                        changed = true;
                    }
                }
                if (!changed) break;
            }

            var result = new List<DeckWord>(corrected.Count);
            foreach (var (i, deckWord) in kept)
            {
                flatTokens[i].KeptForm = (deckWord.WordId, deckWord.ReadingIndex);
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
            // DictionaryForm may be an unreachable homograph (立って df=立てる → 役に立てる); deconjugation below finds 役に立つ.
            if (result.HasValue && dictForm != verb.Text && !DeconjBasesAllow(verb.Text, dictForm))
                result = null;
            if (result.HasValue) return result;

            if (verb.Text != dictForm)
            {
                result = TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, verb.Text, forceExpressionOnly,
                    tokenHashes[wordIndex], prefCumHash, prefCumLen);
                if (result.HasValue) return result;
            }

            // Classical 高き (df=高し) has the modern lemma only in NormalizedForm, which compounds use (気高い).
            if (verb.PartOfSpeech == PartOfSpeech.IAdjective
                && dictForm.EndsWith('し')
                && !string.IsNullOrEmpty(verb.NormalizedForm)
                && verb.NormalizedForm.EndsWith('い')
                && verb.NormalizedForm != dictForm)
            {
                result = TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, verb.NormalizedForm, forceExpressionOnly,
                    HiraRollingHash(verb.NormalizedForm), prefCumHash, prefCumLen);
                if (result.HasValue) return result;
            }

            if (dictForm is "する" or "ある" or "いく" or "行く" or "なる" or "くる" or "来る"
                && (verb.Text.Contains("ない") || verb.Text.Contains("なかっ") || verb.Text.Contains("なく")
                    || verb.Text.Contains("ねえ") || verb.Text.Contains("ねー") || verb.Text.Contains("ねぇ")
                    || verb.Text.Contains("ませ") || verb.Text.EndsWith('ん') || verb.Text.EndsWith('ず')))
            {
                // Idioms are keyed under ない, so 一筋縄では+いかん must be retried as いかない.
                var negForm = dictForm switch
                {
                    "する" => "しない",
                    "いく" => "いかない",
                    "行く" => "いかない",
                    "なる" => "ならない",
                    "くる" => "こない",
                    "来る" => "こない",
                    _ => "ない",
                };
                result = TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, negForm, forceExpressionOnly,
                    HiraRollingHash(negForm), prefCumHash, prefCumLen);
                if (result.HasValue) return result;
            }

            // ねぇ/ねー is never a key (ろくでも+ねぇ), so retry with ない; Sudachi normalizes these to 無い.
            if ((verb.PartOfSpeech == PartOfSpeech.IAdjective || verb.NormalizedForm is "無い" or "ない")
                && dictForm.Length >= 2
                && (dictForm.EndsWith("ねぇ", StringComparison.Ordinal)
                    || dictForm.EndsWith("ねえ", StringComparison.Ordinal)
                    || dictForm.EndsWith("ねー", StringComparison.Ordinal)))
            {
                var naiForm = dictForm[..^2] + "ない";
                result = TryMatchCompoundWindow(wordInfos, wordIndex, lastConsumedIndex, naiForm, forceExpressionOnly,
                    HiraRollingHash(naiForm), prefCumHash, prefCumLen);
                if (result.HasValue) return result;
            }

            if (dictForm != verb.Text && verb.Text.Length > dictForm.Length)
            {
                var deconj = Deconjugator.Instance.Deconjugate(verb.Text);
                var intermediates = _compoundScratchSet ??= new HashSet<string>(StringComparer.Ordinal);
                intermediates.Clear();
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

                var shortBaseForms = intermediates;
                shortBaseForms.Clear();
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

                var altBaseForms = intermediates;
                altBaseForms.Clear();
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
                // Particles over-merge as openers (とは); curated heads like しか (Vしか+ない) are expression-only.
                bool curatedParticleOpener = firstWord.PartOfSpeech == PartOfSpeech.Particle
                                             && ParticleExpressionOpeners.Contains(firstWord.Text);
                if (firstWord.PartOfSpeech is PartOfSpeech.Particle or PartOfSpeech.Interjection
                    && !curatedParticleOpener)
                    continue;
                if (firstWord is { PartOfSpeech: PartOfSpeech.Conjunction, Text.Length: 1 })
                    continue;
                if (firstWord.WasReclassifiedFromSuffix)
                    continue;
                // A name's honorific can't open a window (ヴァレリア様|に言わせれば ≠ ように言う).
                if (startIndex > 0 && TransitionRuleSets.HonorificSuffixes.Contains(firstWord.Text)
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

                bool expressionOnly = forceExpressionOnly || curatedParticleOpener
                    || firstWord.PartOfSpeech is PartOfSpeech.Suffix or PartOfSpeech.Auxiliary or PartOfSpeech.Adverb;

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
                    // A kanji-numeral span is a number, never a JMnedict name (四万, 二十三).
                    bool numeralSpan = true;
                    foreach (var c in candidateSpan)
                        if (!NumeralKanji.Contains(c) && c is not ('万' or '億' or '兆')) { numeralSpan = false; break; }
                    if (numeralSpan)
                        wordIds = wordIds.Where(id => !(WordMeta.TryGetValue(id, out var m)
                                                        && m.Pos.Length > 0
                                                        && m.Pos.All(p => p is PartOfSpeech.Name or PartOfSpeech.Unknown)))
                                         .ToList();
                    if (wordIds.Count > 0)
                    {
                        bool isKana = true;
                        foreach (var c in candidateSpan)
                            if (c is not ((>= 'ぁ' and <= 'ゟ') or (>= '゠' and <= 'ヿ'))) { isKana = false; break; }
                        (exprWordId, compWordId) = CompoundWordSelector.FindCompoundWordIds(wordIds, WordMeta, isKana);
                    }
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

                if (matchId.HasValue && ExpressionExclusionHashes.Contains(candidateHash))
                    continue;

                // A pronoun is never a suru-noun stem: 私+してない is not 私する "use for personal gain".
                if (matchId.HasValue && firstWord.PartOfSpeech == PartOfSpeech.Pronoun
                    && WordMeta.TryGetValue(matchId.Value, out var matchMeta)
                    && matchMeta.GetPrimaryPos() == PartOfSpeech.Verb)
                    continue;

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
            bool isSudachiPossibleDependant = false,
            bool isSudachiNameGuess = false)
        {
            var context = FormScoringContext.Create(
                                                    surface,
                                                    dictionaryForm,
                                                    normalizedForm,
                                                    isNameContext,
                                                    sudachiReading,
                                                    sudachiPOS: sudachiPOS,
                                                    isSudachiPossibleDependant: isSudachiPossibleDependant,
                                                    isSudachiNameGuess: isSudachiNameGuess);

            var result = FormCandidateSelector.PickTopCandidates(allCandidates, context, ArchaicPosTypes, diagnostics);
            return (result.Best, result.MarginToSecond);
        }

        #endregion

        #region Adjacent-word scoring

        // PreMatchedWordId is keyed: same-surface pins (帽子のツバ→鍔, ツバを飲む→唾) must not share a resolution.
        private static (string text, PartOfSpeech pos, string dictionaryForm, string reading, bool isPersonNameContext, bool isNameLike, int? preMatchedWordId)
            GetDedupKey(WordInfo wi) =>
            (wi.Text, wi.PartOfSpeech, wi.DictionaryForm, wi.Reading, wi.IsPersonNameContext,
             PosMapper.IsNameLikeSudachiNoun(wi.PartOfSpeech, wi.PartOfSpeechSection1,
                                             wi.PartOfSpeechSection2, wi.PartOfSpeechSection3),
             wi.PreMatchedWordId);

        private static (DeckWord? word, int? margin) LookupResult(
            WordInfo wi,
            Dictionary<(string, PartOfSpeech, string, string, bool, bool, int?), (DeckWord? word, int? margin)> resultLookup)
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

                // Partial hint ({開'あ}く): append the unhinted okurigana.
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
            Dictionary<(string, PartOfSpeech, string, string, bool, bool, int?), List<FormCandidate>>? candidateLookup = null,
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
            Dictionary<(string, PartOfSpeech, string, string, bool, bool, int?), (DeckWord? word, int? margin)> resultLookup,
            Dictionary<(string, PartOfSpeech, string, string, bool, bool, int?), List<FormCandidate>>? candidateLookup = null,
            ParserDiagnostics? diagnostics = null,
            FuriganaHint[]? relocatedHints = null)
        {
            var sentencePairs = BuildSentencePairs(sentences, wi => LookupResult(wi, resultLookup));
            return await ApplyAdjacentScoringCore(sentencePairs, candidateLookup, diagnostics, relocatedHints);
        }

        // State stands in for the dedup key: pass 1 builds exactly one RederiveState per dedup key.
        private readonly record struct ScoredCandidatesKey(
            RederivationHelper.RederiveState State,
            string NormalizedForm,
            bool IsPossibleDependant,
            bool IsArchaicSentence,
            bool IsSentenceInitial,
            bool IsSentenceFinal,
            bool NextIsCopula,
            bool AdmitCounters);

        private sealed record ScoredCandidates(List<FormCandidate> Candidates, FormScoringContext Context);

        private static FormCandidate? FindCandidate(List<FormCandidate> candidates, int wordId, byte readingIndex)
        {
            foreach (var c in candidates)
                if (c.Word.WordId == wordId && c.ReadingIndex == readingIndex)
                    return c;
            return null;
        }

        private static bool IsInfinitiveResult(DeckWord? word) =>
            word?.LastConjugation is "(infinitive)" or "(unstressed infinitive)";

        private static List<List<(WordInfo word, DeckWord? result, int? margin)>> BuildSentencePairs(
            List<SentenceInfo> sentences,
            Func<WordInfo, (DeckWord? word, int? margin)> lookupResult)
        {
            var result = new List<List<(WordInfo, DeckWord?, int?)>>(sentences.Count);
            foreach (var sentence in sentences)
            {
                var pairs = new List<(WordInfo, DeckWord?, int?)>(sentence.Words.Count);
                foreach (var (wordInfo, _, _) in sentence.Words)
                {
                    if (wordInfo.PartOfSpeech == PartOfSpeech.SupplementarySymbol) continue;
                    var (word, margin) = lookupResult(wordInfo);
                    pairs.Add((wordInfo, word, margin));
                }
                result.Add(pairs);
            }

            return result;
        }

        /// <summary>Kana なくなる is never 亡くなる "to die" (written in kanji); compounds like 耐え切れなくなった are untouched.</summary>
        private static List<FormCandidate>? DropImpossibleKanaReadingCandidates(List<FormCandidate>? candidates, WordInfo word)
        {
            if (candidates is not { Count: > 0 })
                return candidates;

            if (word.Text.StartsWith("なくな", StringComparison.Ordinal) && JapaneseTextHelper.IsAllHiragana(word.Text))
                return candidates.Where(c => c.Word.WordId != 1518540).ToList();

            return candidates;
        }

        private static async Task<List<DeckWord>> ApplyAdjacentScoringCore(
            List<List<(WordInfo word, DeckWord? result, int? margin)>> sentencePairs,
            Dictionary<(string, PartOfSpeech, string, string, bool, bool, int?), List<FormCandidate>>? candidateLookup = null,
            ParserDiagnostics? diagnostics = null,
            FuriganaHint[]? relocatedHints = null)
        {
            bool timing = ParserCounters.SectionTiming;
            long mark = timing ? Stopwatch.GetTimestamp() : 0;

            var allWordIds = new HashSet<int>();
            var rederiveStates = new RederivationHelper.RederiveState?[sentencePairs.Count][];
            var cachedCandidates = new List<FormCandidate>?[sentencePairs.Count][];
            var rederiveCache = new Dictionary<(string, PartOfSpeech, string, string, bool, bool, int?), RederivationHelper.RederiveState?>();
            var softRuleMemo = new Dictionary<TransitionRuleEngine.SoftRulePrefilterKey, bool>();

            // Surface text is immutable across both passes.
            var isClassicalBySentence = new bool[sentencePairs.Count];
            for (int si = 0; si < sentencePairs.Count; si++)
                isClassicalBySentence[si] = IsClassicalSentence(sentencePairs[si]);

            for (int si = 0; si < sentencePairs.Count; si++)
            {
                var sentenceWords = sentencePairs[si];
                bool isArchaicPass1 = isClassicalBySentence[si];
                for (int i = 0; i < sentenceWords.Count; i++)
                {
                    var (currentInfo, currentResult, currentMargin) = sentenceWords[i];
                    if (currentResult == null) continue;
                    Interlocked.Increment(ref ParserCounters.AdjTokens);

                    WordInfo? nextInfo = i < sentenceWords.Count - 1 ? sentenceWords[i + 1].word : null;
                    bool nextIsCopula = nextInfo != null && TransitionRuleSets.CopulaForms.Contains(nextInfo.Text);
                    bool nextIsForwardAnchor = nextInfo != null && TransitionRuleSets.ForwardAnchorSurfaces.Contains(nextInfo.Text);
                    bool prevNominalTarget = i > 0 && TransitionRuleSets.PrevNominalBoostSurfaces.Contains(currentInfo.Text);

                    // A counter homograph after a numeral (第二|話) stays rescorable; only pass 2 sees the numeral.
                    // A pick that is itself a numeral (ガンつく1|ワン) is already cohesive and must not reshuffle.
                    bool prevNumericCounter = i > 0
                        && AdjacentWordScorer.IsNumericSurface(sentenceWords[i - 1].word.Text)
                        && HasCounterLookup(currentInfo.Text)
                        && !currentResult.PartsOfSpeech.Contains(PartOfSpeech.Numeral);

                    var cachedHint = FindMatchingHint(relocatedHints, currentInfo);
                    bool hasHint = cachedHint != null;
                    bool isPreMatched = currentInfo.PreMatchedWordId.HasValue && currentInfo.PreMatchedCandidateWordIds == null;
                    if (isPreMatched && !hasHint) continue;

                    WordInfo? prevInfo = i > 0 ? sentenceWords[i - 1].word : null;
                    var prevResult = i > 0 ? sentenceWords[i - 1].result : null;
                    var nextResult = i < sentenceWords.Count - 1 ? sentenceWords[i + 1].result : null;

                    // An infinitive after の, a noun or another infinitive is suspect (群れ、群れ); chains cascade left to right.
                    bool infinitiveAfterNominal = IsInfinitiveResult(currentResult) && i > 0 &&
                        (prevInfo!.Text == "の"
                         || IsInfinitiveResult(prevResult)
                         || (prevResult != null && PosMask.Has(PosMask.NounLike, PosMask.FromList(prevResult.PartsOfSpeech))));

                    // A sentence-final verb may be an interjection homograph (来い "come!") the first pass can't see.
                    bool sentenceFinalVerb = i == sentenceWords.Count - 1
                        && currentResult.PartsOfSpeech.Contains(PartOfSpeech.Verb);

                    if (!isArchaicPass1 && !nextIsCopula && !nextIsForwardAnchor && !prevNominalTarget && !prevNumericCounter && !hasHint && !infinitiveAfterNominal && !sentenceFinalVerb
                        && ScoringPolicy.IsHighConfidence(currentMargin))
                    {
                        Interlocked.Increment(ref ParserCounters.AdjHighConfidenceSkips);
                        continue;
                    }

                    bool forceRederive = hasHint || nextIsForwardAnchor || prevNominalTarget || prevNumericCounter || ScoringPolicy.IsLowConfidence(currentMargin);

                    if (!forceRederive)
                        for (int k = Math.Max(0, i - 3); k < i; k++)
                        {
                            var kr = sentenceWords[k].result;
                            if (kr != null && TransitionRuleSets.NounVerbCollocations.ContainsKey(kr.WordId))
                            { forceRederive = true; break; }
                        }

                    // infinitiveAfterNominal bypasses the prefilter: its context may turn nominal only in pass 2.
                    if (!forceRederive && !nextIsCopula && !isArchaicPass1 && !infinitiveAfterNominal
                        && !TransitionRuleEngine.CouldAnySoftRuleApply(
                         currentResult.PartsOfSpeech, currentInfo.Text,
                         prevResult?.PartsOfSpeech, prevInfo?.Text,
                         nextResult?.PartsOfSpeech, nextInfo?.Text, softRuleMemo))
                    {
                        Interlocked.Increment(ref ParserCounters.AdjSoftRuleSkips);
                        continue;
                    }

                    var dedupKey = GetDedupKey(currentInfo);

                    if (candidateLookup != null && !hasHint)
                    {
                        if (candidateLookup.TryGetValue(dedupKey, out var fpCandidates))
                        {
                            (cachedCandidates[si] ??= new List<FormCandidate>?[sentenceWords.Count])[i] = fpCandidates;
                            Interlocked.Increment(ref ParserCounters.AdjFirstPassCandidates);
                            continue;
                        }
                    }

                    RederivationHelper.RederiveState? state;
                    if (!rederiveCache.TryGetValue(dedupKey, out state))
                    {
                        long t0 = timing ? Stopwatch.GetTimestamp() : 0;
                        state = RederivationHelper.CollectRederivationIds(currentInfo, _lookups, Deconjugator.Instance);
                        rederiveCache[dedupKey] = state;
                        if (timing) ParserCounters.AddSection(ParserCounters.Section.AdjCollectIds, t0);
                    }
                    if (state == null) continue;

                    (rederiveStates[si] ??= new RederivationHelper.RederiveState?[sentenceWords.Count])[i] = state;
                    Interlocked.Increment(ref ParserCounters.AdjRederived);
                    allWordIds.UnionWith(state.CandidateIds);
                }
            }

            if (timing) ParserCounters.Lap(ParserCounters.Section.AdjPass1, ref mark);

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

            if (timing) ParserCounters.Lap(ParserCounters.Section.AdjWordFetch, ref mark);

            int tokenCount = 0;
            foreach (var sentenceWords in sentencePairs)
                tokenCount += sentenceWords.Count;
            var corrected = new List<DeckWord>(tokenCount);
            int globalPos = 0;
            var bonusCache = new Dictionary<FormCandidate, (int bonus, List<string>? rules, int rubyBonus)>();
            // Shareable because base scores are context-free and the copula flag clear is keyed; bonuses stay per token.
            var scoredMemo = new Dictionary<ScoredCandidatesKey, ScoredCandidates>();

            for (int si = 0; si < sentencePairs.Count; si++)
            {
                var sentenceWords = sentencePairs[si];
                bool isArchaicSentence = isClassicalBySentence[si];
                var resolvedResults = new DeckWord?[sentenceWords.Count];
                for (int ri = 0; ri < sentenceWords.Count; ri++)
                    resolvedResults[ri] = sentenceWords[ri].result;

                for (int i = 0; i < sentenceWords.Count; i++)
                {
                    var (currentInfo, currentResult, currentMargin) = sentenceWords[i];
                    globalPos++;

                    if (currentResult == null)
                    {
                        diagnostics?.LogDroppedToken(currentInfo.Text, currentInfo.PartOfSpeech,
                            "Unresolved: no JMDict match found during lookup");
                        continue;
                    }

                    FuriganaHint? matchingHint = FindMatchingHint(relocatedHints, currentInfo);
                    bool tokenHasHint = matchingHint != null;
                    WordInfo? nextInfo = i < sentenceWords.Count - 1 ? sentenceWords[i + 1].word : null;
                    bool nextIsCopula = nextInfo != null && TransitionRuleSets.CopulaForms.Contains(nextInfo.Text);

                    List<FormCandidate>? candidates = null;
                    bool fromFirstPassCache = false;
                    bool memoHit = false;
                    bool hasMemoKey = false;
                    ScoredCandidatesKey memoKey = default;
                    FormScoringContext? memoContext = null;
                    if (cachedCandidates[si]?[i] is { } cached)
                    {
                        candidates = cached;
                        fromFirstPassCache = true;
                    }
                    else if (rederiveStates[si]?[i] is { } state)
                    {
                        // Kanji surfaces only: kana after a numeral is usually not a counter (何|か, not 箇/課).
                        bool admitCounters = i > 0
                            && AdjacentWordScorer.IsNumericSurface(sentenceWords[i - 1].word.Text)
                            && currentInfo.Text.Any(JapaneseTextHelper.IsKanji)
                            && HasCounterLookup(currentInfo.Text);

                        // Hinted tokens skip the memo: the hint changes the POS filter and is per position.
                        if (!tokenHasHint)
                        {
                            memoKey = new ScoredCandidatesKey(state, currentInfo.NormalizedForm,
                                currentInfo.HasPartOfSpeechSection(PartOfSpeechSection.PossibleDependant),
                                isArchaicSentence, i == 0, i == sentenceWords.Count - 1, nextIsCopula, admitCounters);
                            hasMemoKey = true;
                        }

                        if (hasMemoKey && scoredMemo.TryGetValue(memoKey, out var memo))
                        {
                            candidates = memo.Candidates;
                            memoContext = memo.Context;
                            memoHit = true;
                            Interlocked.Increment(ref ParserCounters.AdjMemoHits);
                        }
                        else
                        {
                            long t0 = timing ? Stopwatch.GetTimestamp() : 0;
                            candidates = RederivationHelper.BuildCandidatesFromWords(state, wordCache, skipPosFilter: tokenHasHint,
                                                                                     allowCounterCandidates: admitCounters);
                            ParserCounters.Add(ref ParserCounters.AdjCandidatesBuilt, candidates.Count);
                            if (timing) ParserCounters.AddSection(ParserCounters.Section.AdjBuildCandidates, t0);
                        }
                    }

                    // A kana surface can't resolve to a kanji-only homograph (kana なくなる ≠ 亡くなる).
                    if (!memoHit)
                        candidates = DropImpossibleKanaReadingCandidates(candidates, currentInfo);

                    if (candidates == null || candidates.Count == 0)
                    {
                        currentInfo.ResolvedWordId = currentResult.WordId;
                        corrected.Add(currentResult);
                        continue;
                    }

                    long tCtx = timing ? Stopwatch.GetTimestamp() : 0;
                    WordInfo? prevInfo = i > 0 ? sentenceWords[i - 1].word : null;
                    var prevResult = i > 0 ? resolvedResults[i - 1] : null;
                    var nextResult = i < sentenceWords.Count - 1 ? sentenceWords[i + 1].result : null;

                    var context = AdjacentWordScorer.AdjacentContext.Create(
                                                                         prevResult?.PartsOfSpeech,
                                                                         prevInfo?.Text,
                                                                         nextResult?.PartsOfSpeech,
                                                                         nextInfo?.Text);

                    // Adjacent context (archaic, sentence edges) only affects WordPriorityScorer; interior tokens keep traces.
                    bool skipRescore = fromFirstPassCache && !isArchaicSentence && i > 0 && i < sentenceWords.Count - 1;

                    var scoringContext = memoContext ?? FormScoringContext.Create(
                        currentInfo.Text, currentInfo.DictionaryForm, currentInfo.NormalizedForm,
                        currentInfo.IsPersonNameContext, currentInfo.Reading,
                        isArchaicSentence,
                        isSentenceInitial: i == 0,
                        isSentenceFinal: i == sentenceWords.Count - 1,
                        sudachiPOS: currentInfo.PartOfSpeech,
                        isSudachiPossibleDependant: currentInfo.HasPartOfSpeechSection(PartOfSpeechSection.PossibleDependant));

                    // 来い → int 2742070, not 来る's imperative; only this pass knows the token is sentence-final.
                    var sfInterjCurrent = scoringContext.IsSentenceFinal
                        ? FindCandidate(candidates, currentResult.WordId, currentResult.ReadingIndex)
                        : null;
                    bool interjectionFlip = sfInterjCurrent != null
                        && FormCandidateSelector.ApplySentenceFinalInterjection(sfInterjCurrent, candidates, scoringContext) != null;

                    // A confident sentence-final verb gets only the interjection override, never an adjacency flip (行けよ).
                    bool onlyInterjectionAllowed = scoringContext.IsSentenceFinal
                        && ScoringPolicy.IsHighConfidence(currentMargin)
                        && currentResult.PartsOfSpeech.Contains(PartOfSpeech.Verb);

                    Interlocked.Increment(ref ParserCounters.AdjTokensScored);
                    ParserCounters.Add(ref ParserCounters.AdjCandidatesScored, candidates.Count);
                    bool anyNonZeroBonus = false;
                    bonusCache.Clear();

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

                    // The next surface (前/後) selects a homograph reading the priority scorer would miss.
                    if (nextInfo != null)
                    {
                        foreach (var (targetWordId, anchor) in TransitionRuleSets.ForwardAnchorBoosts)
                        {
                            if (Array.IndexOf(anchor.NextAnchors, nextInfo.Text) >= 0)
                            {
                                collocMap ??= new Dictionary<int, int>();
                                collocMap[targetWordId] = collocMap.GetValueOrDefault(targetWordId) + anchor.Bonus;
                            }
                        }
                    }

                    // A nominal on the left selects the enumerating homograph (NといいNといい).
                    Dictionary<int, int>? prevNominalMap = null;
                    if (prevResult != null
                        && TransitionRuleSets.PrevNominalBoostSurfaces.Contains(currentInfo.Text)
                        && PosMask.Has(PosMask.NounLike, PosMask.FromList(prevResult.PartsOfSpeech)))
                    {
                        foreach (var (targetWordId, boost) in TransitionRuleSets.PrevNominalBoosts)
                        {
                            if (boost.Surface != currentInfo.Text) continue;
                            collocMap ??= new Dictionary<int, int>();
                            prevNominalMap ??= new Dictionary<int, int>();
                            collocMap[targetWordId] = collocMap.GetValueOrDefault(targetWordId) + boost.Bonus;
                            prevNominalMap[targetWordId] = boost.Bonus;
                        }
                    }

                    // Copula follows nominals only, so nouns must be eligible for the noun-copula synergy.
                    if (nextIsCopula)
                        foreach (var c in candidates)
                            c.IsPosIncompatibleDirectSurface = false;

                    var leftDictForm = prevInfo?.DictionaryForm;
                    var rightDictForm = nextInfo?.DictionaryForm;
                    var left2DictForm = i >= 2 ? sentenceWords[i - 2].word.DictionaryForm : null;
                    var right2DictForm = i < sentenceWords.Count - 2 ? sentenceWords[i + 2].word.DictionaryForm : null;

                    string? hintHiragana = matchingHint.HasValue
                        ? KanaScoringHelpers.ToNormalizedHiragana(matchingHint.Value.Reading, convertLongVowelMark: false)
                        : null;
                    if (timing) ParserCounters.AddSection(ParserCounters.Section.AdjContext, tCtx);
                    long tScore = timing ? Stopwatch.GetTimestamp() : 0;
                    bool needsBaseScore = !skipRescore && !memoHit;
                    foreach (var candidate in candidates)
                    {
                        if (needsBaseScore)
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
                            (rules ??= []).Add(prevNominalMap != null && prevNominalMap.ContainsKey(candidate.Word.WordId)
                                                   ? "prev-nominal-boost" : "noun-verb-collocation");
                        if (furiganaBonus != 0)
                            (rules ??= []).Add($"furigana-hint:{matchingHint!.Value.Reading}");
                        bonusCache[candidate] = (bonus, rules, rubyContextBonus);
                        if (bonus != 0) anyNonZeroBonus = true;
                    }

                    if (timing) ParserCounters.AddSection(ParserCounters.Section.AdjScoreCandidates, tScore);
                    if (hasMemoKey && !memoHit)
                        scoredMemo[memoKey] = new ScoredCandidates(candidates, scoringContext);

                    // Archaic context changes base scores and can flip the winner.
                    if (!anyNonZeroBonus && isArchaicSentence)
                    {
                        var phase2Best = candidates.MaxBy(c => c.TotalScore);
                        anyNonZeroBonus = phase2Best != null &&
                                          (phase2Best.Word.WordId != currentResult.WordId ||
                                           phase2Best.ReadingIndex != currentResult.ReadingIndex);
                    }

                    if (!interjectionFlip && (!anyNonZeroBonus || onlyInterjectionAllowed))
                    {
                        currentInfo.ResolvedWordId = currentResult.WordId;
                        corrected.Add(currentResult);
                        continue;
                    }

                    long tSel = timing ? Stopwatch.GetTimestamp() : 0;
                    Func<FormCandidate, int> getBonusFunc = c => bonusCache.TryGetValue(c, out var b) ? b.bonus : 0;

                    var newBest = FormCandidateSelector.PickTopCandidatesWithBonus(candidates, getBonusFunc);
                    newBest = FormCandidateSelector.RefineBest(newBest, candidates, scoringContext);
                    if (timing) ParserCounters.AddSection(ParserCounters.Section.AdjSelect, tSel);
                    int newBestBonus = newBest != null ? getBonusFunc(newBest) : 0;
                    int newBestAdjusted = newBest != null ? ScoringPolicy.EffectiveScore(newBest) + newBestBonus : int.MinValue;

                    bool changed = newBest != null &&
                                   (newBest.Word.WordId != currentResult.WordId || newBest.ReadingIndex != currentResult.ReadingIndex);

                    if (diagnostics != null && newBest != null &&
                        bonusCache.TryGetValue(newBest, out var dnb) && dnb.rules is { Count: > 0 })
                    {
                        var firstPassCandidate = FindCandidate(candidates, currentResult.WordId, currentResult.ReadingIndex);
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
                        Interlocked.Increment(ref ParserCounters.AdjTokensChanged);
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
                                            PartsOfSpeech = [..newBest.Word.CachedPOS], Origin = newBest.Word.Origin,
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

            if (timing) ParserCounters.Lap(ParserCounters.Section.AdjPass2, ref mark);
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
            Dictionary<(string, PartOfSpeech, string, string, bool, bool, int?), (DeckWord? word, int? margin)> resultLookup)
        {
            int tokenCount = 0;
            foreach (var sentence in sentences)
                tokenCount += sentence.Words.Count;
            var map = new Dictionary<(int, int), int?>(tokenCount);
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