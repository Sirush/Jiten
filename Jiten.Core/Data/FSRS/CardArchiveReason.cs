namespace Jiten.Core.Data.FSRS;

/// <summary>Why a card left the collection. Append-only, never renumber (persisted as byte).</summary>
public enum CardArchiveReason : byte
{
    Unknown = 0,
    KanaRedundancy = 1,
    FormPrune = 2,
    RedundancyResolve = 3,
    Forget = 4,
    BulkForget = 5,
    MassAction = 6,
    WordReplacementMerge = 7,
}

public static class CardArchiveReasonExtensions
{
    /// <summary>
    /// True for removals the system chose on the user's behalf, which are the only ones re-adding the form
    /// restores silently. A removal the user asked for must not resurrect itself.
    /// Mirrored as a SQL predicate in <c>CardRestoreService.AutoRestoreAsync</c>; change both together.
    /// </summary>
    public static bool IsAutoRestorable(this CardArchiveReason reason)
        => reason is CardArchiveReason.KanaRedundancy or CardArchiveReason.FormPrune;
}
