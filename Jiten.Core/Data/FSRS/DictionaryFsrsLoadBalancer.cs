namespace Jiten.Core.Data.FSRS;

/// <summary>
/// In-memory <see cref="IFsrsLoadBalancer"/> backed by a per-local-day histogram. Seed it from a
/// snapshot of currently-scheduled due dates; it then accumulates new placements via
/// <see cref="Register"/> so a batch of cards balances against both the existing schedule and
/// the placements made earlier in the same batch. Days are the user's local days, resolved from a UTC
/// due date with <see cref="OffsetHours"/>, the same convention as <see cref="EasyDaysPolicy"/>.
/// </summary>
public class DictionaryFsrsLoadBalancer : IFsrsLoadBalancer
{
    private readonly Dictionary<DateOnly, int> _loadByDay = new();

    /// <summary>User's UTC offset in hours; a due date counts toward the local day it falls on.</summary>
    public double OffsetHours { get; }

    public DictionaryFsrsLoadBalancer(IEnumerable<DateTime>? scheduledDueDates = null, double offsetHours = 0)
    {
        OffsetHours = offsetHours;
        if (scheduledDueDates == null) return;
        foreach (var due in scheduledDueDates)
            Register(due);
    }

    /// <summary>Seeds from pre-aggregated (local day, count) pairs, e.g. a SQL GROUP BY over shifted due dates.</summary>
    public DictionaryFsrsLoadBalancer(IEnumerable<KeyValuePair<DateOnly, int>> loadByDay, double offsetHours)
    {
        OffsetHours = offsetHours;
        foreach (var (day, count) in loadByDay)
            _loadByDay[day] = count;
    }

    public int GetLoad(DateTime dueDate)
    {
        return _loadByDay.GetValueOrDefault(LocalDay(dueDate));
    }

    public void Register(DateTime dueDate)
    {
        // Infinite intervals (mastered/suspended) never compete for a fuzz-window day; ignore them
        // so they don't distort the histogram.
        if (dueDate == DateTime.MaxValue) return;

        var day = LocalDay(dueDate);
        _loadByDay[day] = _loadByDay.GetValueOrDefault(day) + 1;
    }

    private DateOnly LocalDay(DateTime utcDue) => DateOnly.FromDateTime(utcDue.AddHours(OffsetHours));
}
