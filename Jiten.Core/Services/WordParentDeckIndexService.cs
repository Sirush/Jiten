using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jiten.Core.Services;

/// <summary>Maintains the parent-only word index that the full coverage recompute reads instead of DeckWords.</summary>
public static class WordParentDeckIndexService
{
    /// <summary>A parse stamps LastUpdate before its DeckWords COPY lands, so parents touched this close to a build are treated as not covered by it.</summary>
    public static readonly TimeSpan BuildSafetyMargin = TimeSpan.FromHours(1);

    /// <summary>Above this many stale parents a rebuild is cheaper than the per-deck fallback, and it keeps that fallback too small for a bad plan.</summary>
    public const int StaleParentRebuildThreshold = 1000;

    private const long RebuildLockKey = 7_212_026_001;

    /// <summary>Parents the index does not describe: updated after its snapshot ({0}) or absent from the built set ({1}). Yields "DeckId".</summary>
    public const string StaleParentsQuery = """
        SELECT d."DeckId"
        FROM "jiten"."Decks" d
        LEFT JOIN unnest({1}::int[]) AS built(id) ON built.id = d."DeckId"
        WHERE d."ParentDeckId" IS NULL
          AND (built.id IS NULL OR d."LastUpdate" > {0}::timestamptz)
        """;

    public sealed record BuildState(DateTime BuiltAt, int[] DeckIds)
    {
        public DateTime CoveredUntil => BuiltAt - BuildSafetyMargin;
    }

    private sealed class BuildRow
    {
        public DateTime BuiltAt { get; set; }
        public int[] DeckIds { get; set; } = [];
    }

    public static async Task<BuildState?> GetBuildAsync(DbContext db)
    {
        var row = await db.Database
                          .SqlQueryRaw<BuildRow>("""SELECT "BuiltAt", "DeckIds" FROM "jiten"."WordParentDeckIndexBuild" WHERE "Id" = 1""")
                          .FirstOrDefaultAsync();
        return row == null ? null : new BuildState(row.BuiltAt, row.DeckIds);
    }

    public static Task<int> CountStaleParentsAsync(DbContext db, BuildState build)
        => db.Database
             .SqlQueryRaw<int>($"""SELECT COUNT(*)::int AS "Value" FROM ({StaleParentsQuery}) s""",
                               build.CoveredUntil, build.DeckIds)
             .SingleAsync();

    /// <summary>Returns a build the recompute can rely on, rebuilding first when the index is missing, empty, or has more stale parents than the fallback should carry.</summary>
    public static async Task<BuildState> EnsureFreshAsync(DbContext db, ILogger logger)
    {
        var requestedAt = DateTime.UtcNow;
        var build = await GetBuildAsync(db);
        if (build != null)
        {
            var hasRows = await db.Database
                                  .SqlQueryRaw<bool>("""SELECT EXISTS (SELECT 1 FROM "jiten"."WordParentDeckIndex") AS "Value" """)
                                  .SingleAsync();
            var stale = hasRows ? await CountStaleParentsAsync(db, build) : int.MaxValue;
            if (stale <= StaleParentRebuildThreshold)
                return build;

            logger.LogInformation("WordParentDeckIndex: {Stale} stale parents exceed {Threshold}, rebuilding", stale,
                                  StaleParentRebuildThreshold);
        }

        await RebuildAsync(db, logger, skipIfBuiltAfter: requestedAt);
        return await GetBuildAsync(db) ?? throw new InvalidOperationException("WordParentDeckIndex has no build row after a rebuild");
    }

    /// <summary>Rebuilds the index from DeckWords in one transaction, serialised on an advisory lock; a rebuild that landed after <paramref name="skipIfBuiltAfter"/> makes this one a no-op.</summary>
    public static async Task RebuildAsync(DbContext db, ILogger logger, DateTime? skipIfBuiltAfter = null)
    {
        var sw = Stopwatch.StartNew();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0});", RebuildLockKey);

        if (skipIfBuiltAfter != null)
        {
            var current = await GetBuildAsync(db);
            if (current != null && current.BuiltAt > skipIfBuiltAfter)
            {
                await tx.RollbackAsync();
                logger.LogInformation("WordParentDeckIndex: rebuilt concurrently at {BuiltAt:O}, skipping", current.BuiltAt);
                return;
            }
        }

        await db.Database.ExecuteSqlRawAsync("SET LOCAL work_mem = '1GB';");
        await db.Database.ExecuteSqlRawAsync("SET LOCAL max_parallel_workers_per_gather = 4;");
        // CTAS may use parallel workers for the scan and aggregate; INSERT ... SELECT may not.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE _wpdi_new ON COMMIT DROP AS
            SELECT dw."WordId", dw."ReadingIndex",
                   array_agg(dw."DeckId" ORDER BY dw."DeckId") AS "DeckIds",
                   array_agg(dw."Occurrences" ORDER BY dw."DeckId") AS "Occurrences"
            FROM "jiten"."DeckWords" dw
            JOIN "jiten"."Decks" d ON d."DeckId" = dw."DeckId"
            WHERE d."ParentDeckId" IS NULL
            GROUP BY dw."WordId", dw."ReadingIndex";
            """);
        await db.Database.ExecuteSqlRawAsync("""TRUNCATE "jiten"."WordParentDeckIndex";""");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "jiten"."WordParentDeckIndex" ("WordId", "ReadingIndex", "DeckIds", "Occurrences")
            SELECT "WordId", "ReadingIndex", "DeckIds", "Occurrences" FROM _wpdi_new;
            """);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "jiten"."WordParentDeckIndexBuild" ("Id", "BuiltAt", "DeckIds")
            SELECT 1, now(), COALESCE(array_agg("DeckId"), ARRAY[]::int[])
            FROM "jiten"."Decks" WHERE "ParentDeckId" IS NULL
            ON CONFLICT ("Id") DO UPDATE SET "BuiltAt" = EXCLUDED."BuiltAt", "DeckIds" = EXCLUDED."DeckIds";
            """);
        await db.Database.ExecuteSqlRawAsync("""ANALYZE "jiten"."WordParentDeckIndex";""");
        await tx.CommitAsync();
        logger.LogInformation("WordParentDeckIndex rebuilt in {Elapsed}ms", sw.ElapsedMilliseconds);
    }
}
