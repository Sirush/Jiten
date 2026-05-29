using System.Diagnostics;
using CommandLine;
using Jiten.Cli;
using Jiten.Cli.Commands;
using Jiten.Core;
using Microsoft.EntityFrameworkCore;

// ReSharper disable MethodSupportsCancellation

public class Program
{
    static async Task Main(string[] args)
    {
        var cliContext = CliContext.Create();

        await Parser.Default.ParseArguments<CliOptions>(args)
                    .WithParsedAsync<CliOptions>(async options =>
                    {
                        var watch = Stopwatch.StartNew();

                        if (options.Threads > 1)
                        {
                            Console.WriteLine($"Using {options.Threads} threads.");
                        }

                        await ExecuteCommands(cliContext, options);

                        if (options.Verbose)
                            Console.WriteLine($"Execution time: {watch.ElapsedMilliseconds} ms");
                    });
    }

    private static async Task ExecuteCommands(CliContext context, CliOptions options)
    {
        var importCommands = new ImportCommands(context);
        var deckCommands = new DeckCommands(context);
        var extractionCommands = new ExtractionCommands();
        var diagnosticCommands = new DiagnosticCommands(context);
        var dictionaryCommands = new DictionaryCommands(context);
        var adminCommands = new AdminCommands(context);
        var mlCommands = new MlCommands(context);
        var metadataCommands = new MetadataCommands();
        var benchmarkCommands = new BenchmarkCommands(context);
        var rubyExtractCommands = new RubyExtractCommands(context);

        // Import commands
        if (options.Import)
        {
            await importCommands.ImportJmDict(options);
        }

        if (!string.IsNullOrEmpty(options.ImportKanjidic))
        {
            await importCommands.ImportKanjidic(options.ImportKanjidic);
        }

        if (options.PopulateWordKanji)
        {
            await importCommands.PopulateWordKanji();
        }

        if (options.ComputeKanjiReadings)
        {
            await importCommands.ComputeKanjiReadings();
        }

        if (!string.IsNullOrEmpty(options.ImportPitchAccents))
        {
            await importCommands.ImportPitchAccents(options);
        }

        if (!string.IsNullOrEmpty(options.ImportVocabularyOrigin))
        {
            await importCommands.ImportVocabularyOrigin(options);
        }

        if (!string.IsNullOrEmpty(options.SyncJMNedict))
        {
            await importCommands.SyncJMNedict(options);
        }

        if (options.SyncJmDict)
        {
            await importCommands.SyncJmDict(options);
        }

        if (options.CompareJMDict)
        {
            await importCommands.CompareJMDict(options);
        }

        if (options.ApplyMigrations)
        {
            await importCommands.ApplyMigrations();
        }

        // Extraction commands
        if (options.ExtractFilePath != null)
        {
            if (await extractionCommands.Extract(options)) return;
        }

        // Metadata commands
        if (options.Metadata != null)
        {
            await metadataCommands.DownloadMetadata(options);
        }

        // Deck commands
        if (options.Parse != null)
        {
            await deckCommands.Parse(options);
        }

        if (options.Insert != null)
        {
            if (await deckCommands.Insert(options)) return;
        }

        if (options.BackfillSpeechStats != null)
        {
            await deckCommands.BackfillSpeechStats(options);
        }

        if (options.BackfillSpeechStatsJimaku)
        {
            await deckCommands.BackfillSpeechStatsFromJimaku(options);
        }

        if (options.ComputeFrequencies)
        {
            await JitenHelper.ComputeFrequencies(context.ContextFactory);
        }

        if (options.DebugDeck != null)
        {
            await JitenHelper.DebugDeck(context.ContextFactory, options.DebugDeck.Value);
        }

        // Diagnostic commands
        if (options.ParseTest != null)
        {
            await diagnosticCommands.ParseTest(options);
        }

        if (options.ParseDeckTest != null)
        {
            await diagnosticCommands.ParseDeckTest(options);
        }

        if (options.RunParserTests)
        {
            await diagnosticCommands.RunParserTests(options);
        }

        if (options.RunFormTests)
        {
            await diagnosticCommands.RunFormTests(options);
        }

        if (options.DeconjugateTest != null)
        {
            await diagnosticCommands.DeconjugateTest(options);
        }

        if (!string.IsNullOrEmpty(options.SearchWord))
        {
            await diagnosticCommands.SearchWord(options.SearchWord);
        }

        if (!string.IsNullOrEmpty(options.SearchLookup))
        {
            await diagnosticCommands.SearchLookup(options.SearchLookup);
        }

        if (options.FlushRedis)
        {
            await diagnosticCommands.FlushRedisCache();
        }

        if (options.ScanConfidence)
        {
            await diagnosticCommands.ScanConfidence(options);
        }

        // Dictionary commands
        if (!string.IsNullOrEmpty(options.UserDicMassAdd))
        {
            if (string.IsNullOrEmpty(options.XmlPath))
            {
                Console.WriteLine("You need to specify -xml path/to/user_dic.xml");
                return;
            }

            Console.WriteLine("Importing words...");
            await dictionaryCommands.AddWordsToUserDictionary(options.UserDicMassAdd, options.XmlPath);
        }

        if (!string.IsNullOrEmpty(options.PruneSudachiCsvDirectory))
        {
            Console.WriteLine("Pruning files...");
            await dictionaryCommands.PruneSudachiCsvFiles(options.PruneSudachiCsvDirectory);
        }

        if (!string.IsNullOrEmpty(options.ImportWordCompositionDirectory))
        {
            await importCommands.ImportWordComposition(options);
            return;
        }

        // WordSet commands
        if (options.CreateWordSetFromPos)
        {
            if (string.IsNullOrEmpty(options.SetSlug) || string.IsNullOrEmpty(options.SetName) || string.IsNullOrEmpty(options.Pos))
            {
                Console.WriteLine("For --create-wordset-from-pos, you need to specify --set-slug, --set-name, and --pos.");
                return;
            }
        }

        if (options.CreateWordSetFromCsv)
        {
            if (string.IsNullOrEmpty(options.SetSlug) || string.IsNullOrEmpty(options.SetName) || string.IsNullOrEmpty(options.CsvFile))
            {
                Console.WriteLine("For --create-wordset-from-csv, you need to specify --set-slug, --set-name, and --csv-file.");
                return;
            }

            if (!File.Exists(options.CsvFile))
            {
                Console.WriteLine($"CSV file not found: {options.CsvFile}");
                return;
            }
        }

        // Admin commands
        if (options.RegisterAdmin && !string.IsNullOrEmpty(options.Email) && !string.IsNullOrEmpty(options.Username) &&
            !string.IsNullOrEmpty(options.Password))
        {
            await adminCommands.RegisterAdmin(options.Email, options.Username, options.Password);
        }

        // ML commands
        if (!string.IsNullOrEmpty(options.ExtractFeatures))
        {
            await mlCommands.ExtractFeatures(options.ExtractFeatures);
        }

        if (!string.IsNullOrEmpty(options.ExtractFeaturesDb))
        {
            await mlCommands.ExtractFeaturesDb(options.ExtractFeaturesDb);
        }

        if (options.ExtractMorphemes)
        {
            await mlCommands.ExtractMorphemes();
        }

        if (!string.IsNullOrEmpty(options.ImportDeckDifficulty))
        {
            await mlCommands.ImportDeckDifficulty(options.ImportDeckDifficulty);
        }

        if (!string.IsNullOrEmpty(options.ExportMlTags))
        {
            await mlCommands.ExportMlTags(options.ExportMlTags);
        }

        // Ruby extraction commands
        if (!string.IsNullOrEmpty(options.ExtractRuby))
        {
            await rubyExtractCommands.ExtractRuby(options);
        }

        if (!string.IsNullOrEmpty(options.AlignRuby))
        {
            var rubyAlignCommands = new RubyAlignCommands(context);
            await rubyAlignCommands.AlignRuby(options);
        }

        if (!string.IsNullOrEmpty(options.CheckRubyReadings))
        {
            var rubyAlignCommands = new RubyAlignCommands(context);
            await rubyAlignCommands.CheckRubyReadings(options);
        }

        if (!string.IsNullOrEmpty(options.RubyChangedWinners))
        {
            var rubyAlignCommands = new RubyAlignCommands(context);
            await rubyAlignCommands.RubyChangedWinnerReport(options);
        }

        if (!string.IsNullOrEmpty(options.RubyReprune))
        {
            RubyAlignCommands.RepruneArtifact(options.RubyReprune, options.Output);
        }

        // Benchmark commands
        if (!string.IsNullOrEmpty(options.Benchmark))
        {
            await benchmarkCommands.RunBenchmark(options);
        }

        // SRS maintenance commands
        if (options.CleanupGhostCards)
        {
            await CleanupGhostCards(context, options.DryRun);
        }

        if (options.CleanupNewCards)
        {
            await CleanupNewCards(context, options.DryRun);
        }
    }

