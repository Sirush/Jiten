using System.Diagnostics;
using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Jiten.Parser;
using Jiten.Parser.Conjugation;
using Jiten.Parser.Resolution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Jiten.Cli.Commands;

public class ConjugationCommands(CliContext context)
{
    // Rows per binary-COPY batch — split so the server sees periodic commits
    // instead of one giant import. Keeps Postgres happy on huge imports.
    private const int StreamBatchSize = 50_000;
    private const int PageSize = 5000;

    public async Task GenerateConjugations(CliOptions options)
    {
        // Forward is the default — JMdictDB paradigm expansion produces the full
        // ~26M surface table the beam is calibrated for. The legacy BFS generator
        // (ConjugationTableGenerator) produces only ~9M surfaces because the
        // per-word cap truncates depth-3 forms; switching to it regresses the
        // parser-test suite (~20+ tests) even with identical rule sets. Kept
        // reachable via `--conj-mode bfs` purely for reproducing legacy results
        // — NEVER the intended runtime source.
        string mode = options.ConjMode?.ToLowerInvariant() ?? "forward";
        IConjugationGenerator generator = mode switch
        {
            "bfs" => ConjugationTableGenerator.FromSharedResources(),
            _ => ForwardConjugationGenerator.FromSharedResources(),
        };
        // Forward paradigms reach ~800 unique surfaces for a single v1 lemma
        // (primary × secondary × formIdx × fml/neg/onum combinatorics) —
        // BFS's 300 cap truncates imperative/volitional entirely. Raise the
        // cap when the caller didn't override it.
        if (mode != "bfs" && options.ConjugationsPerWordCap == 300)
        {
            options.ConjugationsPerWordCap = 1500;
            Console.WriteLine("  (forward mode: raised per-word cap to 1500)");
        }
        Console.WriteLine($"Conjugation generator mode: {mode}");

        // Use a dedicated raw NpgsqlConnection for writes: the binary importer
        // holds the connection in "Copy" state, so it can't share a connection
        // with EF Core's paging query. We open a second connection below for
        // reads via the DbContext.
        var connString = context.Configuration.GetConnectionString("JitenDatabase");
        if (string.IsNullOrEmpty(connString))
            throw new InvalidOperationException("JitenDatabase connection string is not configured.");

        await using var writeConn = new NpgsqlConnection(connString);
        await writeConn.OpenAsync();

        Console.WriteLine("Truncating jmdict.\"ConjugatedForms\"...");
        await using (var truncate = writeConn.CreateCommand())
        {
            truncate.CommandText = @"TRUNCATE jmdict.""ConjugatedForms"" RESTART IDENTITY";
            await truncate.ExecuteNonQueryAsync();
        }

        int totalWords;
        await using (var countCtx = await context.ContextFactory.CreateDbContextAsync())
        {
            totalWords = await countCtx.JMDictWords.CountAsync();
        }
        Console.WriteLine($"Total JMDict words: {totalWords}. Streaming with forms...");

        var sw = Stopwatch.StartNew();
        long emitted = 0;
        long processedWords = 0;
        long conjugableWords = 0;
        long cappedWords = 0;
        int lastId = -1;

        NpgsqlBinaryImporter? writer = null;
        int batchRows = 0;

        async Task OpenWriter()
        {
            writer = await writeConn.BeginBinaryImportAsync(
                @"COPY jmdict.""ConjugatedForms"" (""Surface"", ""WordId"", ""ConjugationChain"", ""FormIndex"") FROM STDIN (FORMAT BINARY)");
            batchRows = 0;
        }

        async Task CloseWriter()
        {
            if (writer != null)
            {
                await writer.CompleteAsync();
                await writer.DisposeAsync();
                writer = null;
            }
        }

        try
        {
            await OpenWriter();

            while (true)
            {
                // Dedicated read context per page — paging + Include keeps
                // memory flat.
                List<JmDictWord> page;
                await using (var readCtx = await context.ContextFactory.CreateDbContextAsync())
                {
                    page = await readCtx.JMDictWords
                        .AsNoTracking()
                        .Include(w => w.Forms)
                        .Where(w => w.WordId > lastId)
                        .OrderBy(w => w.WordId)
                        .Take(PageSize)
                        .ToListAsync();
                }

                if (page.Count == 0) break;
                lastId = page[^1].WordId;

                foreach (var word in page)
                {
                    processedWords++;
                    if (!generator.IsConjugable(word)) continue;
                    conjugableWords++;

                    long wordEmitted = 0;
                    foreach (var rec in generator.Generate(word, options.ConjugationsMaxDepth, options.ConjugationsPerWordCap))
                    {
                        await writer!.StartRowAsync();
                        await writer.WriteAsync(rec.Surface);
                        await writer.WriteAsync(rec.WordId);
                        await writer.WriteAsync(rec.Chain, NpgsqlDbType.Array | NpgsqlDbType.Text);
                        await writer.WriteAsync(rec.FormIndex, NpgsqlDbType.Smallint);

                        batchRows++;
                        emitted++;
                        wordEmitted++;

                        if (batchRows >= StreamBatchSize)
                        {
                            await CloseWriter();
                            await OpenWriter();
                        }
                    }

                    if (wordEmitted >= options.ConjugationsPerWordCap)
                    {
                        cappedWords++;
                        Console.WriteLine($"  cap hit: wordId={word.WordId} emitted {wordEmitted}");
                    }
                }

                Console.WriteLine($"  processed {processedWords}/{totalWords}  (conjugable: {conjugableWords}, rows: {emitted}, elapsed {sw.ElapsedMilliseconds}ms)");
            }

            await CloseWriter();
        }
        catch
        {
            if (writer != null) await writer.DisposeAsync();
            throw;
        }

        sw.Stop();
        Console.WriteLine($"Done. Processed {processedWords} words ({conjugableWords} conjugable, {cappedWords} capped). Emitted {emitted} rows in {sw.Elapsed}.");

        await WriteBinaryCache();

        await FlushRedis();
    }

