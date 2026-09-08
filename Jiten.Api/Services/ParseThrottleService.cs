using System.Collections.Concurrent;

namespace Jiten.Api.Services;

public class ParseThrottleService(TimeProvider? timeProvider = null) : IParseThrottleService
{
    public const int BudgetPerWindow = 200_000;
    public const int MinimumCharge = 2000;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, UserBucket> _buckets = new();
    private DateTime _lastCleanup = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
    private readonly Lock _cleanupLock = new();

    public bool TryConsume(string userId, int characterCount, out TimeSpan retryAfter)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        CleanupIfNeeded(now);

        var charge = Math.Max(characterCount, MinimumCharge);
        var bucket = _buckets.GetOrAdd(userId, _ => new UserBucket(BudgetPerWindow, now));

        lock (bucket)
        {
            if (now - bucket.WindowStart >= Window)
            {
                bucket.Remaining = BudgetPerWindow;
                bucket.WindowStart = now;
            }

            if (bucket.Remaining < charge)
            {
                retryAfter = bucket.WindowStart + Window - now;
                return false;
            }

            bucket.Remaining -= charge;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private void CleanupIfNeeded(DateTime now)
    {
        if (now - _lastCleanup < CleanupInterval)
            return;

        lock (_cleanupLock)
        {
            if (now - _lastCleanup < CleanupInterval)
                return;

            var cutoff = now - Window - TimeSpan.FromMinutes(1);
            var keysToRemove = _buckets
                .Where(kvp => { lock (kvp.Value) { return kvp.Value.WindowStart < cutoff; } })
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
                _buckets.TryRemove(key, out _);

            _lastCleanup = now;
        }
    }

    private class UserBucket(int remaining, DateTime windowStart)
    {
        public int Remaining = remaining;
        public DateTime WindowStart = windowStart;
    }
}
