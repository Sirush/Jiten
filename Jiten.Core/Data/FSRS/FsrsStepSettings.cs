namespace Jiten.Core.Data.FSRS;

/// <summary>Learning and relearning step lists in minutes; empty means FSRS schedules the first interval itself.</summary>
public static class FsrsStepSettings
{
    public static readonly int[] DefaultLearningMinutes = [10];
    public static readonly int[] DefaultRelearningMinutes = [10];

    public const int MaxSteps = 4;
    public const int MinStepMinutes = 1;
    public const int MaxStepMinutes = 12 * 60;

    public static TimeSpan[] DefaultLearningSteps => ToTimeSpans(DefaultLearningMinutes);
    public static TimeSpan[] DefaultRelearningSteps => ToTimeSpans(DefaultRelearningMinutes);

    public static TimeSpan[] ToTimeSpans(IEnumerable<int> minutes)
        => minutes.Select(m => TimeSpan.FromMinutes(m)).ToArray();

    /// <summary>Returns a user-facing reason the list is unusable, or null when it is valid.</summary>
    public static string? Validate(int[] minutes, string label)
    {
        if (minutes.Length > MaxSteps)
            return $"{label}: at most {MaxSteps} steps.";
        for (var i = 0; i < minutes.Length; i++)
        {
            if (minutes[i] < MinStepMinutes)
                return $"{label}: each step must be at least 1 minute.";
            if (minutes[i] > MaxStepMinutes)
                return $"{label}: each step must be under 12 hours.";
            if (i > 0 && minutes[i] <= minutes[i - 1])
                return $"{label}: steps must increase from one to the next.";
        }
        return null;
    }
}
