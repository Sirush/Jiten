namespace Jiten.Api.Services;

public class DerivationLinkCacheWarmupService(IServiceProvider services, ILogger<DerivationLinkCacheWarmupService> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
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

        return Task.CompletedTask;
    }
}
