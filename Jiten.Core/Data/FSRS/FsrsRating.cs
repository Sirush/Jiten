namespace Jiten.Core.Data.FSRS;

/// <summary>
/// Represents the user's performance rating for a card review
/// </summary>
public enum FsrsRating
{
    /// <summary>
    /// Complete failure to recall - card needs immediate review
    /// </summary>
    Again = 1,

    /// <summary>
    /// Difficult to recall but eventually remembered
    /// </summary>
    Hard = 2,

    /// <summary>
    /// Normal recall with appropriate effort
    /// </summary>
    Good = 3,

    /// <summary>
    /// Effortless recall - card was too easy
    /// </summary>
    Easy = 4
}

public static class FsrsRatingExtensions
{
    public static bool IsValid(this FsrsRating rating) => rating is >= FsrsRating.Again and <= FsrsRating.Easy;
}
