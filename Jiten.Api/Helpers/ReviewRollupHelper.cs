using Hangfire;
using Jiten.Api.Jobs;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Helpers;

/// <summary>
/// Maintains <see cref="UserReviewDaily"/>, the derived per-day activity counters behind the heatmap,
/// streaks and activity totals.
/// </summary>
public static class ReviewRollupHelper
{
    /// <summary>
    /// The day a review belongs to, resolved at the offset in force at that instant rather than the one in
    /// force now. Writes and rebuilds must agree across a DST boundary, and a rebuild must land on the same
    /// days whatever time of year it runs.
    /// </summary>
    public static DateOnly LocalDateOf(DateTime reviewUtc, TimeZoneInfo? timezone)
        => timezone == null
            ? DateOnly.FromDateTime(reviewUtc)
            : DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(reviewUtc, DateTimeKind.Utc), timezone));

    /// <summary>
    /// Adds a signed delta to one day's counters, creating the row if needed. Negative deltas floor at zero,
    /// so a decrement can never leave a day reporting a negative count.
    /// </summary>
    public static async Task ApplyDeltaAsync(
        UserDbContext ctx, string userId, DateOnly localDate,
        int reviewDelta, int correctDelta, int newCardDelta, long durationDeltaMs)
    {
        if (reviewDelta == 0 && correctDelta == 0 && newCardDelta == 0 && durationDeltaMs == 0)
            return;

        if (ctx.Database.ProviderName?.Contains("Npgsql") == true)
        {
            await ctx.Database.ExecuteSqlRawAsync("""
                INSERT INTO "user"."UserReviewDailies"
                    ("UserId","LocalDate","ReviewCount","CorrectCount","NewCardCount","TotalDurationMs")
                VALUES ({0}::uuid, {1}, GREATEST(0, {2}), GREATEST(0, {3}), GREATEST(0, {4}), GREATEST(0, {5}))
                ON CONFLICT ("UserId","LocalDate") DO UPDATE SET
                    "ReviewCount"     = GREATEST(0, "UserReviewDailies"."ReviewCount"     + {2}),
                    "CorrectCount"    = GREATEST(0, "UserReviewDailies"."CorrectCount"    + {3}),
                    "NewCardCount"    = GREATEST(0, "UserReviewDailies"."NewCardCount"    + {4}),
                    "TotalDurationMs" = GREATEST(0, "UserReviewDailies"."TotalDurationMs" + {5})
                """,
                Guid.Parse(userId), localDate, reviewDelta, correctDelta, newCardDelta, durationDeltaMs);
            return;
        }

        var row = await ctx.UserReviewDailies.FirstOrDefaultAsync(d => d.UserId == userId && d.LocalDate == localDate);
        if (row == null)
        {
            row = new UserReviewDaily { UserId = userId, LocalDate = localDate };
            ctx.UserReviewDailies.Add(row);
        }

        row.ReviewCount = Math.Max(0, row.ReviewCount + reviewDelta);
        row.CorrectCount = Math.Max(0, row.CorrectCount + correctDelta);
        row.NewCardCount = Math.Max(0, row.NewCardCount + newCardDelta);
        row.TotalDurationMs = Math.Max(0, row.TotalDurationMs + durationDeltaMs);
        await ctx.SaveChangesAsync();
    }

    public static async Task MarkDirty(UserDbContext ctx, string userId)
    {
        if (ctx.Database.ProviderName?.Contains("Npgsql") == true)
        {
            var userGuid = Guid.Parse(userId);
            var rows = await ctx.Database.ExecuteSqlRawAsync("""
                UPDATE "user"."UserMetadatas" SET "ReviewRollupDirty" = TRUE WHERE "UserId" = {0}::uuid
                """, userGuid);

            if (rows == 0)
            {
                await ctx.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "user"."UserMetadatas" ("UserId", "CoverageDirty", "ReviewRollupDirty")
                    VALUES ({0}::uuid, FALSE, TRUE)
                    ON CONFLICT DO NOTHING
                    """, userGuid);
            }

            return;
        }

        var metadata = await ctx.UserMetadatas.FirstOrDefaultAsync(m => m.UserId == userId);
        if (metadata != null)
            metadata.ReviewRollupDirty = true;
        else
            ctx.UserMetadatas.Add(new UserMetadata { UserId = userId, ReviewRollupDirty = true });

        await ctx.SaveChangesAsync();
    }

    public static async Task MarkDirtyAndQueue(UserDbContext ctx, IBackgroundJobClient backgroundJobs, string userId)
    {
        await MarkDirty(ctx, userId);
        backgroundJobs.Enqueue<ReviewRollupJob>(job => job.RebuildForUser(userId));
    }

    public static async Task MarkRebuilt(UserDbContext ctx, string userId)
    {
        var metadata = await ctx.UserMetadatas.FirstOrDefaultAsync(m => m.UserId == userId);
        if (metadata == null)
        {
            metadata = new UserMetadata { UserId = userId };
            ctx.UserMetadatas.Add(metadata);
        }

        metadata.ReviewRollupDirty = false;
        metadata.ReviewRollupRebuiltAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Every day the user has studied, or null when no rebuild has run for them yet. Callers fall back to the
    /// log-join query on null, so stats stay correct for users the backfill has not reached.
    /// </summary>
    public static async Task<List<UserReviewDaily>?> TryLoadAsync(UserDbContext ctx, string userId)
    {
        var metadata = await ctx.UserMetadatas
                                .AsNoTracking()
                                .FirstOrDefaultAsync(m => m.UserId == userId);

        if (metadata is not { ReviewRollupRebuiltAt: not null, ReviewRollupDirty: false })
            return null;

        return await ctx.UserReviewDailies
                        .AsNoTracking()
                        .Where(d => d.UserId == userId && d.ReviewCount > 0)
                        .OrderBy(d => d.LocalDate)
                        .ToListAsync();
    }
}
