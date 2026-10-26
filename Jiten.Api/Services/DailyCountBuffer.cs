using System.Collections.Concurrent;

namespace Jiten.Api.Services;

public interface IActivityBuffer
{
    Task FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>Accumulates per-entity, per-UTC-day counters in memory and writes them in batches</summary>
public abstract class DailyCountBuffer<TCounts>(ILogger logger) : IActivityBuffer where TCounts : struct
{
    private readonly ConcurrentDictionary<(int EntityId, DateOnly Date), TCounts> _pending = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);

    protected DailyVisitorFilter Visitors { get; } = new();

    protected abstract TCounts Merge(TCounts current, TCounts added);

    protected abstract Task WriteAsync(List<KeyValuePair<(int EntityId, DateOnly Date), TCounts>> batch, CancellationToken cancellationToken);

    protected void Add(int entityId, TCounts counts) =>
        _pending.AddOrUpdate((entityId, DateOnly.FromDateTime(DateTime.UtcNow)), counts, (_, c) => Merge(c, counts));

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_pending.IsEmpty) return;
        await _flushLock.WaitAsync(cancellationToken);
        try
        {
            var batch = new List<KeyValuePair<(int EntityId, DateOnly Date), TCounts>>();
            foreach (var key in _pending.Keys.ToList())
            {
                if (_pending.TryRemove(key, out var counts))
                    batch.Add(new(key, counts));
            }

            if (batch.Count == 0) return;

            try
            {
                await WriteAsync(batch, cancellationToken);
            }
            catch (Exception ex)
            {
                foreach (var (key, counts) in batch)
                    _pending.AddOrUpdate(key, counts, (_, c) => Merge(c, counts));
                logger.LogWarning(ex, "{Buffer} flush failed; {Count} buckets kept for retry", GetType().Name, batch.Count);
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }
}

public class ActivityFlushService(IEnumerable<IActivityBuffer> buffers, ILogger<ActivityFlushService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var buffer in buffers)
                    await buffer.FlushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            foreach (var buffer in buffers)
                await buffer.FlushAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Final activity flush failed on shutdown");
        }
    }
}
