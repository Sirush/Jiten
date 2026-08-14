using Hangfire;
using Jiten.Api.Jobs;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Jiten.Api.Services;

public interface IUserActivityTracker
{
    Task BumpAsync(string userId);
}

public class UserActivityTracker(
    IConnectionMultiplexer redis,
    IDbContextFactory<UserDbContext> userContextFactory,
    IBackgroundJobClient backgroundJobs,
    ILogger<UserActivityTracker> logger) : IUserActivityTracker
{
    public const int InactiveThresholdDays = 15;
    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromMinutes(5);

    public async Task BumpAsync(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return;

        // Throttle: skip if we've already bumped this user in the last 5 minutes.
        // Prevents per-request DB writes on bursty refresh patterns.
        try
        {
            var set = await redis.GetDatabase()
                .StringSetAsync($"user:activity-bumped:{userId}", "1", ThrottleWindow, When.NotExists);
            if (!set) return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UserActivityTracker: Redis throttle check failed for {UserId}, proceeding", userId);
        }

        try
        {
            await using var ctx = await userContextFactory.CreateDbContextAsync();
            var now = DateTime.UtcNow;
            var inactiveThreshold = now.AddDays(-InactiveThresholdDays);

            var metadata = await ctx.UserMetadatas.FirstOrDefaultAsync(um => um.UserId == userId);
            var previous = metadata?.LastActivity;
            var returningUser = previous == null || previous < inactiveThreshold;

            if (metadata == null)
            {
                metadata = new UserMetadata { UserId = userId, LastActivity = now };
                ctx.UserMetadatas.Add(metadata);
            }
            else
            {
                metadata.LastActivity = now;
            }

            // ComputeUserCoverage skips clean users, so the catch-up enqueue below must mark dirty.
            if (returningUser)
            {
                metadata.CoverageDirty = true;
                metadata.CoverageDirtyAt = now;
            }

            await ctx.SaveChangesAsync();

            // Returning user: if they were inactive (or brand new in metadata terms),
            // queue a full coverage recompute so sort-by-coverage reflects decks added while away.
            if (returningUser)
            {
                backgroundJobs.Enqueue<ComputationJob>(j => j.ComputeUserCoverage(userId));
                logger.LogInformation(
                    "UserActivityTracker: user {UserId} returning after inactivity (previous={Previous}), queued coverage recompute",
                    userId, previous);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UserActivityTracker: failed to bump activity for {UserId}", userId);
        }
    }
}
