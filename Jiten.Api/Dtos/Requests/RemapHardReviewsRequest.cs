namespace Jiten.Api.Dtos.Requests;

/// <summary>
/// Repair for the "Hard means I failed" pattern: rewrites historical Hard ratings to Again, optionally
/// within a date range, then reschedules. Used by the FSRS health check when <c>LikelyHardAsFail</c> is set.
/// </summary>
public class RemapHardReviewsRequest
{
    /// <summary>Inclusive lower bound (UTC). Null means from the beginning of history.</summary>
    public DateTime? From { get; set; }

    /// <summary>Inclusive upper bound (UTC). Null means up to now.</summary>
    public DateTime? To { get; set; }

    /// <summary>Recompute all card schedules after the remap so the new ratings take effect.</summary>
    public bool Reschedule { get; set; } = true;
}
