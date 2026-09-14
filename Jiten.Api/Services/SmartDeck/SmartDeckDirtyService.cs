using Hangfire;
using Jiten.Api.Jobs;
using StackExchange.Redis;

namespace Jiten.Api.Services.SmartDeck;

public interface ISmartDeckDirtyService
{
    Task<bool> MarkDirty(string userId);
    Task<bool> IsRebuildPending(string userId);
    Task<long> ConsumeDirty(string userId);
    Task ReleaseQueuedGate(string userId);

    Task SetEnabled(string userId, bool enabled);
    Task<bool> IsEnabled(string userId);
}

public class SmartDeckDirtyService(IConnectionMultiplexer redis, IBackgroundJobClient backgroundJobs, ILogger<SmartDeckDirtyService> logger)
    : ISmartDeckDirtyService
{
    public static readonly TimeSpan RebuildDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QueuedGateTtl = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DirtyTtl = TimeSpan.FromDays(2);
    private const string EnabledSetKey = "smartdeck:enabled";

    private static string DirtyKey(string userId) => $"smartdeck:dirty:{userId}";
    private static string QueuedKey(string userId) => $"smartdeck:queued:{userId}";

    public async Task<bool> MarkDirty(string userId)
    {
        try
        {
            var db = redis.GetDatabase();
            if (!await db.SetContainsAsync(EnabledSetKey, userId)) return false;

            var count = await db.StringIncrementAsync(DirtyKey(userId));
            if (count == 1) await db.KeyExpireAsync(DirtyKey(userId), DirtyTtl, ExpireWhen.Always, CommandFlags.None);

            if (await db.StringSetAsync(QueuedKey(userId), "1", QueuedGateTtl, false, When.NotExists, CommandFlags.None))
                backgroundJobs.Schedule<SmartDeckJob>(job => job.Rebuild(userId), RebuildDelay);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to mark smart deck dirty for {UserId}", userId);
            return false;
        }
    }

    public async Task<bool> IsRebuildPending(string userId)
    {
        try
        {
            var db = redis.GetDatabase();
            return await db.KeyExistsAsync(QueuedKey(userId)) || await db.KeyExistsAsync(DirtyKey(userId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read smart deck pending state for {UserId}", userId);
            return false;
        }
    }

    public async Task<long> ConsumeDirty(string userId)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetDeleteAsync(DirtyKey(userId));
            return value.HasValue && value.TryParse(out long count) ? count : 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read smart deck dirty counter for {UserId}", userId);
            return 0;
        }
    }

    public async Task ReleaseQueuedGate(string userId)
    {
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(QueuedKey(userId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to release smart deck queue gate for {UserId}", userId);
        }
    }

    public async Task SetEnabled(string userId, bool enabled)
    {
        try
        {
            var db = redis.GetDatabase();
            if (enabled) await db.SetAddAsync(EnabledSetKey, userId);
            else await db.SetRemoveAsync(EnabledSetKey, userId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update smart deck enabled set for {UserId}", userId);
        }
    }

    public async Task<bool> IsEnabled(string userId)
    {
        try
        {
            return await redis.GetDatabase().SetContainsAsync(EnabledSetKey, userId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read smart deck enabled set for {UserId}", userId);
            return false;
        }
    }
}