    private static async Task CleanupGhostCards(CliContext context, bool dryRun)
    {
        await using var jitenDb = await context.ContextFactory.CreateDbContextAsync();

        // Count ghost cards using raw SQL (cross-schema query)
        var countSql = """
            SELECT COUNT(*) FROM "user"."FsrsCards" fc
            JOIN "jmdict"."WordForms" kana_wf
              ON kana_wf."WordId" = fc."WordId" AND kana_wf."ReadingIndex" = fc."ReadingIndex"
            WHERE kana_wf."FormType" = 1
              AND EXISTS (
                  SELECT 1 FROM "user"."FsrsCards" fc2
                  JOIN "jmdict"."WordForms" kanji_wf
                    ON kanji_wf."WordId" = fc2."WordId" AND kanji_wf."ReadingIndex" = fc2."ReadingIndex"
                  WHERE fc2."UserId" = fc."UserId" AND fc2."WordId" = fc."WordId"
                    AND kanji_wf."FormType" = 0
              )
              AND NOT EXISTS (
                  SELECT 1 FROM "user"."FsrsReviewLogs" rl WHERE rl."CardId" = fc."CardId"
              )
            """;

        var ghostCount = await jitenDb.Database.SqlQueryRaw<int>($"SELECT ({countSql})::int AS \"Value\"").FirstAsync();
        Console.WriteLine($"Found {ghostCount} ghost kana cards with 0 review logs.");

        if (ghostCount == 0) return;

        // Show per-user breakdown
        var breakdownSql = """
            SELECT fc."UserId"::text AS "Key", COUNT(*)::int AS "Value"
            FROM "user"."FsrsCards" fc
            JOIN "jmdict"."WordForms" kana_wf
              ON kana_wf."WordId" = fc."WordId" AND kana_wf."ReadingIndex" = fc."ReadingIndex"
            WHERE kana_wf."FormType" = 1
              AND EXISTS (
                  SELECT 1 FROM "user"."FsrsCards" fc2
                  JOIN "jmdict"."WordForms" kanji_wf
                    ON kanji_wf."WordId" = fc2."WordId" AND kanji_wf."ReadingIndex" = fc2."ReadingIndex"
                  WHERE fc2."UserId" = fc."UserId" AND fc2."WordId" = fc."WordId"
                    AND kanji_wf."FormType" = 0
              )
              AND NOT EXISTS (
                  SELECT 1 FROM "user"."FsrsReviewLogs" rl WHERE rl."CardId" = fc."CardId"
              )
            GROUP BY fc."UserId"
            ORDER BY COUNT(*) DESC
            LIMIT 20
            """;

        var breakdown = await jitenDb.Database.SqlQueryRaw<UserGhostCount>(breakdownSql).ToListAsync();
        foreach (var row in breakdown)
            Console.WriteLine($"  User {row.Key}: {row.Value} ghost cards");

        if (dryRun)
        {
            Console.WriteLine("Dry run — no changes made.");
            return;
        }

        var deleteSql = """
            DELETE FROM "user"."FsrsCards"
            WHERE "CardId" IN (
                SELECT fc."CardId" FROM "user"."FsrsCards" fc
                JOIN "jmdict"."WordForms" kana_wf
                  ON kana_wf."WordId" = fc."WordId" AND kana_wf."ReadingIndex" = fc."ReadingIndex"
                WHERE kana_wf."FormType" = 1
                  AND EXISTS (
                      SELECT 1 FROM "user"."FsrsCards" fc2
                      JOIN "jmdict"."WordForms" kanji_wf
                        ON kanji_wf."WordId" = fc2."WordId" AND kanji_wf."ReadingIndex" = fc2."ReadingIndex"
                      WHERE fc2."UserId" = fc."UserId" AND fc2."WordId" = fc."WordId"
                        AND kanji_wf."FormType" = 0
                  )
                  AND NOT EXISTS (
                      SELECT 1 FROM "user"."FsrsReviewLogs" rl WHERE rl."CardId" = fc."CardId"
                  )
            )
            """;

        var deleted = await jitenDb.Database.ExecuteSqlRawAsync(deleteSql);
        Console.WriteLine($"Done. Deleted {deleted} ghost cards.");
    }

