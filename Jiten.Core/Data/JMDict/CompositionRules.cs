namespace Jiten.Core.Data.JMDict;

public enum CompositionGate
{
    Compound,
    Expression,
    Phrase
}

public static class CompositionRules
{
    public static readonly string[] GrammaticalTags = ["prt", "aux", "aux-v", "aux-adj", "cop", "conj"];

    public static bool IsGrammatical(PartOfSpeech pos) =>
        pos is PartOfSpeech.Particle or PartOfSpeech.Auxiliary or PartOfSpeech.Conjunction;

    /// <summary>A particle-bearing split is only kept for JMDict `exp` parents; on any other parent it is a free phrase, not a compound.</summary>
    public static CompositionGate Classify(IEnumerable<string> parentTags, IReadOnlyCollection<PartOfSpeech> morphemes)
    {
        if (!morphemes.Any(IsGrammatical))
            return CompositionGate.Compound;

        return parentTags.Contains("exp") && morphemes.Any(p => !IsGrammatical(p))
            ? CompositionGate.Expression
            : CompositionGate.Phrase;
    }
}
