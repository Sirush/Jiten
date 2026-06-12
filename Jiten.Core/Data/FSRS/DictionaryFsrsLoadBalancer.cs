namespace Jiten.Core.Data.FSRS;

/// <summary>
/// In-memory <see cref="IFsrsLoadBalancer"/> backed by a per-UTC-day histogram. Seed it from a
/// snapshot of currently-scheduled due dates; it then accumulates new placements via
/// <see cref="Register"/> so a batch of cards balances against both the existing schedule and
/// the placements made earlier in the same batch.
/// </summary>
public class DictionaryFsrsLoadBalancer : IFsrsLoadBalancer
{
    private readonly Dictionary<DateOnly, int> _loadByDay = new();

    public DictionaryFsrsLoadBalancer(IEnumerable<DateTime>? scheduledDueDates = null)
    {
        if (scheduledDueDates == null) return;
        foreach (var due in scheduledDueDates)
            Register(due);
    }

    /// <summary>Seeds from pre-aggregated (day, count) pairs, e.g. a SQL GROUP BY over due dates.</summary>
    public DictionaryFsrsLoadBalancer(IEnumerable<KeyValuePair<DateOnly, int>> loadByDay)
    {
        foreach (var (day, count) in loadByDay)
            _loadByDay[day] = count;
    }

    public int GetLoad(DateTime dueDate)
    {
        return _loadByDay.GetValueOrDefault(DateOnly.FromDateTime(dueDate));
    }

    public void Register(DateTime dueDate)
    {
        // Infinite intervals (mastered/suspended) never compete for a fuzz-window day; ignore them
        // so they don't distort the histogram.
        if (dueDate == DateTime.MaxValue) return;

        var day = DateOnly.FromDateTime(dueDate);
        _loadByDay[day] = _loadByDay.GetValueOrDefault(day) + 1;
    }
}
