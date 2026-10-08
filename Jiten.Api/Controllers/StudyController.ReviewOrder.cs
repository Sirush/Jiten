using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Core.Data.FSRS;

namespace Jiten.Api.Controllers;

public partial class StudyController
{
    private readonly record struct ReviewSortKey(FsrsCard Card, int Tier, bool HasMemory, double Retrievability, double DaysOverdue, int Rank)
    {
        /// <summary>Cards without a memory state have no retrievability, so they count as the most forgotten, ranked by days overdue.</summary>
        public double ForgottenFirstKey => HasMemory ? Retrievability : -DaysOverdue;
    }

    /// <summary>
    /// Cards past the due cutoff come before cards served early, then learning/relearning before reviews, in every
    /// order; the user's review sort order only decides the order inside each tier.
    /// </summary>
    private async Task<List<FsrsCard>> OrderDueReviews(List<FsrsCard> cards, StudyReviewSortOrder order,
                                                       FsrsScheduler scheduler, DateTime now, DateTime dueCutoff)
    {
        if (cards.Count <= 1) return cards;

        ScopedFormFrequencies? frequencies = order is StudyReviewSortOrder.FrequencyRankAscending or StudyReviewSortOrder.FrequencyRankDescending
            ? await frequencySource.LoadFrequencies(context, cards.Select(c => c.WordId).Distinct().ToList())
            : null;

        var keyed = cards.Select(c =>
        {
            var hasMemory = c is { Stability: > 0, LastReview: not null };
            var retrievability = hasMemory ? scheduler.GetCardRetrievability(c, now) : 0;
            var tier = (c.Due <= dueCutoff ? 0 : 2) + (c.State is FsrsState.Learning or FsrsState.Relearning ? 0 : 1);

            return new ReviewSortKey(c, tier, hasMemory, retrievability, (now - c.Due).TotalDays,
                                     frequencies?.Resolve(c.WordId, c.ReadingIndex).Rank ?? 0);
        }).ToList();

        var tiered = keyed.OrderBy(k => k.Tier);
        var ordered = order switch
        {
            StudyReviewSortOrder.RetrievabilityDescending => tiered
                .ThenByDescending(k => k.HasMemory)
                .ThenByDescending(k => k.ForgottenFirstKey),
            StudyReviewSortOrder.DifficultyDescending => tiered
                .ThenBy(k => k.Card.Difficulty is null)
                .ThenByDescending(k => k.Card.Difficulty)
                .ThenBy(k => k.HasMemory)
                .ThenBy(k => k.ForgottenFirstKey),
            StudyReviewSortOrder.DifficultyAscending => tiered
                .ThenBy(k => k.Card.Difficulty is null)
                .ThenBy(k => k.Card.Difficulty)
                .ThenBy(k => k.HasMemory)
                .ThenBy(k => k.ForgottenFirstKey),
            StudyReviewSortOrder.FrequencyRankAscending => tiered
                .ThenBy(k => k.Rank <= 0)
                .ThenBy(k => k.Rank)
                .ThenBy(k => k.HasMemory)
                .ThenBy(k => k.ForgottenFirstKey),
            StudyReviewSortOrder.FrequencyRankDescending => tiered
                .ThenBy(k => k.Rank <= 0)
                .ThenByDescending(k => k.Rank)
                .ThenBy(k => k.HasMemory)
                .ThenBy(k => k.ForgottenFirstKey),
            StudyReviewSortOrder.Random => tiered.ThenBy(_ => Random.Shared.Next()),
            _ => tiered
                .ThenBy(k => k.HasMemory)
                .ThenBy(k => k.ForgottenFirstKey)
        };

        return ordered.Select(k => k.Card).ToList();
    }
}
