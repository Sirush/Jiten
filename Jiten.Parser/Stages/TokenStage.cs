using Jiten.Core;
using Jiten.Core.Data;

namespace Jiten.Parser;

internal enum TokenStageGroup
{
    Split,
    Repair,
    Combine,
    Cleanup,
    Disambiguation
}

[Flags]
internal enum TokenFeatures : uint
{
    None            = 0,

    // POS-based
    Prefix          = 1 << 0,
    Suffix          = 1 << 1,
    Auxiliary        = 1 << 2,
    Interjection    = 1 << 3,

    // POS section-based
    AuxVerbStem     = 1 << 4,
    ConjParticle    = 1 << 5,
    NumericAmount   = 1 << 6,
    AdvParticle     = 1 << 7,
    Dependant       = 1 << 8,
    VerbLike        = 1 << 9,

    // Text patterns
    LongVowelMark   = 1 << 10,
    EndsWithTsu     = 1 << 11,
    TextTanSuffix   = 1 << 12,
    TextTanka       = 1 << 13,
    TextHasa        = 1 << 14,
    TextTawake      = 1 << 15,
    TextTatte       = 1 << 16,
    TextRan         = 1 << 17,
    OovGarbage      = 1 << 19,
    TextSakki       = 1 << 20,
    KanaRepetition  = 1 << 21,
    DictKiru        = 1 << 22,
    HiraganaOovBlob = 1 << 23,
    AdverbEndsTo    = 1 << 24,
    TextTobashi     = 1 << 25,
    VerbKaeru       = 1 << 26,
    GeminateSuffixShape = 1 << 27,
    KatakanaRun     = 1 << 28,
    CompoundBoundaryShape = 1 << 29,
    SingleKanjiNoun = 1u << 30,

    // Composite
    InflectableBase = 1 << 18,
}

internal sealed class TokenStage(
    string name,
    TokenStageGroup group,
    Func<List<WordInfo>, List<WordInfo>> process,
    TokenFeatures requiredFeatures = TokenFeatures.None,
    Func<List<WordInfo>, IReadOnlyList<int>, List<WordInfo>>? candidateProcess = null)
{
    public string Name { get; } = name;
    public TokenStageGroup Group { get; } = group;
    public TokenFeatures RequiredFeatures { get; } = requiredFeatures;
    public bool UsesCandidatePositions => candidateProcess != null;
    public List<WordInfo> Apply(List<WordInfo> input, TokenFeatureScan? scan = null) =>
        candidateProcess == null
            ? process(input)
            : candidateProcess(input,
                (scan ?? TokenFeatureScanner.ScanWithCandidates(input)).Candidates(RequiredFeatures));
}

internal sealed class TokenFeatureScan(TokenFeatures features, Dictionary<TokenFeatures, List<int>> candidates)
{
    public TokenFeatures Features { get; } = features;

    public IReadOnlyList<int> Candidates(TokenFeatures feature) =>
        candidates.TryGetValue(feature, out var positions) ? positions : [];
}

internal static class TokenFeatureScanner
{
    public static TokenFeatures Scan(List<WordInfo> tokens) => ScanCore(tokens, null);

    public static TokenFeatureScan ScanWithCandidates(List<WordInfo> tokens)
    {
        var candidates = new Dictionary<TokenFeatures, List<int>>(4);
        return new TokenFeatureScan(ScanCore(tokens, candidates), candidates);
    }

