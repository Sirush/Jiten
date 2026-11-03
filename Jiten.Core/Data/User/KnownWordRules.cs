using Jiten.Core.Data.FSRS;

namespace Jiten.Core.Data;

/// <summary>What a card or a word-set membership says the user knows; SQL that cannot call these members mirrors them and cites them.</summary>
public static class KnownWordRules
{
    /// <summary>Measured on the scheduled interval (due minus last review), not on time elapsed since the review.</summary>
    public const int MatureIntervalDays = RetentionCalculator.MatureThresholdDays;

    public static readonly TimeSpan MatureInterval = TimeSpan.FromDays(MatureIntervalDays);

    public static bool IsMatureInterval(DateTime due, DateTime? lastReview) =>
        lastReview is { } reviewed && due - reviewed >= MatureInterval;

    /// <summary>Mastered and Blacklisted are terminal; a New card is back in the new queue even after a reset kept its last review; a suspended card keeps the tier its interval earned.</summary>
    public static KnownState CardTier(FsrsState state, DateTime due, DateTime? lastReview) => state switch
    {
        FsrsState.Mastered => KnownState.Mastered,
        FsrsState.Blacklisted => KnownState.Blacklisted,
        FsrsState.New => KnownState.New,
        _ when lastReview == null => KnownState.New,
        _ => IsMatureInterval(due, lastReview) ? KnownState.Mature : KnownState.Young
    };

    /// <summary>Only applies to a form without a card: a card of any state outranks set membership.</summary>
    public static KnownState WordSetTier(WordSetStateType state) =>
        state == WordSetStateType.Mastered ? KnownState.Mastered : KnownState.Blacklisted;

    /// <summary>Precedence when several sources describe one form, such as two covering forms; any other state ranks 0.</summary>
    public static int FormRank(KnownState tier) => tier switch
    {
        KnownState.Mastered => 4,
        KnownState.Blacklisted => 3,
        KnownState.Mature => 2,
        KnownState.Young => 1,
        _ => 0
    };

    /// <summary>Learned knowledge; a blacklisted word was opted out of, not learned.</summary>
    public static bool IsLearned(KnownState tier) => tier is KnownState.Young or KnownState.Mature or KnownState.Mastered;

    /// <summary>A word's tier across its forms: any learned form outranks Blacklisted, which wins only over forms the user does not know.</summary>
    public static KnownState WordTier(IEnumerable<KnownState> formTiers)
    {
        var best = KnownState.New;
        var blacklisted = false;
        foreach (var tier in formTiers)
        {
            if (tier == KnownState.Blacklisted)
                blacklisted = true;
            else if (IsLearned(tier) && FormRank(tier) > FormRank(best))
                best = tier;
        }

        return best == KnownState.New && blacklisted ? KnownState.Blacklisted : best;
    }
}
