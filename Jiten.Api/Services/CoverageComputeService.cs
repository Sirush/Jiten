using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public static class CoverageComputeService
{
    private const int COVERAGE_CHUNK_SIZE = 1024;

    private sealed class ChildCoverageRow
    {
        public int DeckId { get; set; }
        public short MatureCov { get; set; }
        public short MatureUCov { get; set; }
        public short YoungCov { get; set; }
        public short YoungUCov { get; set; }
    }

    // Creates _mature_known and _fsrs_young temp tables (ON COMMIT DROP - must be inside a transaction)
    public static async Task CreateKnownWordsTempTablesAsync(UserDbContext userContext, Guid userGuid)
    {
        await userContext.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE _mature_known ON COMMIT DROP AS
            WITH
            fsrs_mature_direct AS (
                SELECT fc."WordId", fc."ReadingIndex"
                FROM "user"."FsrsCards" fc
                WHERE fc."UserId" = {0}::uuid
                  AND (
                      -- Blacklisted/Mastered only; Suspended keeps its interval-derived tier
                      fc."State" IN (4, 5)
                      OR (fc."LastReview" IS NOT NULL AND (fc."Due" - fc."LastReview") >= INTERVAL '21 days')
                  )
            ),
            fsrs_mature AS (
                SELECT "WordId", "ReadingIndex" FROM fsrs_mature_direct
                UNION
                SELECT kana_wf."WordId", kana_wf."ReadingIndex"
                FROM fsrs_mature_direct fmd
                JOIN "jmdict"."WordForms" kanji_wf ON kanji_wf."WordId" = fmd."WordId" AND kanji_wf."ReadingIndex" = fmd."ReadingIndex" AND kanji_wf."FormType" = 0
                JOIN "jmdict"."WordForms" kana_wf ON kana_wf."WordId" = fmd."WordId" AND kana_wf."FormType" = 1
            )
            SELECT "WordId", "ReadingIndex" FROM fsrs_mature
            UNION
            SELECT wsm."WordId", wsm."ReadingIndex"
            FROM "user"."UserWordSetStates" uwss
            JOIN "jiten"."WordSetMembers" wsm ON wsm."SetId" = uwss."SetId"
            WHERE uwss."UserId" = {0}::uuid
              AND NOT EXISTS (
                  SELECT 1
                  FROM "user"."FsrsCards" fc
                  WHERE fc."UserId" = {0}::uuid
                    AND fc."WordId" = wsm."WordId"
                    AND fc."ReadingIndex" = wsm."ReadingIndex"
              );
            """, userGuid);
        await userContext.Database.ExecuteSqlRawAsync("""CREATE INDEX ON _mature_known ("WordId", "ReadingIndex");""");
        await userContext.Database.ExecuteSqlRawAsync("ANALYZE _mature_known;");

        await userContext.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE _fsrs_young ON COMMIT DROP AS
            WITH
            fsrs_young_direct AS (
                SELECT fc."WordId", fc."ReadingIndex"
                FROM "user"."FsrsCards" fc
                WHERE fc."UserId" = {0}::uuid
                  AND fc."State" IN (1, 2, 3, 6)
                  AND fc."LastReview" IS NOT NULL
                  AND (fc."Due" - fc."LastReview") < INTERVAL '21 days'
            ),
            young_expanded AS (
                SELECT "WordId", "ReadingIndex" FROM fsrs_young_direct
                UNION
                SELECT kana_wf."WordId", kana_wf."ReadingIndex"
                FROM fsrs_young_direct fyd
                JOIN "jmdict"."WordForms" kanji_wf ON kanji_wf."WordId" = fyd."WordId" AND kanji_wf."ReadingIndex" = fyd."ReadingIndex" AND kanji_wf."FormType" = 0
                JOIN "jmdict"."WordForms" kana_wf ON kana_wf."WordId" = fyd."WordId" AND kana_wf."FormType" = 1
            )
            SELECT ye."WordId", ye."ReadingIndex"
            FROM young_expanded ye
            WHERE NOT EXISTS (
                SELECT 1 FROM _mature_known mk
                WHERE mk."WordId" = ye."WordId" AND mk."ReadingIndex" = ye."ReadingIndex"
            );
            """, userGuid);
        await userContext.Database.ExecuteSqlRawAsync("""CREATE INDEX ON _fsrs_young ("WordId", "ReadingIndex");""");
        await userContext.Database.ExecuteSqlRawAsync("ANALYZE _fsrs_young;");
    }

    private static async Task<List<ChildCoverageRow>> ComputeCoverageRowsAsync(
        UserDbContext userContext, string whereClause, object param)
    {
        var sql = $"""
            SELECT d."DeckId",
                   CASE WHEN d."WordCount" = 0 THEN 0::smallint
                        ELSE LEAST(ROUND(COALESCE(SUM(dw."Occurrences") FILTER (WHERE mk."WordId" IS NOT NULL), 0)::numeric * 10000 / d."WordCount")::int, 10000)::smallint
                   END AS "MatureCov",
                   CASE WHEN d."UniqueWordCount" = 0 THEN 0::smallint
                        ELSE LEAST(ROUND(COALESCE(COUNT(*) FILTER (WHERE mk."WordId" IS NOT NULL), 0)::numeric * 10000 / d."UniqueWordCount")::int, 10000)::smallint
                   END AS "MatureUCov",
                   CASE WHEN d."WordCount" = 0 THEN 0::smallint
                        ELSE LEAST(ROUND(COALESCE(SUM(dw."Occurrences") FILTER (WHERE yk."WordId" IS NOT NULL), 0)::numeric * 10000 / d."WordCount")::int, 10000)::smallint
                   END AS "YoungCov",
                   CASE WHEN d."UniqueWordCount" = 0 THEN 0::smallint
                        ELSE LEAST(ROUND(COALESCE(COUNT(*) FILTER (WHERE yk."WordId" IS NOT NULL), 0)::numeric * 10000 / d."UniqueWordCount")::int, 10000)::smallint
                   END AS "YoungUCov"
            FROM "jiten"."Decks" d
            LEFT JOIN "jiten"."DeckWords" dw ON dw."DeckId" = d."DeckId"
            LEFT JOIN _mature_known mk ON mk."WordId" = dw."WordId" AND mk."ReadingIndex" = dw."ReadingIndex"
            LEFT JOIN _fsrs_young yk ON yk."WordId" = dw."WordId" AND yk."ReadingIndex" = dw."ReadingIndex"
            WHERE {whereClause}
            GROUP BY d."DeckId", d."WordCount", d."UniqueWordCount"
            """;
        return await userContext.Database.SqlQueryRaw<ChildCoverageRow>(sql, param).ToListAsync();
    }

    // Efficient read-modify-write upsert: loads affected chunks once, updates slots in memory, writes back in bulk
    private static async Task UpsertCoverageChunksAsync(
        UserDbContext userContext, string userId,
        IReadOnlyList<ChildCoverageRow> rows, DateTime computedAt)
    {
        if (rows.Count == 0) return;

        var userGuid = Guid.Parse(userId);
        var chunkIndices = rows.Select(r => r.DeckId / COVERAGE_CHUNK_SIZE).Distinct().ToList();
        var metricValues = new short[] { 1, 2, 3, 4 };

        // Ensure all needed chunks exist (one INSERT per chunk handles all 4 metrics)
        foreach (var chunkIndex in chunkIndices)
        {
            await userContext.Database.ExecuteSqlRawAsync($$"""
                INSERT INTO "user"."UserCoverageChunks" ("UserId", "Metric", "ChunkIndex", "Values", "ComputedAt")
                VALUES
                    ({0}::uuid, 1::smallint, {1}::int, array_fill(0::smallint, ARRAY[{{COVERAGE_CHUNK_SIZE}}]), {2}::timestamptz),
                    ({0}::uuid, 2::smallint, {1}::int, array_fill(0::smallint, ARRAY[{{COVERAGE_CHUNK_SIZE}}]), {2}::timestamptz),
                    ({0}::uuid, 3::smallint, {1}::int, array_fill(0::smallint, ARRAY[{{COVERAGE_CHUNK_SIZE}}]), {2}::timestamptz),
                    ({0}::uuid, 4::smallint, {1}::int, array_fill(0::smallint, ARRAY[{{COVERAGE_CHUNK_SIZE}}]), {2}::timestamptz)
                ON CONFLICT ("UserId", "Metric", "ChunkIndex") DO NOTHING;
                """, userGuid, chunkIndex, computedAt);
        }

        // Load chunks to update
        var existingChunks = await userContext.UserCoverageChunks
            .AsNoTracking()
            .Where(c => c.UserId == userId && chunkIndices.Contains(c.ChunkIndex) && metricValues.Contains(c.Metric))
            .ToListAsync();

        var chunkMap = existingChunks.ToDictionary(
            c => (c.Metric, c.ChunkIndex),
            c => c.Values.ToArray()); // copy so we can mutate

        foreach (var row in rows)
        {
            var chunkIndex = row.DeckId / COVERAGE_CHUNK_SIZE;
            var slot = row.DeckId % COVERAGE_CHUNK_SIZE;
            Apply((short)UserCoverageMetric.MatureCoverage, row.MatureCov);
            Apply((short)UserCoverageMetric.MatureUniqueCoverage, row.MatureUCov);
            Apply((short)UserCoverageMetric.YoungCoverage, row.YoungCov);
            Apply((short)UserCoverageMetric.YoungUniqueCoverage, row.YoungUCov);

            void Apply(short metric, short value)
            {
                if (chunkMap.TryGetValue((metric, chunkIndex), out var arr) && slot < arr.Length)
                    arr[slot] = value;
            }
        }

        foreach (var ((metric, chunkIndex), newValues) in chunkMap)
        {
            await userContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE "user"."UserCoverageChunks"
                SET "Values" = {0}::smallint[], "ComputedAt" = {1}::timestamptz
                WHERE "UserId" = {2}::uuid AND "Metric" = {3}::smallint AND "ChunkIndex" = {4}::int
                """,
                newValues, computedAt, userGuid, metric, chunkIndex);
        }
    }

    // Called inline from the controller: compute coverage for the visible page of children
    public static async Task ComputeSpecificDecksAsync(
        UserDbContext userContext, string userId, IReadOnlyList<int> deckIds)
    {
        var userGuid = Guid.Parse(userId);
        var computedAt = DateTime.UtcNow;
        await using var tx = await userContext.Database.BeginTransactionAsync();
        await userContext.Database.ExecuteSqlRawAsync("SET LOCAL work_mem = '64MB';");
        await CreateKnownWordsTempTablesAsync(userContext, userGuid);
        var rows = await ComputeCoverageRowsAsync(userContext, """d."DeckId" = ANY({0})""", deckIds.ToArray());
        await UpsertCoverageChunksAsync(userContext, userId, rows, computedAt);
        await tx.CommitAsync();
    }

    // Called from the background Hangfire job: compute coverage for ALL children of a parent
    public static async Task ComputeAllChildrenAsync(
        UserDbContext userContext, string userId, int parentDeckId)
    {
        var userGuid = Guid.Parse(userId);
        var computedAt = DateTime.UtcNow;
        await using var tx = await userContext.Database.BeginTransactionAsync();
        await userContext.Database.ExecuteSqlRawAsync("SET LOCAL work_mem = '64MB';");
        await CreateKnownWordsTempTablesAsync(userContext, userGuid);
        var rows = await ComputeCoverageRowsAsync(userContext, """d."ParentDeckId" = {0}""", parentDeckId);
        await UpsertCoverageChunksAsync(userContext, userId, rows, computedAt);
        await tx.CommitAsync();
    }
}
