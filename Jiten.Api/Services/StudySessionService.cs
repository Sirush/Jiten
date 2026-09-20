using StackExchange.Redis;

namespace Jiten.Api.Services;

public class StudySessionService(IConnectionMultiplexer redis, ILogger<StudySessionService> logger) : IStudySessionService
{
    private static readonly TimeSpan SessionTtl = TimeSpan.FromHours(2);
    private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PendingClaimTtl = TimeSpan.FromSeconds(30);
    private const string PendingMarker = "\u0001pending";
    private static readonly TimeSpan CursorHintTtl = TimeSpan.FromHours(24);
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task<string> CreateSession(string userId)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        try
        {
            await _db.StringSetAsync($"srs:session:{sessionId}", userId, SessionTtl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to store study session in Redis");
        }
        return sessionId;
    }

    public async Task<bool> ValidateSession(string sessionId, string userId)
    {
        try
        {
            var stored = await _db.StringGetAsync($"srs:session:{sessionId}");
            if (stored.IsNullOrEmpty) return true;
            return stored == userId;
        }
        catch
        {
            return true;
        }
    }

    public async Task<ReviewClaim> TryClaimReview(string sessionId, string clientRequestId)
    {
        var key = $"srs:review:{sessionId}:{clientRequestId}";
        try
        {
            // The pending marker outlives any request that could still commit, but not a crashed one.
            if (await _db.StringSetAsync(key, PendingMarker, PendingClaimTtl, When.NotExists))
                return ReviewClaim.Acquired;
            var stored = await _db.StringGetAsync(key);
            if (stored.IsNullOrEmpty) return ReviewClaim.Acquired;
            return stored == PendingMarker ? ReviewClaim.InFlight : new ReviewClaim(ReviewClaimStatus.Completed, (string?)stored);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to claim review idempotency key in Redis");
            return ReviewClaim.Acquired;
        }
    }

    public async Task ReleaseReviewClaim(string sessionId, string clientRequestId)
    {
        try
        {
            await _db.KeyDeleteAsync($"srs:review:{sessionId}:{clientRequestId}");
        }
        catch {}
    }

    public async Task StoreCachedReviewResult(string sessionId, string clientRequestId, string resultJson)
    {
        try
        {
            await _db.StringSetAsync($"srs:review:{sessionId}:{clientRequestId}", resultJson, IdempotencyTtl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cache review result in Redis");
        }
    }

    public async Task RefreshSession(string sessionId)
    {
        try
        {
            await _db.KeyExpireAsync($"srs:session:{sessionId}", SessionTtl);
        }
        catch { /* best effort */ }
    }

    public async Task<long> BumpStudyOverviewVersion(string userId)
    {
        try
        {
            return await _db.StringIncrementAsync($"srs:overview-version:{userId}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to bump study overview version in Redis");
            return -1;
        }
    }

    public async Task<long> GetStudyOverviewVersion(string userId)
    {
        try
        {
            var val = await _db.StringGetAsync($"srs:overview-version:{userId}");
            return val.IsNullOrEmpty ? 0 : (long)val;
        }
        catch
        {
            return 0;
        }
    }

    public async Task StoreNewCardCursorHints(string userId, IReadOnlyDictionary<long, int> nextDeckByWordKey)
    {
        if (nextDeckByWordKey.Count == 0) return;
        try
        {
            var key = CursorHintKey(userId);
            var entries = nextDeckByWordKey.Select(kv => new HashEntry(kv.Key, kv.Value)).ToArray();
            await _db.HashSetAsync(key, entries);
            await _db.KeyExpireAsync(key, CursorHintTtl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to store new-card cursor hints in Redis");
        }
    }

    public async Task<int?> TakeNewCardCursorHint(string userId, long wordKey)
    {
        try
        {
            var key = CursorHintKey(userId);
            var value = await _db.HashGetAsync(key, wordKey);
            if (value.IsNullOrEmpty) return null;
            await _db.HashDeleteAsync(key, wordKey);
            return (int)value;
        }
        catch
        {
            return null;
        }
    }

    private static string CursorHintKey(string userId) => $"srs:new-cursor:{userId}";
}
