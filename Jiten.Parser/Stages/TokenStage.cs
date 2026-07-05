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
    AItsumo         = 1 << 21,
    KanaRepetition  = 1 << 22,
    DictKiru        = 1 << 23,
    HiraganaOovBlob = 1 << 24,
    AdverbEndsTo    = 1 << 25,

    // Composite
    InflectableBase = 1 << 18,
}

internal sealed class TokenStage(
    string name,
    TokenStageGroup group,
    Func<List<WordInfo>, List<WordInfo>> process,
    TokenFeatures requiredFeatures = TokenFeatures.None)
{
    public string Name { get; } = name;
    public TokenStageGroup Group { get; } = group;
    public TokenFeatures RequiredFeatures { get; } = requiredFeatures;
    public List<WordInfo> Apply(List<WordInfo> input) => process(input);
}

internal static class TokenFeatureScanner
{
    public static TokenFeatures Scan(List<WordInfo> tokens)
    {
        var f = TokenFeatures.None;
        bool sawAInterjection = false, sawItsumo = false;
        string prevText = "";

        foreach (var w in tokens)
        {
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
            if (text.Length > 0 && text[^1] == 'っ')
                f |= TokenFeatures.EndsWithTsu;
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
                case "あ" when w.PartOfSpeech == PartOfSpeech.Interjection:
                    sawAInterjection = true;
                    break;
                case "いつも":
                    sawItsumo = true;
                    break;
            }

            if (w.DictionaryForm == "切る")
                f |= TokenFeatures.DictKiru;

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

        if (sawAInterjection && sawItsumo)
            f |= TokenFeatures.AItsumo;

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
