namespace Jiten.Parser.Scoring;

/// <summary>
/// Scalar scoring constants shared across scorers.
/// Tuned empirically against the parser test suite.
/// </summary>
internal static class Constants
{
    /// <summary>
    /// Score penalty charged per character in a span that is not covered by any
    /// dictionary token. Lets the optimiser prefer fully-covered segmentations
    /// while still tolerating small uncovered regions (proper names, onomatopoeia,
    /// typos) when nothing better is available. Mirrors Ichiran's <c>*gap-penalty*</c>.
    /// </summary>
    public const int UncoveredCharPenalty = 500;

    /// <summary>
    /// Penalty magnitude for grammatical adjacencies that are effectively impossible
    /// in modern Japanese (e.g. two copulas in a row). Large enough to dominate any
    /// realistic base-score margin, so the adjacency wins only if nothing else parses.
    /// Used as the <c>Delta</c> on Forbidden-grade soft rules.
    /// </summary>
    public const int ForbiddenPenalty = -3000;
}
