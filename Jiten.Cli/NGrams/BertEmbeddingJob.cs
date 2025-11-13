using Jiten.Core;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Cli.NGrams;

public class BertEmbeddingJob
{
    private readonly JitenDbContext _dbContext;
    private readonly BertModelService _bertModel;

    public BertEmbeddingJob(DbContextOptions<JitenDbContext> dbOptions, BertModelService bertModel)
    {
        _dbContext = new JitenDbContext(dbOptions);
        _bertModel = bertModel;
    }
    
    /// <summary>
    /// Compute BERT embeddings for all pending n-grams
    /// </summary>
    public async Task ComputeEmbeddingsAsync(
        BertEmbeddingConfig config,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Starting BERT embedding computation");

        var batchSize = config.BatchSize;
        var processedCount = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Fetch pending n-grams (prioritize by significance)
            var pendingNgrams = await _dbContext.Set<PrecomputedNgram>()
                                                .Where(n => !n.BertEmbeddingComputed)
                                                .OrderByDescending(n => n.SignificanceScore)
                                                .ThenByDescending(n => n.Occurrences)
                                                .Take(batchSize)
                                                .ToListAsync(cancellationToken);

            if (pendingNgrams.Count == 0)
            {
                Console.WriteLine("No more pending n-grams to process");
                break;
            }

            // Compute embeddings in batch
            await ProcessEmbeddingBatchAsync(pendingNgrams, cancellationToken);

            processedCount += pendingNgrams.Count;

            if (processedCount % 100 == 0)
            {
                var remaining = await _dbContext.Set<PrecomputedNgram>()
                                                .CountAsync(n => !n.BertEmbeddingComputed, cancellationToken);

                Console.WriteLine(
                                  $"Processed {processedCount} n-grams, {remaining} remaining");
            }

            // Add small delay to prevent overloading
            await Task.Delay(config.DelayBetweenBatches, cancellationToken);
        }

        Console.WriteLine(
                          $"BERT embedding computation completed. Processed {processedCount} n-grams");
    }

    private async Task ProcessEmbeddingBatchAsync(
        List<PrecomputedNgram> ngrams,
        CancellationToken cancellationToken)
    {
        // Compute embeddings in parallel with controlled concurrency
        var semaphore = new SemaphoreSlim(4); // Max 4 concurrent BERT inferences

        var tasks = ngrams.Select(async ngram =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var embedding = await _bertModel.GetEmbeddingAsync(ngram.FullContext);

                ngram.BertEmbedding = embedding.Embedding;
                ngram.BertEmbeddingComputed = true;
                ngram.LastUpdated = DateTimeOffset.UtcNow;

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                                  $"Error computing embedding for n-gram {ngram.NgramId}: {ngram.FullContext}");
                return false;
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        // Save to database
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}

public class BertEmbeddingConfig
{
    public int BatchSize { get; set; } = 50;
    public int DelayBetweenBatches { get; set; } = 100; // milliseconds
    public int MaxConcurrentInferences { get; set; } = 4;
}