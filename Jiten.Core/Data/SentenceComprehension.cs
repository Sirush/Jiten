namespace Jiten.Core.Data;

public static class SentenceComprehension
{
    /// <summary>Learning cards (Due with no review) and plain New do not count; a blacklisted word is one the user chose not to learn.</summary>
    public static bool IsKnown(IReadOnlyCollection<KnownState> states) =>
        states.Any(s => s is KnownState.Young or KnownState.Mature or KnownState.Mastered
                             or KnownState.Redundant or KnownState.Blacklisted);

    /// <summary>Distinct unknown content words other than the target word; particles and auxiliaries never count.</summary>
    public static int CountUnknown(IEnumerable<SentenceToken> tokens, int targetWordId, Func<int, byte, bool> isKnown)
    {
        var unknown = new HashSet<int>();
        foreach (var token in tokens)
        {
            if (token.IsFunctionWord || token.WordId == targetWordId) continue;
            if (!isKnown(token.WordId, token.ReadingIndex))
                unknown.Add(token.WordKey);
        }

        return unknown.Count;
    }
}
