namespace Jiten.Core.Data;

public static class SentenceComprehension
{
    /// <summary>Learning cards (Due with no review) and plain New do not count; a blacklisted word is one the user chose not to learn.</summary>
    public static bool IsKnown(IReadOnlyCollection<KnownState> states) =>
        states.Any(s => s is KnownState.Young or KnownState.Mature or KnownState.Mastered
                             or KnownState.Redundant or KnownState.Blacklisted);

    /// <summary>JMnedict entries; custom entries from 8,000,000 up are vocabulary and still count.</summary>
    public static bool IsNameEntry(int wordId) => wordId is >= 5_000_000 and < 8_000_000;

    /// <summary>Every occurrence of an unknown content word other than the target; particles, auxiliaries and names never count.</summary>
    public static IEnumerable<SentenceToken> UnknownTokens(IEnumerable<SentenceToken> tokens, int targetWordId, Func<int, byte, bool> isKnown) =>
        tokens.Where(t => !t.IsFunctionWord && t.WordId != targetWordId && !IsNameEntry(t.WordId) && !isKnown(t.WordId, t.ReadingIndex));

    /// <summary>Distinct unknown content words other than the target word, as <see cref="UnknownTokens"/> finds them.</summary>
    public static int CountUnknown(IEnumerable<SentenceToken> tokens, int targetWordId, Func<int, byte, bool> isKnown) =>
        UnknownTokens(tokens, targetWordId, isKnown).Select(t => t.WordKey).Distinct().Count();
}
