using System.Threading.RateLimiting;

namespace Jiten.Api.Helpers;

/// <summary>Admits a request only when every inner limiter does; leases acquired before a refusal are released.</summary>
public sealed class ChainedRateLimiter(params RateLimiter[] limiters) : RateLimiter
{
    public override TimeSpan? IdleDuration => limiters.Min(l => l.IdleDuration);

    public override RateLimiterStatistics? GetStatistics() => limiters[0].GetStatistics();

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        var acquired = new List<RateLimitLease>(limiters.Length);
        foreach (var limiter in limiters)
        {
            var lease = limiter.AttemptAcquire(permitCount);
            if (!lease.IsAcquired)
            {
                foreach (var held in acquired) held.Dispose();
                return lease;
            }

            acquired.Add(lease);
        }

        return new CombinedLease(acquired);
    }

    // Inner limiters are used with QueueLimit = 0, so acquisition can never wait.
    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
        => new(AttemptAcquireCore(permitCount));

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        foreach (var limiter in limiters)
            limiter.Dispose();
    }

    private sealed class CombinedLease(List<RateLimitLease> leases) : RateLimitLease
    {
        public override bool IsAcquired => true;

        public override IEnumerable<string> MetadataNames => leases.SelectMany(l => l.MetadataNames).Distinct();

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            foreach (var lease in leases)
            {
                if (lease.TryGetMetadata(metadataName, out metadata))
                    return true;
            }

            metadata = null;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing) return;
            foreach (var lease in leases)
                lease.Dispose();
        }
    }
}
