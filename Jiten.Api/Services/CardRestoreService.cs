using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public record CardRestoreOutcome(
    int WordId,
    byte ReadingIndex,
    bool Restored,
    int ReviewsRestored,
    bool HistoryTruncated,
    string? Error);

/// <summary>
/// Puts archived cards and their history back into the collection
/// </summary>
public static class CardRestoreService
{
    /// <summary>
    /// Restores the archived forms the caller lists
    /// </summary>
    public static async Task<List<CardRestoreOutcome>> RestoreAsync(
        UserDbContext userCtx, JitenDbContext jitenCtx, string userId,
        IReadOnlyList<(int WordId, byte ReadingIndex)> keys,
        double[] parameters, double desiredRetention)
    {
        var outcomes = new List<CardRestoreOutcome>(keys.Count);
        if (keys.Count == 0)
            return outcomes;

        var keySet = keys.ToHashSet();
        var wordIds = keySet.Select(k => k.WordId).Distinct().ToList();

        var rows = (await userCtx.FsrsCardArchives
                                 .Where(a => a.UserId == userId && wordIds.Contains(a.WordId))
                                 .ToListAsync())
                   .Where(a => keySet.Contains((a.WordId, a.ReadingIndex)))
                   .ToDictionary(a => (a.WordId, a.ReadingIndex));

        var readingCounts = await jitenCtx.WordForms
                                          .AsNoTracking()
                                          .Where(wf => wordIds.Contains(wf.WordId))
                                          .GroupBy(wf => wf.WordId)
                                          .Select(g => new { WordId = g.Key, ReadingCount = g.Count() })
                                          .ToDictionaryAsync(w => w.WordId, w => w.ReadingCount);

        var liveCards = (await userCtx.FsrsCards
                                      .Include(c => c.ReviewLogs)
                                      .Where(c => c.UserId == userId && wordIds.Contains(c.WordId))
                                      .ToListAsync())
                        .Where(c => keySet.Contains((c.WordId, c.ReadingIndex)))
                        .ToDictionary(c => (c.WordId, c.ReadingIndex));

        var scheduler = new FsrsScheduler(desiredRetention: desiredRetention, parameters: parameters);
        var replayScheduler = new FsrsScheduler(desiredRetention: desiredRetention, parameters: parameters, enableFuzzing: false);

        foreach (var key in keys.Distinct())
        {
            if (!rows.TryGetValue(key, out var row))
            {
                outcomes.Add(new CardRestoreOutcome(key.WordId, key.ReadingIndex, false, 0, false, "Not in the archive"));
                continue;
            }

            if (!readingCounts.TryGetValue(key.WordId, out var readingCount))
            {
                outcomes.Add(new CardRestoreOutcome(key.WordId, key.ReadingIndex, false, 0, false,
                                                    "This word no longer exists in JMdict"));
                continue;
            }

            if (key.ReadingIndex >= readingCount)
            {
                outcomes.Add(new CardRestoreOutcome(key.WordId, key.ReadingIndex, false, 0, false,
                                                    "This form no longer exists in JMdict"));
                continue;
            }

            var (reviews, corrupt) = CardArchiveService.ReadReviews(row);
            if (corrupt)
            {
                outcomes.Add(new CardRestoreOutcome(key.WordId, key.ReadingIndex, false, 0, true,
                                                    "The stored history could not be read"));
                continue;
            }

            var truncated = row.HistoryTruncated;
            int restoredCount;

            if (liveCards.TryGetValue(key, out var liveCard))
                restoredCount = RestoreOntoLiveCard(userCtx, liveCard, reviews, scheduler, replayScheduler);
            else
                restoredCount = RestoreAsNewCard(userCtx, userId, row, reviews, scheduler, replayScheduler);

            userCtx.FsrsCardArchives.Remove(row);
            outcomes.Add(new CardRestoreOutcome(key.WordId, key.ReadingIndex, true, restoredCount, truncated, null));
        }

        return outcomes;
    }

