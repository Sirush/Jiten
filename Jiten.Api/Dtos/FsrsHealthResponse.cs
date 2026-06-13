namespace Jiten.Api.Dtos;

/// <summary>
/// A pre-optimize sanity report on the user's review history. Surfaces grade distribution, same-day
/// cramming, and grade-usage patterns that corrupt FSRS parameter training, so the optimizer can be
/// run informed rather than blind.
/// </summary>
public class FsrsHealthResponse
{
    public int TotalReviews { get; set; }

    /// <summary>Counts per FSRS rating: index 0 = Again, 1 = Hard, 2 = Good, 3 = Easy.</summary>
    public int[] RatingCounts { get; set; } = new int[4];

    /// <summary>
    /// Reviews that repeated a card already reviewed earlier the same calendar day. A high share
    /// usually means cramming or relearning-heavy use, which the optimizer treats differently.
    /// </summary>
    public int SameDayReviews { get; set; }

    public int MinimumReviewsForOptimize { get; set; }

    /// <summary>True once the total review count clears the optimizer minimum.</summary>
    public bool MeetsMinimum { get; set; }

    /// <summary>User has enough history but has never pressed Hard — a sign of unused grade resolution.</summary>
    public bool NeverUsesHard { get; set; }

    /// <summary>User has enough history but has never pressed Easy.</summary>
    public bool NeverUsesEasy { get; set; }

    /// <summary>
    /// Heuristic: the user presses Hard often yet almost never Again, the classic "Hard means I failed"
    /// pattern that biases parameter training. When set, the Hard→Again remap repair is offered.
    /// </summary>
    public bool LikelyHardAsFail { get; set; }
}
