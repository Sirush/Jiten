using Jiten.Core.Services;

namespace Jiten.Cli.Commands;

public class SiteStatsCommands(CliContext context)
{
    public async Task WordsForCoverage(int sampleModulo)
    {
        var options = new Core.Services.WordsForCoverage.Options(SampleModulo: sampleModulo);
        var results = await Core.Services.WordsForCoverage.ComputeAsync(context.ContextFactory, options, Console.WriteLine);

        var levels = Core.Services.WordsForCoverage.Levels;
        Console.WriteLine();
        Console.WriteLine($"{"MediaType",-12} {"Titles",7} {string.Join(" ", levels.Select(l => $"{l * 100,8:0}%"))} {"Excluded",9}");
        foreach (var r in results)
            Console.WriteLine($"{r.MediaType,-12} {r.Titles,7} {string.Join(" ", r.MedianRanks.Select(v => $"{v,9:N0}"))} {r.MedianExcludedPercent,8:F1}%");

        Console.WriteLine();
        Console.WriteLine("Library words");
        foreach (var r in results)
            Console.WriteLine($"{r.MediaType,-12} {r.Titles,7} {string.Join(" ", r.LibraryWords.Select(v => $"{v,9:N0}"))}");
    }
}
