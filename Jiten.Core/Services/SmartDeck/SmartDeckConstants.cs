using Jiten.Core.Data;

namespace Jiten.Core.Services.SmartDeck;

public static class SmartDeckConstants
{
    public const int BoostedTitles = 5;
    public const int MaxTitles = 100;
    public const int MaxWords = 100_000;
    public const int MaxPins = 3;

    public const double WindowWeight = 3.0;
    public const double FutureUnitWeight = 1.0;
    public const double PassedUnitWeight = 0.5;
    public const double WholeTitleWeight = 1.0;
    public const double PlanningWeight = 0.3;
    public const double PinnedWeight = 1.0;
    public const double MinRecencyWeight = 0.1;

    public const int DefaultLookaheadUnits = 1;
    public const int DefaultTargetPercentage = 95;
    public const int MinTargetPercentage = 50;
    public const int MaxTargetPercentage = 100;
    public const int MaxOngoingUnits = 3;
    public const int DefaultRecencyHalfLifeDays = 14;
    public const int NeverFadesHalfLife = 0;

    public const double OccurrencesScale = 100;

    public static bool IsSequentialByDefault(MediaType mediaType)
        => mediaType is not (MediaType.VisualNovel or MediaType.VideoGame or MediaType.YouTube);

    public static double RecencyWeight(double ageDays, int halfLifeDays)
    {
        if (ageDays <= 0 || halfLifeDays == NeverFadesHalfLife) return 1.0;
        return Math.Max(MinRecencyWeight, Math.Pow(0.5, ageDays / halfLifeDays));
    }

    public static double TitleWeight(bool pinned, bool planning, double ageDays, int halfLifeDays)
    {
        if (pinned) return PinnedWeight;
        return planning ? PlanningWeight : RecencyWeight(ageDays, halfLifeDays);
    }
}
