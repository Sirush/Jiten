using System.Collections.Concurrent;
using Jiten.Api.Services.SmartDeck;

namespace Jiten.Parser.Tests.Integration.Infrastructure;

public class RecordingSmartDeckDirtyService : ISmartDeckDirtyService
{
    public ConcurrentQueue<string> Marks { get; } = new();
    private readonly ConcurrentDictionary<string, bool> _enabled = new();

    public bool MarkSucceeds { get; set; } = true;
    public bool Pending { get; set; }

    public Task<bool> MarkDirty(string userId)
    {
        Marks.Enqueue(userId);
        return Task.FromResult(MarkSucceeds);
    }

    public Task<bool> IsRebuildPending(string userId) => Task.FromResult(Pending);

    public Task<long> ConsumeDirty(string userId) => Task.FromResult(0L);
    public Task ReleaseQueuedGate(string userId) => Task.CompletedTask;

    public Task SetEnabled(string userId, bool enabled)
    {
        _enabled[userId] = enabled;
        return Task.CompletedTask;
    }

    public Task<bool> IsEnabled(string userId) => Task.FromResult(_enabled.GetValueOrDefault(userId));
}
