using Jiten.Core.WebNovel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jiten.Cli.Commands;

public class WebNovelCommands
{
    /// <summary>
    /// Dry run: fetches a novel's metadata, table of contents and first episode, and reports how it would be
    /// split into subdecks. Writes nothing. Use it to check the site's markup after a failed sync.
    /// </summary>
    public async Task Test(string url, int? chunkCharBudget)
    {
        if (!WebNovelUrlParser.TryParse(url, out var provider, out var sourceId))
        {
            Console.WriteLine($"'{url}' is not a supported webnovel URL or ncode.");
            return;
        }

        var services = new ServiceCollection();
        services.AddHttpClient(SyosetuSource.HttpClientName, client =>
                {
                    client.DefaultRequestHeaders.Add("User-Agent",
                                                     "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                                                     "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                    client.Timeout = TimeSpan.FromSeconds(30);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
                });

        await using var serviceProvider = services.BuildServiceProvider();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

        var source = new SyosetuSource(httpClientFactory, NullLogger<SyosetuSource>.Instance, provider);

        Console.WriteLine($"Provider: {provider}   Source id: {sourceId}");
        Console.WriteLine("Fetching metadata...");

        var info = await source.GetInfoAsync(sourceId);

        Console.WriteLine();
        Console.WriteLine($"  Title       : {info.Title}");
        Console.WriteLine($"  Author      : {info.Author}");
        Console.WriteLine($"  Genre       : {info.Genre}");
        Console.WriteLine($"  Keywords    : {string.Join(", ", info.Keywords)}");
        Console.WriteLine($"  Episodes    : {info.EpisodeCount}");
        Console.WriteLine($"  Characters  : {info.TotalCharacters:N0}");
        Console.WriteLine($"  First up    : {info.FirstPublishedAt:yyyy-MM-dd}");
        Console.WriteLine($"  Last up     : {info.LastUpdatedAt:yyyy-MM-dd HH:mm}");
        Console.WriteLine($"  One-shot    : {info.IsOneShot}   Completed: {info.IsCompleted}   Hiatus: {info.IsOnHiatus}   R15: {info.IsR15}");
        Console.WriteLine($"  Synopsis    : {Preview(info.Synopsis, 120)}");

        Console.WriteLine();
        Console.WriteLine("Fetching table of contents...");
        var toc = await source.GetTocAsync(sourceId);
        Console.WriteLine($"  {toc.Count} episodes listed (API reported {info.EpisodeCount})");

        if (toc.Count == 0)
            return;

        foreach (var episode in toc.Take(3))
        {
            Console.WriteLine($"    #{episode.Number,-4} {Preview(episode.Title, 40),-42} " +
                              $"updated {episode.UpdatedAt:yyyy-MM-dd HH:mm}   section: {episode.SectionTitle}");
        }

        if (toc.Count > 3)
            Console.WriteLine($"    ... #{toc[^1].Number} {Preview(toc[^1].Title, 40)}");

        Console.WriteLine();
        Console.WriteLine($"Fetching episode #{toc[0].Number}...");
        var text = await source.GetEpisodeTextAsync(sourceId, toc[0]);

        var characters = SubdeckChunker.CountCharacters(text);
        var rubyCount = text.Count(c => c == '{');

        Console.WriteLine($"  Raw length      : {text.Length:N0}");
        Console.WriteLine($"  Counted chars   : {characters:N0}  (furigana + whitespace excluded)");
        Console.WriteLine($"  Ruby annotations: {rubyCount}");
        Console.WriteLine();
        Console.WriteLine("  --- first 300 chars ---");
        Console.WriteLine("  " + Preview(text, 300).Replace("\n", "\n  "));
        Console.WriteLine("  ---");

        // Project the whole work using this episode's length as the per-episode estimate
        var budget = chunkCharBudget ?? SubdeckChunker.DefaultCharBudget;
        var averageChars = info.EpisodeCount > 0 && info.TotalCharacters > 0
            ? (int)(info.TotalCharacters / info.EpisodeCount)
            : characters;

        var projected = SubdeckChunker.Plan([],
                                            toc.Select(e => new ChunkEpisode(e.Number, e.Title, averageChars)).ToList(),
                                            budget);

        Console.WriteLine();
        Console.WriteLine($"Subdeck projection (budget {budget:N0} chars, ~{averageChars:N0} chars/episode):");
        Console.WriteLine($"  {projected.Count} subdecks");

        foreach (var plan in projected.Take(5))
            Console.WriteLine($"    [{plan.ChunkIndex}] {plan.Title}  ({plan.EpisodesToAppend.Count} episodes)");

        if (projected.Count > 5)
            Console.WriteLine($"    ... [{projected[^1].ChunkIndex}] {projected[^1].Title} ({projected[^1].EpisodesToAppend.Count} episodes)");
    }

    private static string Preview(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var single = value.Length <= maxLength ? value : value[..maxLength] + "…";
        return single;
    }
}
