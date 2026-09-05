using Jiten.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jiten.Cli.Commands;

public class DescriptionSearchCommands(CliContext context)
{
    private DescriptionSearchService? CreateService()
    {
        var dir = context.Configuration[SentenceEmbedder.ModelDirConfigKey];
        if (!SentenceEmbedder.IsAvailable(dir))
        {
            Console.WriteLine($"No sentence embedding model at DescriptionEmbeddingModelDir='{dir}' (needs onnx/model.onnx and sentencepiece.bpe.model).");
            return null;
        }

        return new DescriptionSearchService(context.ContextFactory, () => new SentenceEmbedder(dir!), NullLogger<DescriptionSearchService>.Instance);
    }

    public async Task Embed(CliOptions options)
    {
        var service = CreateService();
        if (service == null)
            return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await service.LoadFromDbAsync();
        var (embedded, removed) = await service.SyncAsync(options.Force);
        Console.WriteLine($"Embedded {embedded} descriptions, removed {removed} stale vectors in {sw.Elapsed.TotalSeconds:F1}s ({service.VectorCount} vectors total).");
    }

    public async Task Search(CliOptions options)
    {
        var service = CreateService();
        if (service == null)
            return;

        var loaded = await service.LoadFromDbAsync();
        if (loaded == 0)
        {
            Console.WriteLine("No description embeddings in the database. Run --embed-descriptions first.");
            return;
        }

        var parsed = DescriptionQueryParser.Parse(options.DescribeSearch!);
        HashSet<int>? allowed = null;
        if (parsed.MediaType != null)
        {
            await using var filterDb = await context.ContextFactory.CreateDbContextAsync();
            allowed = filterDb.Decks.Where(d => d.ParentDeckId == null && d.MediaType == parsed.MediaType).Select(d => d.DeckId).ToHashSet();
            Console.WriteLine($"Detected media type {parsed.MediaType}; ranking on \"{parsed.Text}\" over {allowed.Count} decks.");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = service.Search(parsed.Text, options.SimilarLimit, allowed, cutNoise: !options.Explain);
        var elapsed = sw.Elapsed.TotalMilliseconds;

        await using var db = await context.ContextFactory.CreateDbContextAsync();
        var titles = await DeckVectorCliHelpers.LoadTitles(db, results.Select(r => r.DeckId).ToList());

        var expanded = DescriptionQueryGlossary.Expand(parsed.Text);
        if (expanded != parsed.Text)
            Console.WriteLine($"Embedded as: \"{expanded}\"");
        if (options.Explain)
            Console.WriteLine($"Keywords: {string.Join(", ", DescriptionKeywords.Extract(parsed.Text))}");

        Console.WriteLine($"\n\"{options.DescribeSearch}\" over {loaded} decks in {elapsed:F0}ms:\n");
        var rank = 1;
        var kept = service.Search(parsed.Text, options.SimilarLimit, allowed).Select(m => m.DeckId).ToHashSet();
        foreach (var m in results)
        {
            var mark = options.Explain && !kept.Contains(m.DeckId) ? "x" : " ";
            var line = $"{mark}{rank,3}. {m.Score:F3}  [{m.DeckId}] {titles.GetValueOrDefault(m.DeckId, "?")}";
            if (options.Explain)
                line += $"   cosine {m.Cosine:F3} + boost {m.Score - m.Cosine:F3}" + (m.KeywordsHit.Length > 0 ? $" ({string.Join(", ", m.KeywordsHit)})" : "");
            Console.WriteLine(line);
            rank++;
        }
    }
}
