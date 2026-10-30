using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Helpers;

public static class MemoryCacheExtensions
{
    private static readonly object[] Stripes = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();

    /// <summary>Concurrent first callers share one load; a failed load is evicted so the next caller retries instead of rethrowing a cached fault.</summary>
    public static async Task<T> GetOrCreateOnceAsync<T>(this IMemoryCache cache, string key, MemoryCacheEntryOptions options,
                                                        Func<Task<T>> load)
    {
        var stripe = Stripes[(key.GetHashCode() & int.MaxValue) % Stripes.Length];
        Lazy<Task<T>> lazy;
        lock (stripe)
        {
            if (cache.TryGetValue(key, out Lazy<Task<T>>? existing) && existing != null)
                lazy = existing;
            else
                lazy = cache.Set(key, new Lazy<Task<T>>(load, LazyThreadSafetyMode.ExecutionAndPublication), options);
        }

        try
        {
            return await lazy.Value;
        }
        catch
        {
            lock (stripe)
            {
                if (cache.TryGetValue(key, out Lazy<Task<T>>? current) && ReferenceEquals(current, lazy))
                    cache.Remove(key);
            }

            throw;
        }
    }
}
