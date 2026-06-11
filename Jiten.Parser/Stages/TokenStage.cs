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
            // Fused katakana-mora theft (ケン|カって): single Xって token with a katakana head,
            // handled by RepairQuotativeTte alongside the two-token Xっ|て shape.
            if (text.Length == 3 && text[0] is >= 'ァ' and <= 'ヺ'
                && text.EndsWith("って", StringComparison.Ordinal))
                f |= TokenFeatures.EndsWithTsu;

            switch (text)
            {
                case "たん" when w.PartOfSpeech == PartOfSpeech.Suffix:
                    f |= TokenFeatures.TextTanSuffix;
                    break;
                case "たんか" when w.PartOfSpeech == PartOfSpeech.Noun:
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
                case "らん":
                    f |= TokenFeatures.TextRan;
                    break;
            }

            if (w.Text.Length >= 4
                && w.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Interjection or PartOfSpeech.Filler
                && (f & TokenFeatures.OovGarbage) == 0)
            {
                bool allHira = true;
                foreach (var c in w.Text)
                    if (c is < '぀' or > 'ゟ') { allHira = false; break; }
                if (allHira)
                    f |= TokenFeatures.OovGarbage;
            }
        }

        return f;
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
