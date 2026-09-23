using Jiten.Api.Dtos;
using Jiten.Core.Data.FSRS;

namespace Jiten.Api.Helpers;

public static class LeechHelper
{
    /// <summary>
    /// A card is an active leech when its lapse count has reached the user's leech threshold and it
    /// hasn't recovered since (days at 90% recall below the mature threshold). Derived on the fly, so
    /// changing the threshold retroactively updates which cards are flagged.
    /// Mirrored client-side in Jiten.Web/app/utils/leech.ts.
    /// </summary>
    /// <param name="stabilityDays">FsrsScheduler.GetStabilityDays: raw stability is not in days under FSRS-7.</param>
    public static bool IsLeech(int lapses, double? stabilityDays, int leechThreshold) =>
        leechThreshold > 0
        && lapses >= leechThreshold
        && (stabilityDays ?? 0) < RetentionCalculator.MatureThresholdDays;

    public static bool ShouldSuspend(LeechAction action, FsrsRating rating, bool isLeech) =>
        action == LeechAction.Suspend && rating == FsrsRating.Again && isLeech;

    public static bool IsNotifyStep(int lapses, int leechThreshold)
    {
        if (leechThreshold <= 0) return false;
        if (lapses == leechThreshold) return true;

        var halfThreshold = Math.Max(leechThreshold / 2, 1);
        return lapses > leechThreshold && (lapses - leechThreshold) % halfThreshold == 0;
    }
}
