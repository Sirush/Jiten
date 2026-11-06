using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

/// <summary>Builds optimiser input from a user's whole grading history, fits parameters and scores them.</summary>
public static class FsrsOptimisationService
{
    /// <param name="reviews">Sorted by card, then time, as <see cref="CardArchiveService.LoadAllReviewsAsync"/> returns them.</param>
    public static List<FsrsTrainingItem> BuildTrainingItems(List<UserReview> reviews)
    {
        var items = new List<FsrsTrainingItem>();
        var start = 0;
        while (start < reviews.Count)
        {
            var cardId = reviews[start].CardId;
            var end = start;
            while (end < reviews.Count && reviews[end].CardId == cardId)
                end++;

            var count = end - start;
            if (count >= 2)
            {
                var trainingReviews = new FsrsTrainingReview[count];
                trainingReviews[0] = new FsrsTrainingReview((int)reviews[start].Rating, 0);
                for (var i = 1; i < count; i++)
                {
                    var deltaT = Math.Max(0, (reviews[start + i].ReviewUtc - reviews[start + i - 1].ReviewUtc).TotalDays);
                    trainingReviews[i] = new FsrsTrainingReview((int)reviews[start + i].Rating, deltaT);
                }
                items.Add(new FsrsTrainingItem(trainingReviews, reviews[end - 1].ReviewUtc.Ticks));
            }

            start = end;
        }

        return items;
    }

    /// <summary>Optimises on the user's live and archived reviews; returns an error message when the history is too thin.</summary>
    public static async Task<(string? Error, FsrsOptimizationResult? Result)> OptimiseAsync(UserDbContext userContext, string userId, FsrsVersion version)
    {
        var reviews = await CardArchiveService.LoadAllReviewsAsync(userContext, userId);
        if (reviews.Count < FsrsOptimizer.MinimumReviews)
            return ($"At least {FsrsOptimizer.MinimumReviews} reviews are required to optimise parameters. You have {reviews.Count}.", null);

        var items = BuildTrainingItems(reviews);
        if (items.Count == 0)
            return ("Not enough review data to optimise.", null);

        return (null, Fit(version, items));
    }

    public static FsrsOptimizationResult Fit(FsrsVersion version, List<FsrsTrainingItem> items)
        => version == FsrsVersion.V7 ? FsrsOptimizerV7.Optimize(items) : FsrsOptimizer.Optimize(items);

    public static double EvaluateLoss(FsrsVersion version, List<FsrsTrainingItem> items, double[] parameters)
        => version == FsrsVersion.V7 ? FsrsOptimizerV7.EvaluateLoss(items, parameters) : FsrsOptimizer.EvaluateLoss(items, parameters);

    /// <param name="reviews">Sorted by card, then time.</param>
    public static FsrsHealthResponse ComputeHealth(List<UserReview> reviews)
    {
        var total = reviews.Count;
        var ratingCounts = new int[4];
        var sameDay = 0;
        long prevCardId = -1;
        var prevDay = default(DateTime);

        foreach (var review in reviews)
        {
            var idx = (int)review.Rating - 1;
            if (idx >= 0 && idx < 4)
                ratingCounts[idx]++;

            var day = review.ReviewUtc.Date;
            if (review.CardId == prevCardId && day == prevDay)
                sameDay++;
            prevCardId = review.CardId;
            prevDay = day;
        }

        var min = FsrsOptimizer.MinimumReviews;
        var hard = ratingCounts[1];
        var easy = ratingCounts[3];
        var again = ratingCounts[0];

        return new FsrsHealthResponse
        {
            TotalReviews = total,
            RatingCounts = ratingCounts,
            SameDayReviews = sameDay,
            MinimumReviewsForOptimize = min,
            MeetsMinimum = total >= min,
            NeverUsesHard = total >= min && hard == 0,
            NeverUsesEasy = total >= min && easy == 0,
            LikelyHardAsFail = IsLikelyHardAsFail(total, hard, again),
        };
    }

    /// <summary>Hard used heavily while Again is almost never pressed: Hard is standing in for a fail.</summary>
    private static bool IsLikelyHardAsFail(int total, int hard, int again)
        => total >= FsrsOptimizer.MinimumReviews && hard >= total * 0.18 && again <= total * 0.02;

    /// <summary>Lifetime review total from the daily rollup, the measure the nightly refit compares growth against.</summary>
    public static async Task<int> LifetimeReviewTotalAsync(UserDbContext userContext, string userId)
        => await userContext.UserReviewDailies.Where(d => d.UserId == userId).SumAsync(d => (int?)d.ReviewCount) ?? 0;
}
