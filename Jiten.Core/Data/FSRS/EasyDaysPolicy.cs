namespace Jiten.Core.Data.FSRS;

/// <summary>
/// A per-weekday load preference applied on top of load balancing ("Easy Days"). Each weekday carries a
/// weight in [0, 1] — 1.0 = normal, 0.5 = reduced, ~0 = avoid — and the scheduler steers fuzzed due dates
/// toward higher-weight days within the existing fuzz window. It is a preference, not a hard rule: if a
/// card's whole window falls on avoided days it still schedules there. Weekday is computed in the user's
/// local time via <see cref="OffsetHours"/>.
/// </summary>
public sealed class EasyDaysPolicy
{
    /// <summary>Weekday weights indexed by <see cref="DayOfWeek"/> (0 = Sunday … 6 = Saturday).</summary>
    public double[] WeekdayWeights { get; }

    /// <summary>User's UTC offset in hours, used to resolve the local weekday of a UTC due date.</summary>
    public double OffsetHours { get; }

    public EasyDaysPolicy(double[] weekdayWeights, double offsetHours)
    {
        WeekdayWeights = weekdayWeights;
        OffsetHours = offsetHours;
    }

    /// <summary>Load capacity weight for the local weekday of the given UTC due date.</summary>
    public double Weight(DateTime utcDue)
    {
        var localWeekday = utcDue.AddHours(OffsetHours).DayOfWeek;
        return WeekdayWeights[(int)localWeekday];
    }

    /// <summary>
    /// Builds a policy from raw weekday weights, or returns null when the feature is inactive (missing,
    /// wrong length, or every day at full weight). Weights are clamped to [0, 1].
    /// </summary>
    public static EasyDaysPolicy? From(double[]? weekdayWeights, double offsetHours)
    {
        if (weekdayWeights is not { Length: 7 })
            return null;

        var clamped = new double[7];
        var active = false;
        for (var i = 0; i < 7; i++)
        {
            clamped[i] = Math.Clamp(weekdayWeights[i], 0.0, 1.0);
            if (clamped[i] < 1.0)
                active = true;
        }

        return active ? new EasyDaysPolicy(clamped, offsetHours) : null;
    }
}
