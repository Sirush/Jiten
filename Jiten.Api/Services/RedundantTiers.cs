using System.Collections.Frozen;
using Jiten.Api.Helpers;

namespace Jiten.Api.Services;

public sealed class RedundantTiers(
    Dictionary<long, VocabularyTier> cardDriven,
    FrozenDictionary<long, VocabularyTier> setDriven,
    string setDrivenCacheKey,
    HashSet<long> cardKeys)
{
    public static readonly RedundantTiers Empty = new([], FrozenDictionary<long, VocabularyTier>.Empty, "", []);

    public IReadOnlyDictionary<long, VocabularyTier> CardDriven => cardDriven;

    public FrozenDictionary<long, VocabularyTier> SetDriven => setDriven;

    /// <summary>Identifies <see cref="SetDriven"/>, for caches of counts derived from it.</summary>
    public string SetDrivenCacheKey => setDrivenCacheKey;

    public bool TryGetTier(long key, out VocabularyTier tier)
    {
        if (cardDriven.TryGetValue(key, out tier)) return true;
        if (cardKeys.Contains(key))
        {
            tier = default;
            return false;
        }

        return setDriven.TryGetValue(key, out tier);
    }

    /// <summary>Set-driven entries this user's own cards or card-driven tiers replace; counts taken over the shared layer must take these back out.</summary>
    public IEnumerable<(long Key, VocabularyTier Tier)> ShadowedSetDriven()
    {
        if (setDriven.Count == 0) yield break;

        foreach (var key in cardKeys)
            if (setDriven.TryGetValue(key, out var tier))
                yield return (key, tier);

        foreach (var key in cardDriven.Keys)
            if (setDriven.TryGetValue(key, out var tier))
                yield return (key, tier);
    }
}
