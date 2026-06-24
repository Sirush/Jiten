using Jiten.Core;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Cli;

/// <summary>
/// Removes the three garbage classes from jmdict."WordCompositions":
///   1. parent is a proper noun (name-type POS) — "Composed of" for names is noise;
///   2. any component is a particle / auxiliary (the composition is really a phrase / expression);
///   3. every component is a single character — redundant with the kanji breakdown (赤本 -> 赤+本).
///
/// All three are predicate-based DELETEs, so this command is idempotent / rerunnable.
/// Use with --dry-run to preview the counts (executed inside a rolled-back transaction).
/// </summary>
public static class CompositionCleaner
{
    // JMnedict proper-noun POS tags. Deliberately excludes the bare JMdict "fem"/"masc"
    // tags ("female/male term"), which are NOT names.
    private static readonly string[] NamePos =
    {
        "person", "place", "station", "organization", "company", "surname",
        "name-fem", "name-masc", "product", "work", "given", "char", "group",
        "obj", "ev", "dei", "myth", "fict", "leg", "serv", "relig", "unclass"
    };

    private static readonly string[] ParticlePos =
    {
        "prt", "aux", "aux-v", "aux-adj", "cop", "conj"
    };

    public static async Task Cleanup(IDbContextFactory<JitenDbContext> contextFactory, bool dryRun)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var totalRows = await context.WordCompositions.CountAsync();
        Console.WriteLine($"WordCompositions rows before cleanup: {totalRows:N0}");

        await using var tx = await context.Database.BeginTransactionAsync();

        // 1. Parent is a name-type proper noun.
        var nameSql = $@"
DELETE FROM jmdict.""WordCompositions"" wc
USING jmdict.""Words"" w
WHERE w.""WordId"" = wc.""WordId""
  AND w.""PartsOfSpeech"" && {ArrayLiteral(NamePos)};";

        // 2. The (WordId, ReadingIndex) group contains a particle / auxiliary component (= a phrase).
        var particleSql = $@"
DELETE FROM jmdict.""WordCompositions"" wc
WHERE (wc.""WordId"", wc.""ReadingIndex"") IN (
    SELECT DISTINCT x.""WordId"", x.""ReadingIndex""
    FROM jmdict.""WordCompositions"" x
    JOIN jmdict.""Words"" cw ON cw.""WordId"" = x.""ComponentWordId""
    WHERE cw.""PartsOfSpeech"" && {ArrayLiteral(ParticlePos)}
);";

        // 3. Every component in the group is a single character (redundant with the kanji breakdown).
        var singleCharSql = @"
DELETE FROM jmdict.""WordCompositions"" wc
WHERE (wc.""WordId"", wc.""ReadingIndex"") IN (
    SELECT x.""WordId"", x.""ReadingIndex""
    FROM jmdict.""WordCompositions"" x
    GROUP BY x.""WordId"", x.""ReadingIndex""
    HAVING bool_and(char_length(x.""ComponentSurface"") = 1)
);";

        var nameDeleted = await context.Database.ExecuteSqlRawAsync(nameSql);
        var particleDeleted = await context.Database.ExecuteSqlRawAsync(particleSql);
        var singleCharDeleted = await context.Database.ExecuteSqlRawAsync(singleCharSql);

        var totalDeleted = nameDeleted + particleDeleted + singleCharDeleted;

        Console.WriteLine($@"=== Composition Cleanup ({(dryRun ? "DRY RUN" : "APPLIED")}) ===
Name-parent rows deleted:        {nameDeleted:N0}
Particle-component rows deleted: {particleDeleted:N0}
All-single-char rows deleted:    {singleCharDeleted:N0}
Total rows deleted:              {totalDeleted:N0}
Rows remaining:                  {totalRows - totalDeleted:N0}");

        if (dryRun)
        {
            await tx.RollbackAsync();
            Console.WriteLine("Dry-run: rolled back, no changes persisted.");
        }
        else
        {
            await tx.CommitAsync();
            Console.WriteLine("Cleanup committed.");
        }
    }

    private static string ArrayLiteral(string[] tags)
    {
        // tags are compile-time constants (no user input) — safe to inline.
        var quoted = string.Join(", ", tags.Select(t => $"'{t}'"));
        return $"ARRAY[{quoted}]::text[]";
    }
}
