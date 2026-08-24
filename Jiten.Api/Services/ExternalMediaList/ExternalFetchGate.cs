namespace Jiten.Api.Services.ExternalMediaList;

/// <summary>
/// Serialises outbound list fetches per provider and spaces consecutive ones, so concurrent users queue
/// instead of pushing a provider over its rate limit. Singleton: the spacing state is process-wide.
/// </summary>
public class ExternalFetchGate
{
    private readonly IReadOnlyDictionary<ExternalListProvider, TimeSpan> _spacing;
    private readonly Dictionary<ExternalListProvider, SemaphoreSlim> _slots;
    private readonly Dictionary<ExternalListProvider, DateTimeOffset> _nextAllowed = new();
    private readonly TimeSpan _maxWait;

    public ExternalFetchGate() : this(TimeSpan.FromMilliseconds(700), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(30))
    {
    }

    public ExternalFetchGate(TimeSpan anilistSpacing, TimeSpan vndbSpacing, TimeSpan maxWait)
    {
        _spacing = new Dictionary<ExternalListProvider, TimeSpan>
                   {
                       [ExternalListProvider.Anilist] = anilistSpacing,
                       [ExternalListProvider.Vndb] = vndbSpacing,
                   };

        _slots = _spacing.Keys.ToDictionary(p => p, _ => new SemaphoreSlim(1, 1));
        _maxWait = maxWait;
    }

    /// <summary>Returns false when the provider stayed busy for the whole budget; the caller must not call out in that case.</summary>
    public async Task<bool> EnterAsync(ExternalListProvider provider, CancellationToken ct = default)
    {
        if (!_slots.TryGetValue(provider, out var slot))
            return true;

        var deadline = DateTimeOffset.UtcNow + _maxWait;

        if (!await slot.WaitAsync(_maxWait, ct))
            return false;

        TimeSpan spacingWait;
        lock (_nextAllowed)
        {
            var next = _nextAllowed.GetValueOrDefault(provider);
            spacingWait = next - DateTimeOffset.UtcNow;
        }

        if (spacingWait <= TimeSpan.Zero)
            return true;

        if (DateTimeOffset.UtcNow + spacingWait > deadline)
        {
            slot.Release();
            return false;
        }

        try
        {
            await Task.Delay(spacingWait, ct);
        }
        catch
        {
            slot.Release();
            throw;
        }

        return true;
    }

    public void Exit(ExternalListProvider provider)
    {
        if (!_slots.TryGetValue(provider, out var slot))
            return;

        lock (_nextAllowed)
        {
            _nextAllowed[provider] = DateTimeOffset.UtcNow + _spacing[provider];
        }

        slot.Release();
    }
}