    public async Task InspectSurface(string surface)
    {
        var connString = context.Configuration.GetConnectionString("JitenDatabase");
        if (string.IsNullOrEmpty(connString))
            throw new InvalidOperationException("JitenDatabase connection string is not configured.");

        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT cf.""Surface"", cf.""WordId"", cf.""ConjugationChain"", cf.""FormIndex"",
                                   (SELECT array_to_string(w.""PartsOfSpeech"", ',') FROM jmdict.""Words"" w WHERE w.""WordId"" = cf.""WordId"") AS pos,
                                   (SELECT string_agg(f.""Text"", '/') FROM jmdict.""WordForms"" f WHERE f.""WordId"" = cf.""WordId"") AS forms
                            FROM jmdict.""ConjugatedForms"" cf
                            WHERE cf.""Surface"" = @s
                            LIMIT 50";
        cmd.Parameters.AddWithValue("s", surface);
        await using var r = await cmd.ExecuteReaderAsync();
        int n = 0;
        while (await r.ReadAsync())
        {
            var s = r.GetString(0);
            var wid = r.GetInt32(1);
            var chain = r.IsDBNull(2) ? Array.Empty<string>() : (string[])r.GetValue(2);
            var fi = r.GetInt16(3);
            var pos = r.IsDBNull(4) ? "" : r.GetString(4);
            var forms = r.IsDBNull(5) ? "" : r.GetString(5);
            Console.WriteLine($"  '{s}' wordId={wid} formIdx={fi} pos={pos}  forms={forms}  chain=[{string.Join(", ", chain)}]");
            n++;
        }
        if (n == 0) Console.WriteLine($"  (no rows for surface '{surface}')");
    }

    public async Task PrintStats()
    {
        var connString = context.Configuration.GetConnectionString("JitenDatabase");
        if (string.IsNullOrEmpty(connString))
            throw new InvalidOperationException("JitenDatabase connection string is not configured.");

        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();

        Console.WriteLine("=== Row / chain / length distribution ===");
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT COALESCE(array_length(""ConjugationChain"", 1), 0) AS len, COUNT(*)
                FROM jmdict.""ConjugatedForms""
                GROUP BY len
                ORDER BY len";
            await using var r = await cmd.ExecuteReaderAsync();
            long grand = 0;
            while (await r.ReadAsync())
            {
                var len = r.GetInt32(0);
                var cnt = r.GetInt64(1);
                grand += cnt;
                Console.WriteLine($"  chain len={len}: {cnt:N0}");
            }
            Console.WriteLine($"  total: {grand:N0}");
        }

