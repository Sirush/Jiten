using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Jiten.Cli.Commands;

public class ImportCommands(CliContext context)
{
    public async Task ImportJmDict(CliOptions options)
    {
        if (string.IsNullOrEmpty(options.XmlPath) || string.IsNullOrEmpty(options.DictionaryPath) ||
            string.IsNullOrEmpty(options.FuriganaPath))
        {
            Console.WriteLine("For import, you need to specify -xml path/to/jmdict_dtd.xml, -dic path/to/jmdict and -furi path/to/JmdictFurigana.json.");
            return;
        }

        Console.WriteLine("Importing JMdict...");
        await JmDictHelper.Import(context.ContextFactory, options.XmlPath, options.DictionaryPath, options.FuriganaPath);
        await JmDictHelper.ImportJMNedict(context.ContextFactory, options.NameDictionaryPath!);
    }

    public async Task ImportKanjidic(string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"KANJIDIC2 file not found: {path}");
            return;
        }

        Console.WriteLine("Importing KANJIDIC2...");
        await KanjidicHelper.Import(context.ContextFactory, path);
    }

    public async Task PopulateWordKanji()
    {
        Console.WriteLine("Populating WordKanji junction table...");
        await KanjidicHelper.PopulateWordKanji(context.ContextFactory);
    }

    public async Task ComputeKanjiReadings()
    {
        Console.WriteLine("Computing kanji reading associations...");
        await KanjidicHelper.ComputeKanjiReadings(context.ContextFactory);
    }

    public async Task ImportWordComposition(CliOptions options)
    {
        await WordCompositionImporter.Import(
            context.ContextFactory,
            options.ImportWordCompositionDirectory!,
            options.SplitType,
            options.DryRun);
    }

    public async Task ImportPitchAccents(CliOptions options)
    {
        Console.WriteLine("Importing pitch accents...");
        await JmDictHelper.ImportPitchAccents(options.Verbose, context.ContextFactory, options.ImportPitchAccents!);
        Console.WriteLine("Pitch accents imported.");
    }

    public async Task ImportVocabularyOrigin(CliOptions options)
    {
        Console.WriteLine("Importing vocabulary origin...");
        await JmDictHelper.ImportVocabularyOrigin(options.Verbose, context.ContextFactory, options.ImportVocabularyOrigin!);
        Console.WriteLine("Vocabulary origin imported.");
    }

    public async Task SyncJMNedict(CliOptions options)
    {
        if (string.IsNullOrEmpty(options.XmlPath))
        {
            Console.WriteLine("For JMNedict sync, you need to specify -xml path/to/jmdict_dtd.xml");
            return;
        }

        Console.WriteLine("Syncing JMNedict entries with database...");
        await JmDictHelper.SyncMissingJMNedict(context.ContextFactory, options.XmlPath, options.SyncJMNedict!);
        Console.WriteLine("JMNedict sync complete.");
    }

    public async Task SyncJmDict(CliOptions options)
    {
        if (string.IsNullOrEmpty(options.XmlPath) || string.IsNullOrEmpty(options.DictionaryPath) ||
            string.IsNullOrEmpty(options.FuriganaPath))
        {
            Console.WriteLine("For JMDict sync, you need to specify --xml path/to/jmdict_dtd.xml, --dic path/to/jmdict and --furi path/to/JmdictFurigana.json.");
            return;
        }

        Console.WriteLine("Syncing JMDict entries with database...");
        var reportPath = options.DryRun ? (options.Output ?? "jmdict-sync-changes.txt") : null;
        await JmDictHelper.SyncJmDict(context.ContextFactory, options.XmlPath, options.DictionaryPath, options.FuriganaPath,
            options.DryRun, reportPath);
        Console.WriteLine("JMDict sync complete.");
    }

    public async Task CompareJMDict(CliOptions options)
    {
        if (string.IsNullOrEmpty(options.XmlPath) || string.IsNullOrEmpty(options.DictionaryPath) || options.Extra == null)
        {
            Console.WriteLine("Usage : -xml dtdPath -dic oldDictionaryPath -x newDictionaryPath");
            return;
        }

        await JmDictHelper.CompareJMDicts(options.XmlPath, options.DictionaryPath, options.Extra);
    }

    public async Task ApplyMigrations()
    {
        Console.WriteLine("Applying migrations to the Jiten database.");
        await using var jitenContext = new JitenDbContext(context.DbOptions);
        await jitenContext.Database.MigrateAsync();
        Console.WriteLine("Migrations applied to the Jiten database.");

        Console.WriteLine("Applying migrations to the User database.");
        var connectionString = context.Configuration.GetConnectionString("JitenDatabase");
        var userOptionsBuilder = new DbContextOptionsBuilder<UserDbContext>();
        userOptionsBuilder.UseNpgsql(connectionString, o => { o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery); });
        await using var userContext = new UserDbContext(userOptionsBuilder.Options);
        await userContext.Database.MigrateAsync();
        Console.WriteLine("Migrations applied to the User database.");
    }
}
