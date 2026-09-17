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
    public const string Queue = "review-rollup";
    private const int CardBatchSize = 2000;

    /// Namespaces this job's advisory locks so the per-user key cannot collide with another feature's.
    private const int AdvisoryLockClass = 0x52564A;

    private sealed class DayCounters
    {
        public int ReviewCount;
        public int CorrectCount;
        public int NewCardCount;
        public long TotalDurationMs;
    }

    [Queue(Queue)]
    public async Task RebuildForUser(string userId)
    {
        await using var ctx = await userContextFactory.CreateDbContextAsync();

        // The user lock is session-scoped, so the whole job has to ride one connection.
        await ctx.Database.OpenConnectionAsync();
        await LockUser(ctx, userId);
        try
        {
            // Queued duplicates coalesce here: the first job claims the flag, the rest find it clean and exit.
            if (!await ReviewRollupHelper.TryClaimDirty(ctx, userId))
                return;

            await Rebuild(ctx, userId);
        }
        finally
        {
            await UnlockUser(ctx, userId);
        }
    }

    private async Task Rebuild(UserDbContext ctx, string userId)
    {
        var settings = await FsrsSettingsHelper.LoadAsync(ctx, userId);
        var timezone = FsrsSettingsHelper.ResolveTimeZone(FsrsSettingsHelper.GetStudySettings(settings).Timezone);

        var days = new Dictionary<DateOnly, DayCounters>();

        // Both passes read under one snapshot: archiving moves a card's history out of the live logs and into
        // an archive blob, so reading them at two different instants would count that card twice or lose it.
        await using (var snapshot = await BeginSnapshotTransaction(ctx))
        {
            await AccumulateLiveLogs(ctx, userId, timezone, days);
            await AccumulateArchivedLogs(ctx, userId, timezone, days);
            await snapshot.RollbackAsync();
        }

        await using var transaction = await ctx.Database.BeginTransactionAsync();

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

        await ctx.SaveChangesAsync();
        await ReviewRollupHelper.StampRebuilt(ctx, userId);
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

        // A backfill is a forced rebuild; without the flag a user rebuilt before would be skipped as clean.
        await ctx.UserMetadatas.Where(m => userIds.Contains(m.UserId))
                 .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReviewRollupDirty, true));

        foreach (var userId in userIds)
            backgroundJobs.Enqueue<ReviewRollupJob>(job => job.RebuildForUser(userId));

        logger.LogInformation("Queued review rollup backfill for {UserCount} users", userIds.Count);
    }

    private static Task<IDbContextTransaction> BeginSnapshotTransaction(UserDbContext ctx)
        => ctx.Database.ProviderName?.Contains("Npgsql") == true
            ? ctx.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead)
            : ctx.Database.BeginTransactionAsync();

    /// <summary>
    /// Serialises rebuilds of one user so a waiting duplicate sees the claimed flag instead of repeating the
    /// scan, and two builds can never interleave their delete and insert halves.
    /// </summary>
    private static Task LockUser(UserDbContext ctx, string userId)
        => ctx.Database.ProviderName?.Contains("Npgsql") == true
            ? ctx.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock({0}, hashtext({1}))", AdvisoryLockClass, userId)
            : Task.CompletedTask;

    private static Task UnlockUser(UserDbContext ctx, string userId)
        => ctx.Database.ProviderName?.Contains("Npgsql") == true
            ? ctx.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock({0}, hashtext({1}))", AdvisoryLockClass, userId)
            : Task.CompletedTask;

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
