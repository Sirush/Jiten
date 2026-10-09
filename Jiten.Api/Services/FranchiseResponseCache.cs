using Jiten.Api.Dtos;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace Jiten.Api.Services;

/// <summary>Anonymous franchise responses; every franchise sync drops them all.</summary>
public sealed class FranchiseResponseCache(IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private CancellationTokenSource _generation = new();

    public async Task<FranchiseDto?> GetOrBuildAsync(int franchiseId, Func<Task<FranchiseDto?>> build)
    {
        var key = $"franchise:{franchiseId}";
        if (cache.TryGetValue(key, out FranchiseDto? cached))
            return cached;

        var generation = Volatile.Read(ref _generation).Token;
        var dto = await build();
        if (dto != null)
            cache.Set(key, dto, new MemoryCacheEntryOptions()
                                .SetAbsoluteExpiration(Lifetime)
                                .AddExpirationToken(new CancellationChangeToken(generation)));
        return dto;
    }

    public void Invalidate() => Interlocked.Exchange(ref _generation, new CancellationTokenSource()).Cancel();
}
