using Jiten.Core.Services;

namespace Jiten.Api.Services;

public class DeckVectorCacheWarmupService(IServiceProvider services, ILogger<DeckVectorCacheWarmupService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var service = services.GetRequiredService<DeckVectorService>();
            await service.LoadFromDbAsync();
            logger.LogInformation("DeckVectorService warmup completed in {ElapsedMs}ms ({Count} vectors)",
                sw.ElapsedMilliseconds, service.VectorCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DeckVectorService warmup failed");
        }

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var search = services.GetRequiredService<DescriptionSearchService>();
            var count = await search.LoadFromDbAsync();
            search.EnsureModelLoaded();
            logger.LogInformation("DescriptionSearchService warmup completed in {ElapsedMs}ms ({Count} vectors)", sw.ElapsedMilliseconds, count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DescriptionSearchService warmup failed");
        }
    }
}
