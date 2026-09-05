namespace Jiten.Api.Services;

public class DerivationLinkCacheWarmupService(IServiceProvider services, StartupReadiness readiness,
                                              ILogger<DerivationLinkCacheWarmupService> logger)
    : BackgroundService
{
    // The singleton loads in its constructor; resolving it off the host thread keeps Kestrel's bind from waiting on it.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() =>
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _ = services.GetRequiredService<IDerivationLinkCache>();
            logger.LogInformation("DerivationLinkCache warmup completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DerivationLinkCache warmup failed");
        }
        finally
        {
            readiness.MarkReady(StartupReadiness.DerivationLinks);
        }
    }, stoppingToken);
}