    /// <summary>
    /// Puts the history of auto-restorable removals back onto cards the caller is about to insert
    /// </summary>
    public static async Task<int> AutoRestoreAsync(UserDbContext ctx, string userId, IReadOnlyList<FsrsCard> newCards,
                                                   bool restoreSchedule = true,
                                                   Func<FsrsCard, IEnumerable<DateTime>>? pendingReviewTimes = null)
    {
        if (newCards.Count == 0)
            return 0;

        var keySet = newCards.Select(c => (c.WordId, c.ReadingIndex)).ToHashSet();
        var wordIds = keySet.Select(k => k.WordId).Distinct().ToList();

        var rows = (await ctx.FsrsCardArchives
                             .Where(a => a.UserId == userId && wordIds.Contains(a.WordId)
                                         && (a.Reason == CardArchiveReason.KanaRedundancy
                                             || a.Reason == CardArchiveReason.FormPrune))
                             .ToListAsync())
                   .Where(a => keySet.Contains((a.WordId, a.ReadingIndex)))
                   .ToDictionary(a => (a.WordId, a.ReadingIndex));

        if (rows.Count == 0)
            return 0;

        var restored = 0;

        foreach (var card in newCards)
        {
            if (!rows.TryGetValue((card.WordId, card.ReadingIndex), out var row))
                continue;

            var (reviews, corrupt) = CardArchiveService.ReadReviews(row);
            if (corrupt)
                continue;

            if (restoreSchedule)
            {
                card.Stability = row.Stability;
                card.Difficulty = row.Difficulty;
                card.Lapses = row.Lapses;
            }

            card.CreatedAt = row.CardCreatedAt;

            var known = card.ReviewLogs.Select(l => CardArchiveService.TruncateToSecond(l.ReviewDateTime))
                            .Concat((pendingReviewTimes?.Invoke(card) ?? []).Select(CardArchiveService.TruncateToSecond))
                            .ToHashSet();

            foreach (var review in CardArchiveService.DistinctBySecond(reviews, known))
                card.ReviewLogs.Add(new FsrsReviewLog
                                    {
                                        Rating = review.Rating,
                                        ReviewDateTime = review.ReviewDateTime,
                                        ReviewDuration = review.ReviewDuration
                                    });

            ctx.FsrsCardArchives.Remove(row);
            restored++;
        }

        return restored;
    }

    private static int RestoreAsNewCard(UserDbContext ctx, string userId, FsrsCardArchive row, List<PackedReview> reviews,
                                        FsrsScheduler scheduler, FsrsScheduler replayScheduler)
    {
        var card = new FsrsCard(userId, row.WordId, row.ReadingIndex)
                   {
                       State = row.State,
                       Step = row.Step,
                       Stability = row.Stability,
                       Difficulty = row.Difficulty,
                       Due = row.Due,
                       LastReview = row.LastReview,
                       Lapses = row.Lapses,
                       CreatedAt = row.CardCreatedAt
                   };

        foreach (var review in CardArchiveService.DistinctBySecond(reviews))
            card.ReviewLogs.Add(new FsrsReviewLog
                                {
                                    Rating = review.Rating,
                                    ReviewDateTime = review.ReviewDateTime,
                                    ReviewDuration = review.ReviewDuration
                                });

        if (row.HistoryMerged)
            FsrsReplay.Recompute(card, card.ReviewLogs.ToList(), scheduler, replayScheduler);

        ctx.FsrsCards.Add(card);
        return card.ReviewLogs.Count;
    }

    /// <summary>
    /// Merges archived history into a card the user has since re-created
    /// </summary>
    private static int RestoreOntoLiveCard(
        UserDbContext ctx, FsrsCard card, List<PackedReview> reviews,
        FsrsScheduler scheduler, FsrsScheduler replayScheduler)
    {
        var known = card.ReviewLogs.Select(l => CardArchiveService.TruncateToSecond(l.ReviewDateTime)).ToHashSet();
        var added = new List<FsrsReviewLog>();

        foreach (var review in CardArchiveService.DistinctBySecond(reviews, known))
        {
            var log = new FsrsReviewLog
                      {
                          CardId = card.CardId,
                          Rating = review.Rating,
                          ReviewDateTime = review.ReviewDateTime,
                          ReviewDuration = review.ReviewDuration
                      };
            added.Add(log);
            card.ReviewLogs.Add(log);
        }

        if (added.Count > 0)
        {
            ctx.FsrsReviewLogs.AddRange(added);
            FsrsReplay.Recompute(card, card.ReviewLogs.ToList(), scheduler, replayScheduler);
        }

        return added.Count;
    }
}
