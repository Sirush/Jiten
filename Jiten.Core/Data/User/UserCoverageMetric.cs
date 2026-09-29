namespace Jiten.Core.Data;

public enum UserCoverageMetric : short
{
    MatureCoverage = 1,
    MatureUniqueCoverage = 2,
    YoungCoverage = 3,
    YoungUniqueCoverage = 4,

    /// <summary>Jiten+ only: sentences with every content word known, in basis points; -1 = no sentence profile yet.</summary>
    ReadableSentences = 5,

    /// <summary>Jiten+ only: sentences with exactly one unknown content word, in basis points; -1 = no sentence profile yet.</summary>
    IPlusOneSentences = 6
}

