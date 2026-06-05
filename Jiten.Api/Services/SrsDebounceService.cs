using System.Collections.Concurrent;

namespace Jiten.Api.Services;

public class SrsDebounceService : ISrsDebounceService
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(1f);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5f);

    private readonly ConcurrentDictionary<string, DebounceBucket> _buckets = new();
    private DateTime _lastCleanup = DateTime.UtcNow;
    private readonly Lock _cleanupLock = new();

    public bool TryAcquire(string operation, string userId, int wordId, byte readingIndex)
    {
        var key = $"{operation}:{userId}:{wordId}:{readingIndex}";
        var now = DateTime.UtcNow;

        CleanupIfNeeded(now);

        var bucket = _buckets.GetOrAdd(key, _ => new DebounceBucket(DateTime.MinValue));

        lock (bucket)
        {
            if (now - bucket.LastOperation < DebounceWindow)
                return false;

            bucket.LastOperation = now;
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

            var cutoff = now - DebounceWindow - TimeSpan.FromSeconds(1);
            var keysToRemove = _buckets
                .Where(kvp => { lock (kvp.Value) { return kvp.Value.LastOperation < cutoff; } })
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
                _buckets.TryRemove(key, out _);

            _lastCleanup = now;
        }
    }

    private class DebounceBucket(DateTime lastOperation)
    {
        public DateTime LastOperation = lastOperation;
    }
}