    private static TokenFeatures ScanCore(List<WordInfo> tokens,
                                          Dictionary<TokenFeatures, List<int>>? candidates)
    {
        var f = TokenFeatures.None;
        string prevText = "";

        void AddCandidate(TokenFeatures feature, int position)
        {
            f |= feature;
            if (candidates == null) return;
            if (!candidates.TryGetValue(feature, out var positions))
                candidates[feature] = positions = [];
            if (positions.Count == 0 || positions[^1] != position)
                positions.Add(position);
        }

        for (int index = 0; index < tokens.Count; index++)
        {
            var w = tokens[index];
            switch (w.PartOfSpeech)
            {
                case PartOfSpeech.Prefix:       f |= TokenFeatures.Prefix; break;
                case PartOfSpeech.Suffix:        f |= TokenFeatures.Suffix; break;
                case PartOfSpeech.Auxiliary:      f |= TokenFeatures.Auxiliary; break;
                case PartOfSpeech.Interjection:  f |= TokenFeatures.Interjection; break;
                case PartOfSpeech.Verb:
                case PartOfSpeech.IAdjective:
                case PartOfSpeech.NaAdjective:   f |= TokenFeatures.InflectableBase; break;
            }

            f |= SectionFeature(w.PartOfSpeechSection1);
            f |= SectionFeature(w.PartOfSpeechSection2);
            f |= SectionFeature(w.PartOfSpeechSection3);

            var text = w.Text;

            if (text.Contains('ー'))
                f |= TokenFeatures.LongVowelMark;
            // ッ included: RepairClippedAdjective accepts a katakana sokuon token too.
            if (text.Length > 0 && text[^1] is 'っ' or 'ッ')
                f |= TokenFeatures.EndsWithTsu;

            // Candidate positions are needed only immediately before the structural block. Normal
            // feature rescans skip these extra string walks; the pipeline requests a candidate scan
            // before it evaluates the four stages.
            if (candidates != null)
            {
                if (text.EndsWith('っ') || text.EndsWith("っぱ", StringComparison.Ordinal)
                                        || text.EndsWith("っぷ", StringComparison.Ordinal))
                    AddCandidate(TokenFeatures.GeminateSuffixShape, index);
                if (text.Length > 0 && text.All(JapaneseTextHelper.IsKatakanaWordChar))
                    AddCandidate(TokenFeatures.KatakanaRun, index);
                if (text.Length is 2 or 3 && "上中内外前後間下先際的".Contains(text[^1]))
                {
                    if (index > 0)
                        AddCandidate(TokenFeatures.CompoundBoundaryShape, index - 1);
                }
                if (w.PartOfSpeech == PartOfSpeech.Expression && text.Length == 3
                    && text[..2] is "その" or "この" or "あの" or "どの"
                    && JapaneseTextHelper.IsKanji(text[2]))
                    AddCandidate(TokenFeatures.CompoundBoundaryShape, index);
                // Verb-tagged bare kanji enter too: a stranded okurigana leaves the stem tagged
                // either way (探[Noun]|しっス vs 捜[Verb]|しっ|ス).
                if (text.Length == 1 && JapaneseTextHelper.IsKanji(text[0])
                    && w.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Verb)
                    AddCandidate(TokenFeatures.SingleKanjiNoun, index);
            }
            // Fused-mora theft where って rides inside a kana/kanji-headed token (ケン|カって, エリ|アっての,
            // 結|果って[果て], 偶|然って[然て], 寒|さって[さて], 婆|さ|んって[んて]), handled by RepairQuotativeTte
            // alongside the two-token Xっ|て shape. Contains (not EndsWith) catches an idiom-fused tail too
            // (アっての = ア+って+の → あっての). The hiragana head covers さ/ん-mora theft; a standalone
            // quotative って (Particle) is 2 chars so it never trips this.
            if (text.Length >= 3 && (text[0] is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヺ' or >= '一' and <= '鿿')
                && text.Contains("って", StringComparison.Ordinal))
                f |= TokenFeatures.EndsWithTsu;

            switch (text)
            {
                case "たん" when w.PartOfSpeech == PartOfSpeech.Suffix:
                    f |= TokenFeatures.TextTanSuffix;
                    break;
                case "たんか" when w.PartOfSpeech == PartOfSpeech.Noun:
                    f |= TokenFeatures.TextTanka;
                    break;
                // たか mis-tokenised as 鷹/高 when it's past た + question か (言い過ぎ|たか) — same repair stage
                case "たか" when w.PartOfSpeech == PartOfSpeech.Noun:
                    f |= TokenFeatures.TextTanka;
                    break;
                case "はさ" when w.PartOfSpeech == PartOfSpeech.Noun:
                    f |= TokenFeatures.TextHasa;
                    break;
                case "たわけ":
                    f |= TokenFeatures.TextTawake;
                    break;
                case "たって" or "だって" when w.HasPartOfSpeechSection(PartOfSpeechSection.ConjunctionParticle):
                    f |= TokenFeatures.TextTatte;
                    break;
                case "だな" when w.PartOfSpeech == PartOfSpeech.Noun:
                    f |= TokenFeatures.TextTatte;
                    break;
                case "かって" when w.PartOfSpeech == PartOfSpeech.Adverb:
                    f |= TokenFeatures.TextTatte;
                    break;
                case "いたって" when w.PartOfSpeech == PartOfSpeech.Adverb:
                    f |= TokenFeatures.TextTatte;
                    break;
                case "らん":
                    f |= TokenFeatures.TextRan;
                    break;
                case "さっ":
                    f |= TokenFeatures.TextSakki;
                    break;
                // 飛ばし heads the counter-compound 段飛ばし; RepairDanTobashi re-checks the numeral+段 head.
                case "飛ばし":
                    f |= TokenFeatures.TextTobashi;
                    break;
                // The emphatic prefix ど arrives as Adverb (truncated どう) — CombinePrefixes
                // re-checks precisely.
                case "ど" when w.PartOfSpeech == PartOfSpeech.Adverb:
                    f |= TokenFeatures.Prefix;
                    break;
            }

            if (w.DictionaryForm == "切る")
                f |= TokenFeatures.DictKiru;

            // A verb normalised to X返る is a candidate intensifier compound; RepairIntensifierKaeru
            // splits only the OOV ones (a real 静まり返る is a JMDict entry and is excluded there).
            if (w.PartOfSpeech == PartOfSpeech.Verb && w.NormalizedForm.EndsWith("返る", StringComparison.Ordinal))
                f |= TokenFeatures.VerbKaeru;

            // SplitUnattestedToAdverbs only acts on adverb tokens ending in と (凛と, 堂々と).
            if (w.PartOfSpeech == PartOfSpeech.Adverb && text.Length >= 2 && text[^1] == 'と')
                f |= TokenFeatures.AdverbEndsTo;

            // A 2-mora-unit kana repetition long enough to be a collapsible run by itself
            // (ごろごろごろ), or continuing the previous token's unit (ごろごろ|ごろ) — a cheap
            // superset of what CollapseReduplicatedMimetic can act on (it needs 3+ units total).
            if ((f & TokenFeatures.KanaRepetition) == 0 && IsUnitRepetition(text))
            {
                if (text.Length >= 6
                    || (prevText.Length >= 2 && IsUnitRepetition(prevText)
                        && prevText[0] == text[0] && prevText[1] == text[1]))
                    f |= TokenFeatures.KanaRepetition;
            }
            prevText = text;

            if (w.Text.Length >= 3
                && w.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Interjection or PartOfSpeech.Filler
                && (f & TokenFeatures.OovGarbage) == 0)
            {
                bool allHira = true;
                foreach (var c in w.Text)
                    if (c is < '぀' or > 'ゟ') { allHira = false; break; }
                if (allHira)
                    f |= TokenFeatures.OovGarbage;
            }

            // RetokeniseOovBlobs only re-cuts long hiragana(+ー) noun blobs.
            if (w.Text.Length >= 5
                && w.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                && (f & TokenFeatures.HiraganaOovBlob) == 0)
            {
                bool hiraBlob = true;
                foreach (var c in w.Text)
                    if (c is (< '぀' or > 'ゟ') and not 'ー') { hiraBlob = false; break; }
                if (hiraBlob)
                    f |= TokenFeatures.HiraganaOovBlob;
            }
        }

        return f;
    }

    // Even-length kana text that is its own leading 2-char unit repeated (ごろ, ごろごろ, ぐるぐるぐる).
    // Character test is a cheap kana-range superset; the consuming stage re-checks precisely.
    private static bool IsUnitRepetition(string s)
    {
        if (s.Length < 2 || (s.Length & 1) != 0) return false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is < '぀' or > 'ヿ') return false;
            if (i >= 2 && c != s[i & 1]) return false;
        }
        return true;
    }

    private static TokenFeatures SectionFeature(PartOfSpeechSection section) => section switch
    {
        PartOfSpeechSection.AuxiliaryVerbStem  => TokenFeatures.AuxVerbStem,
        PartOfSpeechSection.ConjunctionParticle => TokenFeatures.ConjParticle,
        PartOfSpeechSection.Amount              => TokenFeatures.NumericAmount,
        PartOfSpeechSection.Numeral             => TokenFeatures.NumericAmount,
        PartOfSpeechSection.AdverbialParticle   => TokenFeatures.AdvParticle,
        PartOfSpeechSection.Dependant           => TokenFeatures.Dependant,
        PartOfSpeechSection.PossibleDependant   => TokenFeatures.Dependant,
        PartOfSpeechSection.VerbLike            => TokenFeatures.VerbLike | TokenFeatures.InflectableBase,
        PartOfSpeechSection.Suffix              => TokenFeatures.Suffix,
        PartOfSpeechSection.PossibleSuru        => TokenFeatures.InflectableBase,
        PartOfSpeechSection.PossibleVerbSuruNoun => TokenFeatures.InflectableBase,
        _                                       => TokenFeatures.None,
    };
}
