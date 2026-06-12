namespace Jiten.Core.Data.FSRS;

/// <summary>
/// Supplies the number of reviews already scheduled per day so the scheduler can place a fuzzed
/// due date on the least-loaded day within the fuzz window instead of picking a uniformly random day.
/// It only ever chooses inside the existing fuzz range, so it introduces no scheduling deviation
/// beyond what random fuzz already allows; it just flattens day-to-day review load.
/// </summary>
public interface IFsrsLoadBalancer
{
    /// <summary>
    /// Number of reviews currently scheduled for the calendar day of the given (UTC) due date.
    /// </summary>
    int GetLoad(DateTime dueDate);

    /// <summary>
    /// Records that a card has been scheduled for the calendar day of the given (UTC) due date,
    /// so subsequent placements in the same batch balance against it.
    /// </summary>
    void Register(DateTime dueDate);
}
