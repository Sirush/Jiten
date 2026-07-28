using Jiten.Core.Data.Billing;

namespace Jiten.Core.Services;

/// <summary>A limit that a Jiten+ tier (Trial or Full alike) raises above the free allowance.</summary>
public sealed class TieredLimit
{
    public int Free { get; set; }

    public int Plus { get; set; }

    public int ForTier(JitenPlusTier tier) => tier == JitenPlusTier.None ? Free : Plus;
}

/// <summary>
/// Per-tier collection limits, bound from the <c>JitenPlus:Limits</c> config section so they can be
/// retuned per environment without a code change. Limits gate growth only: a user who ends up above
/// their allowance (tier lapse, lowered free limit) keeps everything and is merely blocked from adding.
/// </summary>
public sealed class JitenPlusLimitsOptions
{
    public const string SectionName = "JitenPlus:Limits";

    public TieredLimit StudyDecks { get; set; } = new() { Free = 60, Plus = 200 };

    /// <summary>Total words across all of a user's word-list study decks.</summary>
    public TieredLimit StudyDeckWords { get; set; } = new() { Free = 150_000, Plus = 300_000 };

    /// <summary>Words accepted in a single word-list import.</summary>
    public TieredLimit ImportWords { get; set; } = new() { Free = 50_000, Plus = 100_000 };

    public TieredLimit ActiveMediaRequests { get; set; } = new() { Free = 20, Plus = 30 };

    public TieredLimit CustomSentencesPerWord { get; set; } = new() { Free = 3, Plus = 10 };

    public TieredLimit Roadmaps { get; set; } = new() { Free = 0, Plus = 50 };
}
