using Jiten.Core.Data;

namespace Jiten.Core.Difficulty;

/// <summary>Shift applied to the raw model difficulty from kanji variety relative to length; constants are fitted on user votes and ratings.</summary>
public static class AlgorithmAdjustmentCalculator
{
    private const double ModelWeight = 0.7068;
    private const double LogUniqueKanjiWeight = 0.8622;
    private const double LogCharacterWeight = -0.1020;

    private const int MinUniqueKanji = 10;
    private const int MinCharacters = 100;

    // The ratings offset fades out between these scores; hard decks have too few ratings to justify lowering them.
    private const double OffsetFullBelow = 2.5;
    private const double OffsetZeroAbove = 3.5;

    public const decimal MinDifficulty = 0m;
    public const decimal MaxDifficulty = 5m;

    private static (double Intercept, double RatingsOffset)? GroupConstants(MediaTypeGroup group) => group switch
    {
        MediaTypeGroup.Prose => (-4.3527, 0.4393),
        MediaTypeGroup.AudioVisual => (-3.8989, 0.3199),
        MediaTypeGroup.VisualText => (-4.2307, 0),
        MediaTypeGroup.NonFiction => (-4.1505, 0),
        _ => null
    };

    /// <summary>Counts are those of the whole work; child decks take their root deck's adjustment, not their own.</summary>
    public static decimal Compute(MediaType mediaType, decimal modelDifficulty, int uniqueKanjiCount, int characterCount)
    {
        var constants = GroupConstants(MediaTypeGroups.GetGroup(mediaType));
        if (constants == null || characterCount <= 0)
            return 0m;

        var (intercept, ratingsOffset) = constants.Value;
        var model = (double)modelDifficulty;

        var score = ModelWeight * model
                    + LogUniqueKanjiWeight * Math.Log(Math.Max(uniqueKanjiCount, MinUniqueKanji))
                    + LogCharacterWeight * Math.Log(Math.Max(characterCount, MinCharacters))
                    + intercept;

        var offsetShare = Math.Clamp((OffsetZeroAbove - score) / (OffsetZeroAbove - OffsetFullBelow), 0, 1);
        score -= ratingsOffset * offsetShare;

        var corrected = Math.Clamp(score, (double)MinDifficulty, (double)MaxDifficulty);
        return Math.Round((decimal)(corrected - model), 2);
    }

    /// <summary>Raw model value moved by the adjustment, kept on the 0-5 scale.</summary>
    public static decimal Apply(decimal modelValue, decimal adjustment) =>
        Math.Clamp(modelValue + adjustment, MinDifficulty, MaxDifficulty);
}
