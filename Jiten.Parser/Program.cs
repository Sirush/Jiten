using Jiten.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Jiten.Parser;

public static class Program
{
    public static async Task Main(string[] args)
    {
        // var text = "「あそこ美味しいよねー。早くお祭り終わって欲しいなー。ノンビリ遊びに行きたーい」";
        // var text = await File.ReadAllTextAsync("Y:\\00_JapaneseStudy\\JL\\Backlogs\\Default_2024.12.28_10.52.47-2024.12.28_19.58.40.txt");
        // text = """
        //        部屋の床が大きく崩れたのだ。
        //        「……」「……」「……」「……！」
        //        「！」「！」「！」「！」「！」「！」「！」「！」「！」「！」足場が抜けたことに、仕掛けた一人を除く十人がそれぞれ驚き、それぞれ落下しつつも、しかしそれぞれに対処する――すわ、これが第十二回十二大戦の幕開けだった。
        //        """;

        var text = "新しそうだ";
        var configuration = new ConfigurationBuilder()
                            .SetBasePath(Directory.GetCurrentDirectory())
                            .AddJsonFile("sharedsettings.json", optional: true, reloadOnChange: true)
                            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                            .AddEnvironmentVariables()
                            .Build();

        var connectionString = configuration.GetConnectionString("JitenDatabase");
        var optionsBuilder = new DbContextOptionsBuilder<JitenDbContext>();
        optionsBuilder.UseNpgsql(connectionString, o => { o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery); });

        // Create a simple factory for the standalone parser
        var factory = new SimpleDbContextFactory(optionsBuilder.Options);

        // await Parser.ParseTextToDeck(factory, text, predictDifficulty:false);
        await Parser.ParseText(factory, text);
    }

    // Simple factory implementation for standalone usage
    private class SimpleDbContextFactory : IDbContextFactory<JitenDbContext>
    {
        private readonly DbContextOptions<JitenDbContext> _options;

        public SimpleDbContextFactory(DbContextOptions<JitenDbContext> options)
        {
            _options = options;
        }

        public JitenDbContext CreateDbContext()
        {
            return new JitenDbContext(_options);
        }

        // Console.InputEncoding = Encoding.UTF8;
        // Console.OutputEncoding = Encoding.UTF8;
        //
        // while (true)
        // {
        //     var text = Console.ReadLine();
        //
        //     if (string.IsNullOrWhiteSpace(text))
        //         return;
        //
        //     var deck = await ParseTextToDeck(context, text);
        //     Console.WriteLine(JsonSerializer.Serialize(deck));
        //     Console.WriteLine();
        // }
    }
}