using Jiten.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jiten.Cli.Commands;

public class VectorCommands(CliContext context)
{
    private DeckVectorService CreateService() =>
        new(context.ContextFactory, NullLogger<DeckVectorService>.Instance);

    private string? ResolveModelPath(CliOptions options) =>
        options.FtModel ?? context.Configuration[DeckVectorService.ModelPathConfigKey];

    public async Task ComputeVectors(CliOptions options)
    {
        var modelPath = ResolveModelPath(options);
        if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
        {
            Console.WriteLine("--compute-vectors needs the FastText model: pass --ft-model <path> or set FastTextModelPath in config.");
            return;
        }

        var service = CreateService();
        Console.WriteLine("Computing dense FastText deck vectors...");
        await service.ComputeAsync(modelPath);
        await service.SaveToDbAsync();
        Console.WriteLine($"Saved {service.VectorCount} deck vectors (dim {service.Dimension}) to Postgres.");
    }

    public async Task SimilarTo(CliOptions options)
    {
        var deckId = options.SimilarTo!.Value;
        var service = CreateService();
        if (!await service.LoadFromDbAsync())
        {
            Console.WriteLine("No embeddings in the database — run --compute-vectors first.");
            return;
        }

        if (!service.TryGetVector(deckId, out _))
        {
            Console.WriteLine($"Deck {deckId} has no vector (not a parent deck, or fewer than {DeckVectorService.MinContentWords} content words).");
            return;
        }

        var results = service.FindSimilar(deckId, options.SimilarLimit);
        if (results.Count == 0)
        {
            Console.WriteLine("No similar decks found.");
            return;
        }

        await using var db = await context.ContextFactory.CreateDbContextAsync();
        var titles = await DeckVectorCliHelpers.LoadTitles(db, results.Select(r => r.DeckId).Append(deckId).ToList());

        if (!options.Explain)
        {
            Console.WriteLine($"\nMost similar to [{deckId}] {titles.GetValueOrDefault(deckId, "?")}:\n");
            var rank0 = 1;
            foreach (var r in results)
                Console.WriteLine($"{rank0++,2}. {r.Similarity * 100,5:F1}%  [{r.DeckId}] {titles.GetValueOrDefault(r.DeckId, "?")}");
            return;
        }

        var (srcWords, overlaps) = await service.ExplainOverlapAsync(deckId, results.Select(r => r.DeckId).ToList());
        var overlapById = overlaps.ToDictionary(o => o.DeckId);

        Console.WriteLine($"\nMost similar to [{deckId}] {titles.GetValueOrDefault(deckId, "?")}  ({srcWords} content words):\n");
        Console.WriteLine(" #   cos%  shared  tgtWords  distinct%   sig%  deck");
        var rank = 1;
        foreach (var r in results)
        {
            var title = titles.GetValueOrDefault(r.DeckId, "?");
            var sig = service.SignatureOverlap(deckId, r.DeckId) * 100;
            if (!overlapById.TryGetValue(r.DeckId, out var o))
            {
                Console.WriteLine($"{rank++,2}. {r.Similarity * 100,5:F1}%      -        -         -  {sig,5:F1}%  [{r.DeckId}] {title}");
                continue;
            }

            Console.WriteLine($"{rank++,2}. {r.Similarity * 100,5:F1}%  {o.SharedCount,6}  {o.TargetContentWords,8}  {o.DistinctiveOverlap * 100,7:F1}%  {sig,5:F1}%  [{r.DeckId}] {title}");
            if (o.TopShared.Count > 0)
                Console.WriteLine($"      shared: {string.Join("  ", o.TopShared.Select(t => $"{t.Text}({t.Idf:F1})"))}");
        }

        // What the endpoint would actually return for a short-regime deck (overlap gate applied).
        var floor = options.OverlapFloor ?? DeckVectorService.DefaultOverlapFloor;
        var minShared = options.MinShared ?? DeckVectorService.MinSharedDistinctiveWords;
        var gated = service.FindSimilarGated(deckId, options.SimilarLimit, DeckVectorService.GatedOverFetch, floor, minShared, DeckVectorService.MinAnchorIdf);
        var gatedTitles = await DeckVectorCliHelpers.LoadTitles(db, gated.Select(g => g.DeckId).ToList());
        var sharedByMatch = gated.ToDictionary(g => g.DeckId, g => service.SharedSignatureWords(deckId, g.DeckId).Take(10).ToList());
        var wordTexts = await DeckVectorCliHelpers.LoadWordTexts(db, sharedByMatch.Values.SelectMany(x => x).Distinct().ToList());
        Console.WriteLine($"\n--- Gated result (source < {DeckVectorService.ShortRegimeUniqueWords} uniq; floor {floor * 100:F0}%, min-shared {minShared}): {gated.Count} match(es) ---");
        foreach (var g in gated)
        {
            Console.WriteLine($"  {g.Overlap * 100,5:F1}% ov  {g.SharedCount,3} shared  {g.Similarity * 100,4:F0}% cos  [{g.DeckId}] {gatedTitles.GetValueOrDefault(g.DeckId, "?")}");
            var words = sharedByMatch[g.DeckId].Select(id => $"{wordTexts.GetValueOrDefault(id, id.ToString())}({service.Idf(id):F1})");
            Console.WriteLine($"        distinctive: {string.Join("  ", words)}");
        }
    }
}
