using StackExchange.Redis;

namespace Jiten.Api.Services;

public interface IPendingCoverageQueue
{
    Task AddAsync(int deckId);
    Task AddManyAsync(IEnumerable<int> deckIds);
    Task<List<int>> DrainAsync();

    // Increments a bounded retry counter for a user whose batch coverage job was contended.
    // Returns true if the caller may re-queue the deckIds for another attempt, false if the
    // retry budget is exhausted and the caller should fall back (e.g., mark CoverageDirty).
    Task<bool> TryRecordContentionAsync(string userId, int maxAttempts);
}

public class PendingCoverageQueue(IConnectionMultiplexer redis, ILogger<PendingCoverageQueue> logger)
    : RedisPendingDeckSet(redis, logger, "coverage:pending_decks"), IPendingCoverageQueue
{
    private static readonly TimeSpan ContentionTtl = TimeSpan.FromHours(2);

    public async Task<bool> TryRecordContentionAsync(string userId, int maxAttempts)
    {
        try
        {
            var db = Redis.GetDatabase();
            var key = $"coverage:batch-contention:{userId}";
            var count = await db.StringIncrementAsync(key);
            if (count == 1)
                await db.KeyExpireAsync(key, ContentionTtl);
            return count <= maxAttempts;
        }
        catch (Exception ex)
        {
            // On Redis failure fall through to the safe path (caller should mark dirty).
            Logger.LogWarning(ex, "Failed to record batch contention for {UserId}", userId);
            return false;
        }
    }
}
