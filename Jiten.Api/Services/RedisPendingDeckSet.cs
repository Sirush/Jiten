using StackExchange.Redis;

namespace Jiten.Api.Services;

/// <summary>
/// A Redis-set-backed queue of deck ids awaiting a periodic batch sweep. Adds are idempotent (a SET
/// dedups) and <see cref="DrainAsync"/> atomically reads-and-clears, so decks enqueued mid-drain survive
/// for the next sweep (MULTI/EXEC executes server-side without interleaving). Redis failures are
/// non-fatal: they are logged and the queue degrades to a no-op / empty drain.
/// </summary>
public abstract class RedisPendingDeckSet(IConnectionMultiplexer redis, ILogger logger, string key)
{
    protected IConnectionMultiplexer Redis => redis;
    protected ILogger Logger => logger;

    public async Task AddAsync(int deckId)
    {
        try
        {
            await redis.GetDatabase().SetAddAsync(key, deckId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add deck {DeckId} to {Key}", deckId, key);
        }
    }

    public async Task AddManyAsync(IEnumerable<int> deckIds)
    {
        var values = deckIds.Select(id => (RedisValue)id).ToArray();
        if (values.Length == 0) return;
        try
        {
            await redis.GetDatabase().SetAddAsync(key, values);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add {Count} decks to {Key}", values.Length, key);
        }
    }

    public async Task<List<int>> DrainAsync()
    {
        try
        {
            var db = redis.GetDatabase();
            var tx = db.CreateTransaction();
            var membersTask = tx.SetMembersAsync(key);
            _ = tx.KeyDeleteAsync(key);
            var ok = await tx.ExecuteAsync();
            if (!ok) return [];

            var members = await membersTask;
            var result = new List<int>(members.Length);
            foreach (var m in members)
                if (int.TryParse(m, out var id))
                    result.Add(id);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to drain {Key}", key);
            return [];
        }
    }
}
