using Hangfire;
using Jiten.Api.Services;
using Jiten.Core.Services;

namespace Jiten.Api.Jobs;

/// <summary>
/// Maintains the dense FastText deck vectors. <see cref="Recompute"/> is the nightly full rebuild
/// (refit SIF transform, persist to DeckEmbeddings + DeckEmbeddingSpace, refresh the live cache).
/// <see cref="EmbedPending"/> is the periodic incremental sweep that embeds decks added/reparsed
/// since the last run, using the persisted transform. Both require "FastTextModelPath" in config.
/// </summary>
public class RecomputeVectorsJob(
    DeckVectorService vectorService,
    IPendingEmbeddingQueue pendingQueue,
    IConfiguration configuration,
    ILogger<RecomputeVectorsJob> logger)
{
    private string? ModelPath()
    {
        var path = configuration[DeckVectorService.ModelPathConfigKey];
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            return path;
        logger.LogWarning("RecomputeVectorsJob: FastText model not found at FastTextModelPath='{Path}'", path);
        return null;
    }

    [Queue("stats")]
    public async Task Recompute()
    {
        var modelPath = ModelPath();
        if (modelPath == null)
            return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await vectorService.ComputeAsync(modelPath);
        await vectorService.SaveToDbAsync();
        logger.LogInformation("RecomputeVectorsJob finished in {ElapsedMs}ms ({Count} vectors)",
            sw.ElapsedMilliseconds, vectorService.VectorCount);
    }

    [Queue("stats")]
    public async Task EmbedPending()
    {
        var pending = await pendingQueue.DrainAsync();
        if (pending.Count == 0)
            return;

        var modelPath = ModelPath();
        if (modelPath == null)
        {
            // Re-queue so a later run (once the model is available) doesn't drop them.
            foreach (var id in pending)
                await pendingQueue.AddAsync(id);
            return;
        }

        var count = await vectorService.EmbedDecksAsync(modelPath, pending);
        logger.LogInformation("RecomputeVectorsJob.EmbedPending embedded {Count}/{Pending} decks", count, pending.Count);
    }
}
