namespace Jiten.Api.Services;

public class WordFormSiblingCacheWarmupService(IServiceProvider services, StartupReadiness readiness,
                                               ILogger<WordFormSiblingCacheWarmupService> logger)
    : BackgroundService
{
    // The singleton loads in its constructor; resolving it off the host thread keeps Kestrel's bind from waiting on it.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() =>
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _ = services.GetRequiredService<IWordFormSiblingCache>();
            logger.LogInformation("WordFormSiblingCache warmup completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WordFormSiblingCache warmup failed");
        }
        finally
        {
            readiness.MarkReady(StartupReadiness.WordFormSiblings);
        }
    }, stoppingToken);
}
