using System.Collections.Concurrent;
using Jiten.Api.Services;

namespace Jiten.Parser.Tests.Integration.Infrastructure;

/// <summary>
/// Test double for <see cref="IBillingAlertService"/> that records raised alerts instead of pushing them,
/// so tests can assert that a silent-failure path actually alerts.
/// </summary>
public class RecordingBillingAlertService : IBillingAlertService
{
    public record RaisedAlert(string Key, string Title, string Detail);

    private readonly ConcurrentQueue<RaisedAlert> _raised = new();

    public IReadOnlyList<RaisedAlert> Raised => _raised.ToArray();

    public void Clear() => _raised.Clear();

    public Task RaiseAsync(string key, string title, string detail, CancellationToken ct = default)
    {
        _raised.Enqueue(new RaisedAlert(key, title, detail));
        return Task.CompletedTask;
    }
}
