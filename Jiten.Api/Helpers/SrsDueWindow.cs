using Jiten.Api.Dtos;
using Jiten.Core.Data.FSRS;

namespace Jiten.Api.Helpers;

/// <summary>Review cards count as due up to <see cref="Review"/>; learning-state cards up to <see cref="Learning"/>.</summary>
public readonly record struct SrsDueWindow(DateTime Review, DateTime Learning)
{
    public bool IsDue(FsrsState state, DateTime due)
        => due <= (state is FsrsState.Learning or FsrsState.Relearning ? Learning : Review);

    public static SrsDueWindow At(DateTime utcNow, StudySettingsDto settings)
        => new(ReviewCutoff(utcNow, settings), LearningCutoff(utcNow, settings));

    public static DateTime ReviewCutoff(DateTime utcNow, StudySettingsDto settings)
        => settings.DayBoundaryScheduling ? FsrsSettingsHelper.LocalDayStartUtc(utcNow, settings.Timezone, 1) : utcNow;

    /// <summary>Learning and relearning cards are due from this point, so a step can end inside the current session.</summary>
    public static DateTime LearningCutoff(DateTime utcNow, StudySettingsDto settings)
    {
        var dueCutoff = ReviewCutoff(utcNow, settings);
        var learnAhead = utcNow.AddMinutes(settings.LearnAheadMinutes);

        var nextLocalMidnight = FsrsSettingsHelper.LocalDayStartUtc(utcNow, settings.Timezone, 1);
        if (learnAhead > nextLocalMidnight) learnAhead = nextLocalMidnight;
        return learnAhead > dueCutoff ? learnAhead : dueCutoff;
    }
}
