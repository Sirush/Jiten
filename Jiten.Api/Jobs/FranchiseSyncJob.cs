using Hangfire;
using Jiten.Api.Services;

namespace Jiten.Api.Jobs;

public class FranchiseSyncJob(FranchiseSyncRunner runner)
{
    private static readonly TimeSpan PendingWindow = TimeSpan.FromMinutes(10);
    private static long _queuedAtTicks;

    /// <summary>A metadata sweep touches thousands of decks; a run still waiting in the queue already covers every change before it starts.</summary>
    public static void Enqueue(IBackgroundJobClient backgroundJobs)
    {
        var now = DateTime.UtcNow.Ticks;
        var queuedAt = Interlocked.Read(ref _queuedAtTicks);
        if (queuedAt != 0 && now - queuedAt < PendingWindow.Ticks)
            return;
        if (Interlocked.CompareExchange(ref _queuedAtTicks, now, queuedAt) != queuedAt)
            return;

        backgroundJobs.Enqueue<FranchiseSyncJob>(job => job.Run());
    }

    [Queue("stats")]
    public async Task Run()
    {
        Interlocked.Exchange(ref _queuedAtTicks, 0);
        await runner.RunAsync();
    }
}
