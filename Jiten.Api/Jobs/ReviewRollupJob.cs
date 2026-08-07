using System.Data;
using Hangfire;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Jiten.Api.Jobs;

/// <summary>
/// Rebuilds <see cref="UserReviewDaily"/> from live review logs plus archived history blobs
/// </summary>
public class ReviewRollupJob(
    IDbContextFactory<UserDbContext> userContextFactory,
    IBackgroundJobClient backgroundJobs,
    ILogger<ReviewRollupJob> logger)
{
    private const int CardBatchSize = 2000;

    private sealed class DayCounters
    {
        public int ReviewCount;
        public int CorrectCount;
        public int NewCardCount;
        public long TotalDurationMs;
    }

    [Queue("default")]
    public async Task RebuildForUser(string userId)
    {
        await using var ctx = await userContextFactory.CreateDbContextAsync();

        var settings = await FsrsSettingsHelper.LoadAsync(ctx, userId);
        var timezone = FsrsSettingsHelper.ResolveTimeZone(FsrsSettingsHelper.GetStudySettings(settings).Timezone);

        var days = new Dictionary<DateOnly, DayCounters>();

        await using var transaction = await BeginRebuildTransaction(ctx);

        await AccumulateLiveLogs(ctx, userId, timezone, days);
        await AccumulateArchivedLogs(ctx, userId, timezone, days);

        await ctx.UserReviewDailies.Where(d => d.UserId == userId).ExecuteDeleteAsync();

        if (days.Count > 0)
        {
            ctx.UserReviewDailies.AddRange(days.Select(kv => new UserReviewDaily
                                                             {
                                                                 UserId = userId,
                                                                 LocalDate = kv.Key,
                                                                 ReviewCount = kv.Value.ReviewCount,
                                                                 CorrectCount = kv.Value.CorrectCount,
                                                                 NewCardCount = kv.Value.NewCardCount,
                                                                 TotalDurationMs = kv.Value.TotalDurationMs
                                                             }));
        }

        await ReviewRollupHelper.MarkRebuilt(ctx, userId);

        await ctx.SaveChangesAsync();
        await transaction.CommitAsync();

        logger.LogInformation("Rebuilt review rollup for user {UserId}: {DayCount} days", userId, days.Count);
    }

    /// <summary>
    /// Catches users flagged dirty by a path that had no job client to hand, and retries any rebuild that was
    /// enqueued and lost.
    /// </summary>
    [Queue("default")]
    public async Task RebuildDirty()
    {
        await using var ctx = await userContextFactory.CreateDbContextAsync();

        var userIds = await ctx.UserMetadatas.AsNoTracking()
                               .Where(m => m.ReviewRollupDirty)
                               .Select(m => m.UserId)
                               .Take(500)
                               .ToListAsync();

        foreach (var userId in userIds)
            backgroundJobs.Enqueue<ReviewRollupJob>(job => job.RebuildForUser(userId));

        if (userIds.Count > 0)
            logger.LogInformation("Queued review rollup rebuild for {UserCount} dirty users", userIds.Count);
    }

    /// <summary>Enqueues a rebuild for every user who has any review history, live or archived.</summary>
    [Queue("default")]
    public async Task BackfillAll()
    {
        await using var ctx = await userContextFactory.CreateDbContextAsync();

        var withLiveLogs = await ctx.FsrsCards.AsNoTracking()
                                    .Select(c => c.UserId)
                                    .Distinct()
                                    .ToListAsync();
        var withArchives = await ctx.FsrsCardArchives.AsNoTracking()
                                    .Where(a => a.ReviewCount > 0)
                                    .Select(a => a.UserId)
                                    .Distinct()
                                    .ToListAsync();

        var userIds = withLiveLogs.Concat(withArchives).Distinct().ToList();
        foreach (var userId in userIds)
            backgroundJobs.Enqueue<ReviewRollupJob>(job => job.RebuildForUser(userId));

        logger.LogInformation("Queued review rollup backfill for {UserCount} users", userIds.Count);
    }

    private static Task<IDbContextTransaction> BeginRebuildTransaction(UserDbContext ctx)
        => ctx.Database.ProviderName?.Contains("Npgsql") == true
            ? ctx.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead)
            : ctx.Database.BeginTransactionAsync();

    private static async Task AccumulateLiveLogs(
        UserDbContext ctx, string userId, TimeZoneInfo? timezone, Dictionary<DateOnly, DayCounters> days)
    {
        var lastCardId = 0L;

        while (true)
        {
            var cardIds = await ctx.FsrsCards.AsNoTracking()
                                   .Where(c => c.UserId == userId && c.CardId > lastCardId)
                                   .OrderBy(c => c.CardId)
                                   .Select(c => c.CardId)
                                   .Take(CardBatchSize)
                                   .ToListAsync();

            if (cardIds.Count == 0)
                break;

            var logs = await ctx.FsrsReviewLogs.AsNoTracking()
                                .Where(l => cardIds.Contains(l.CardId))
                                .Select(l => new { l.CardId, l.ReviewDateTime, l.Rating, l.ReviewDuration })
                                .ToListAsync();

            foreach (var byCard in logs.GroupBy(l => l.CardId))
            {
                var first = true;
                foreach (var log in byCard.OrderBy(l => l.ReviewDateTime))
                {
                    Add(days, ReviewRollupHelper.LocalDateOf(log.ReviewDateTime, timezone),
                        log.Rating != FsrsRating.Again, first, log.ReviewDuration ?? 0);
                    first = false;
                }
            }

            lastCardId = cardIds[^1];
        }
    }

    private static async Task AccumulateArchivedLogs(
        UserDbContext ctx, string userId, TimeZoneInfo? timezone, Dictionary<DateOnly, DayCounters> days)
    {
        var lastArchiveId = 0L;

        while (true)
        {
            var rows = await ctx.FsrsCardArchives.AsNoTracking()
                                .Where(a => a.UserId == userId && a.ArchiveId > lastArchiveId && a.ReviewCount > 0)
                                .OrderBy(a => a.ArchiveId)
                                .Take(CardBatchSize)
                                .ToListAsync();

            if (rows.Count == 0)
                break;

            foreach (var row in rows)
            {
                var (reviews, corrupt) = Services.CardArchiveService.ReadReviews(row);
                if (corrupt)
                    continue;

                var first = true;
                foreach (var review in reviews)
                {
                    Add(days, ReviewRollupHelper.LocalDateOf(review.ReviewDateTime, timezone),
                        review.Rating != FsrsRating.Again, first, review.ReviewDuration ?? 0);
                    first = false;
                }
            }

            lastArchiveId = rows[^1].ArchiveId;
        }
    }

    private static void Add(Dictionary<DateOnly, DayCounters> days, DateOnly date, bool correct, bool isFirst, int durationMs)
    {
        if (!days.TryGetValue(date, out var counters))
            days[date] = counters = new DayCounters();

        counters.ReviewCount++;
        if (correct) counters.CorrectCount++;
        if (isFirst) counters.NewCardCount++;
        counters.TotalDurationMs += durationMs;
    }
}
