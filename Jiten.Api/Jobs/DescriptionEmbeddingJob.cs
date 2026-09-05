using Hangfire;
using Jiten.Core.Services;

namespace Jiten.Api.Jobs;

/// <summary>Hourly sweep that embeds new or edited deck descriptions for natural-language search.</summary>
public class DescriptionEmbeddingJob(DescriptionSearchService searchService, ILogger<DescriptionEmbeddingJob> logger)
{
    private const int MaxEmbedsPerRun = 200;

    [Queue("stats")]
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public async Task Sync(CancellationToken ct)
    {
        if (!searchService.IsAvailable)
            return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (embedded, removed) = await searchService.SyncAsync(maxEmbeds: MaxEmbedsPerRun, ct: ct);
        logger.LogInformation("DescriptionEmbeddingJob: embedded {Embedded}, removed {Removed} in {ElapsedMs}ms ({Total} vectors live)",
            embedded, removed, sw.ElapsedMilliseconds, searchService.VectorCount);
    }
}
