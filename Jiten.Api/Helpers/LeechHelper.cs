using Jiten.Core.Data.FSRS;

namespace Jiten.Api.Helpers;

public static class LeechHelper
{
    /// <summary>
    /// A card is an active leech when its lapse count has reached the user's leech threshold and it
    /// hasn't recovered since (current stability below the mature threshold). Derived on the fly, so
    /// changing the threshold retroactively updates which cards are flagged.
    /// Mirrored client-side in Jiten.Web/app/utils/leech.ts.
    /// </summary>
    public static bool IsLeech(int lapses, double? stability, int leechThreshold) =>
        leechThreshold > 0
        && lapses >= leechThreshold
        && (stability ?? 0) < RetentionCalculator.MatureThresholdDays;
}
