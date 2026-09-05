namespace Jiten.Api.Services;

/// <summary>Tracks the caches a request cannot be served correctly without; /health stays 503 until all are loaded.</summary>
public sealed class StartupReadiness
{
    public const string Parser = "parser";
    public const string WordFormSiblings = "wordFormSiblings";
    public const string DerivationLinks = "derivationLinks";

    private static readonly string[] Required = [Parser, WordFormSiblings, DerivationLinks];
    private readonly HashSet<string> _ready = new();
    private readonly object _lock = new();
    private readonly TaskCompletionSource _allReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void MarkReady(string component)
    {
        lock (_lock)
        {
            _ready.Add(component);
            if (Required.All(_ready.Contains)) _allReady.TrySetResult();
        }
    }

    /// <summary>Completes once every required cache is loaded; optional warmups wait on it to stay off the critical path.</summary>
    public Task WhenReady => _allReady.Task;

    public bool IsReady
    {
        get
        {
            lock (_lock) return Required.All(_ready.Contains);
        }
    }

    public IReadOnlyList<string> Pending
    {
        get
        {
            lock (_lock) return Required.Where(r => !_ready.Contains(r)).ToList();
        }
    }
}