    private static async Task CleanupNewCards(CliContext context, bool dryRun)
    {
        await using var jitenDb = await context.ContextFactory.CreateDbContextAsync();

        var countSql = """SELECT COUNT(*)::int AS "Value" FROM "user"."FsrsCards" WHERE "State" = 0""";
        var count = await jitenDb.Database.SqlQueryRaw<int>(countSql).FirstAsync();
        Console.WriteLine($"Found {count} legacy FsrsCards with State=New.");

        if (count == 0) return;

        var breakdownSql = """
            SELECT "UserId"::text AS "Key", COUNT(*)::int AS "Value"
            FROM "user"."FsrsCards" WHERE "State" = 0
            GROUP BY "UserId" ORDER BY COUNT(*) DESC LIMIT 20
            """;
        var breakdown = await jitenDb.Database.SqlQueryRaw<UserGhostCount>(breakdownSql).ToListAsync();
        foreach (var row in breakdown)
            Console.WriteLine($"  User {row.Key}: {row.Value} cards");

        if (dryRun)
        {
            Console.WriteLine("Dry run — no changes made.");
            return;
        }

        var deleteSql = """DELETE FROM "user"."FsrsCards" WHERE "State" = 0""";
        var deleted = await jitenDb.Database.ExecuteSqlRawAsync(deleteSql);
        Console.WriteLine($"Done. Deleted {deleted} legacy New cards.");
    }

    private class UserGhostCount
    {
        public string Key { get; set; } = "";
        public int Value { get; set; }
    }
}