        Console.WriteLine("\n=== Top 30 chains by row count ===");
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT ""ConjugationChain"", COUNT(*)
                FROM jmdict.""ConjugatedForms""
                GROUP BY ""ConjugationChain""
                ORDER BY COUNT(*) DESC
                LIMIT 30";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var chain = (string[])r.GetValue(0);
                var cnt = r.GetInt64(1);
                var s = chain.Length == 0 ? "(identity)" : "[" + string.Join(", ", chain) + "]";
                Console.WriteLine($"  {cnt,12:N0}  {s}");
            }
        }

        Console.WriteLine("\n=== Per-word form count distribution ===");
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                WITH per_word AS (
                    SELECT ""WordId"", COUNT(*) AS c FROM jmdict.""ConjugatedForms"" GROUP BY ""WordId""
                )
                SELECT
                    CASE
                        WHEN c <= 10 THEN '  1..10'
                        WHEN c <= 30 THEN ' 11..30'
                        WHEN c <= 60 THEN ' 31..60'
                        WHEN c <= 100 THEN ' 61..100'
                        WHEN c <= 150 THEN '101..150'
                        WHEN c <= 200 THEN '151..200'
                        WHEN c <= 250 THEN '201..250'
                        WHEN c < 300 THEN '251..299'
                        ELSE '  =300 (capped)'
                    END AS bucket,
                    COUNT(*) AS words,
                    SUM(c) AS rows
                FROM per_word
                GROUP BY bucket
                ORDER BY MIN(c)";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var bucket = r.GetString(0);
                var words = r.GetInt64(1);
                var rows = r.GetInt64(2);
                Console.WriteLine($"  {bucket}  words={words,7:N0}  rows={rows,12:N0}");
            }
        }

        Console.WriteLine("\n=== Top 20 words by form count ===");
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT cf.""WordId"", COUNT(*) AS c,
                       (SELECT string_agg(f.""Text"", '/')
                          FROM jmdict.""Words"" w
                          JOIN jmdict.""WordForms"" f ON f.""WordId"" = w.""WordId""
                         WHERE w.""WordId"" = cf.""WordId"") AS forms,
                       (SELECT array_to_string(w.""PartsOfSpeech"", ',')
                          FROM jmdict.""Words"" w WHERE w.""WordId"" = cf.""WordId"") AS pos
                FROM jmdict.""ConjugatedForms"" cf
                GROUP BY cf.""WordId""
                ORDER BY c DESC
                LIMIT 20";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var wordId = r.GetInt32(0);
                var cnt = r.GetInt64(1);
                var forms = r.IsDBNull(2) ? "" : r.GetString(2);
                var pos = r.IsDBNull(3) ? "" : r.GetString(3);
                Console.WriteLine($"  wordId={wordId,10}  count={cnt,5}  pos={pos}  forms={forms}");
            }
        }
    }

    private async Task WriteBinaryCache()
    {
        // Build an in-memory ConjugationTable from the freshly-populated Postgres
        // rows, then serialise to the packed binary file. This is what the
        // parser loads on startup — skipping the ~80s Npgsql scan each process.
        Console.WriteLine("Building in-memory table from Postgres for binary cache...");
        var table = await ConjugationTableLoader.BuildFromDatabaseAsync(context.ContextFactory, Console.WriteLine);

        var primaryPath = ConjugationTableBinaryFile.DefaultPath;
        ConjugationTableBinaryFile.Write(table, primaryPath, Console.WriteLine);

        // Also drop a copy into the repo's Shared/resources/ (if present) so a
        // rebuild of sibling projects — Api, Tests — picks it up via the normal
        // Shared copy-to-output. Silently skipped in deployed installs.
        var sharedPath = ConjugationTableBinaryFile.FindSharedResourcesPath();
        if (sharedPath != null && !string.Equals(sharedPath, primaryPath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Copy(primaryPath, sharedPath, overwrite: true);
                Console.WriteLine($"Mirrored binary cache to {sharedPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to mirror binary cache to {sharedPath}: {ex.Message}");
            }
        }
    }

    private Task FlushRedis() => context.FlushRedisAsync();
}
