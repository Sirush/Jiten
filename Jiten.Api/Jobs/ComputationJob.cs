using System.Text;
using CsvHelper;
using Hangfire;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace Jiten.Api.Jobs;

public class ComputationJob(
    IDbContextFactory<JitenDbContext> contextFactory,
    IDbContextFactory<UserDbContext> userContextFactory,
    IConfiguration configuration,
    IBackgroundJobClient backgroundJobs,
    IPendingCoverageQueue pendingCoverageQueue,
    ILogger<ComputationJob> logger)
{
    private static readonly object CoverageComputeLock = new();
    private static readonly HashSet<string> CoverageComputingUserIds = new();
    private const int COVERAGE_CHUNK_SIZE = 1024;

    private const string CoverageWordStateCtes = """
            fsrs_mature_direct AS (
                SELECT fc."WordId", fc."ReadingIndex"
                FROM "user"."FsrsCards" fc
                WHERE fc."UserId" = {0}::uuid
                  AND (
                      fc."State" IN (4, 5, 6)
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
            ),
            wordset_known AS (
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
                  )
            ),
            mature_known_direct AS (
                SELECT "WordId", "ReadingIndex" FROM fsrs_mature_direct
                UNION
                SELECT "WordId", "ReadingIndex" FROM wordset_known
            ),
            mature_known AS (
                SELECT "WordId", "ReadingIndex" FROM mature_known_direct
                UNION
                SELECT kana_wf."WordId", kana_wf."ReadingIndex"
                FROM mature_known_direct mkd
                JOIN "jmdict"."WordForms" kanji_wf ON kanji_wf."WordId" = mkd."WordId" AND kanji_wf."ReadingIndex" = mkd."ReadingIndex" AND kanji_wf."FormType" = 0
                JOIN "jmdict"."WordForms" kana_wf ON kana_wf."WordId" = mkd."WordId" AND kana_wf."FormType" = 1
            ),
            fsrs_young_direct AS (
                SELECT fc."WordId", fc."ReadingIndex"
                FROM "user"."FsrsCards" fc
                WHERE fc."UserId" = {0}::uuid
                  AND fc."State" IN (1, 2, 3, 6)
                  AND fc."LastReview" IS NOT NULL
                  AND (fc."Due" - fc."LastReview") < INTERVAL '21 days'
            ),
            fsrs_young AS (
                SELECT y."WordId", y."ReadingIndex" FROM (
                    SELECT "WordId", "ReadingIndex" FROM fsrs_young_direct
                    UNION
                    SELECT kana_wf."WordId", kana_wf."ReadingIndex"
                    FROM fsrs_young_direct fyd
                    JOIN "jmdict"."WordForms" kanji_wf ON kanji_wf."WordId" = fyd."WordId" AND kanji_wf."ReadingIndex" = fyd."ReadingIndex" AND kanji_wf."FormType" = 0
                    JOIN "jmdict"."WordForms" kana_wf ON kana_wf."WordId" = fyd."WordId" AND kana_wf."FormType" = 1
                ) y
                WHERE NOT EXISTS (
                    SELECT 1 FROM mature_known mk
                    WHERE mk."WordId" = y."WordId" AND mk."ReadingIndex" = y."ReadingIndex"
                )
            )
        """;
    private const string COVERAGE_WORK_MEM = "256MB";

    [Queue("coverage")]
    public async Task DailyUserCoverage()
    {
        await using var userContext = await userContextFactory.CreateDbContextAsync();

        // Only refresh coverage for users who've been active recently. Inactive users get a
        // catch-up compute on their next login/refresh via UserActivityTracker.
        var activeThreshold = DateTime.UtcNow.AddDays(-UserActivityTracker.InactiveThresholdDays);
        var userIds = await (from u in userContext.Users.AsNoTracking()
                             join um in userContext.UserMetadatas.AsNoTracking()
                                 on u.Id equals um.UserId
                             where um.LastActivity != null && um.LastActivity >= activeThreshold
                                   && (um.CoverageDirty
                                       || !userContext.UserCoverageChunks.Any(c => c.UserId == u.Id))
                             select u.Id).ToListAsync();

        foreach (var userId in userIds)
        {
            backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserCoverage(userId));
        }
    }

    [Queue("coverage")]
    public async Task ComputeUserCoverage(string userId)
    {
        // Prevent duplicate concurrent computations for the same user
        lock (CoverageComputeLock)
        {
            if (!CoverageComputingUserIds.Add(userId))
                return;
        }

        await using var userContext = await userContextFactory.CreateDbContextAsync();
        userContext.Database.SetCommandTimeout(TimeSpan.FromMinutes(3));

        try
        {
            var computedAt = DateTime.UtcNow;

            // Only compute coverage for users with at least 10 known words or any WordSet subscriptions
            var hasSufficientFsrsCards = await userContext.FsrsCards.CountAsync(fc => fc.UserId == userId) >= 10;
            var hasWordSetSubscriptions = await userContext.UserWordSetStates.AnyAsync(uwss => uwss.UserId == userId);

            if (!hasSufficientFsrsCards && !hasWordSetSubscriptions)
            {
                // Remove existing coverages if they exist, if the user cleared his words for example
                await userContext.UserCoverageChunks.Where(uc => uc.UserId == userId).ExecuteDeleteAsync();
                await UpsertCoverageMetadata(userContext, userId, computedAt, isDirty: false);
                await userContext.SaveChangesAsync();
                return;
            }

            var totalSw = System.Diagnostics.Stopwatch.StartNew();
            await using var transaction = await userContext.Database.BeginTransactionAsync();
            await userContext.Database.ExecuteSqlRawAsync($"SET LOCAL work_mem = '{COVERAGE_WORK_MEM}';");
            await userContext.UserCoverageChunks.Where(uc => uc.UserId == userId).ExecuteDeleteAsync();
            logger.LogInformation("Coverage: old chunks deleted in {Elapsed}ms", totalSw.ElapsedMilliseconds);
            await RecomputeUserCoverageChunks(userContext, userId, computedAt);
            await transaction.CommitAsync();

            await UpsertCoverageMetadata(userContext, userId, computedAt, isDirty: false);
            await userContext.SaveChangesAsync();

            // Queue kanji grid computation
            backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserKanjiGrid(userId));
        }
        finally
        {
            // Ensure removal even if an exception occurs
            lock (CoverageComputeLock)
            {
                CoverageComputingUserIds.Remove(userId);
            }
        }
    }

    // Drains the Redis set of newly-parsed decks and fans out a single batch coverage job
    // per eligible user covering all pending decks. Runs on a recurring schedule (~15min).
    // This coalesces bursts of deck ingest so we enqueue O(users) jobs instead of O(users * decks).
    [Queue("coverage")]
    public async Task SweepPendingCoverageDecks()
    {
        var deckIds = await pendingCoverageQueue.DrainAsync();
        if (deckIds.Count == 0)
        {
            logger.LogDebug("SweepPendingCoverageDecks: nothing to do");
            return;
        }

        await using var ctx = await userContextFactory.CreateDbContextAsync();

        var fsrsEligible = ctx.FsrsCards
            .GroupBy(c => c.UserId)
            .Where(g => g.Count() >= 10)
            .Select(g => g.Key);

        var wordSetEligible = ctx.UserWordSetStates
            .Select(s => s.UserId)
            .Distinct();

        // Restrict to recently-active users. Inactive users get caught up by UserActivityTracker
        // when they next log in or refresh their token.
        var activeThreshold = DateTime.UtcNow.AddDays(-UserActivityTracker.InactiveThresholdDays);
        var activeUserIds = ctx.UserMetadatas
            .Where(um => um.LastActivity != null && um.LastActivity >= activeThreshold)
            .Select(um => um.UserId);

        var userIds = await fsrsEligible.Union(wordSetEligible)
            .Where(id => activeUserIds.Contains(id))
            .Distinct()
            .ToListAsync();

        // Users without any coverage rows need a full compute first; otherwise deck slot updates would make most decks appear as 0.
        var userIdsWithCoverage = await ctx.UserCoverageChunks
            .AsNoTracking()
            .Where(c => userIds.Contains(c.UserId))
            .Select(c => c.UserId)
            .Distinct()
            .ToListAsync();

        var withCoverage = userIdsWithCoverage.ToHashSet();
        var deckIdArray = deckIds.ToArray();

        logger.LogInformation("SweepPendingCoverageDecks: draining {DeckCount} decks for {UserCount} eligible users",
            deckIdArray.Length, userIds.Count);

        foreach (var userId in userIds)
        {
            if (!withCoverage.Contains(userId))
                backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserCoverage(userId));
            else
                backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserDeckCoverageBatch(userId, deckIdArray));
        }
    }

    [Queue("coverage")]
    public async Task ComputeUserDeckCoverageBatch(string userId, int[] deckIds)
    {
        if (deckIds is null || deckIds.Length == 0) return;

        bool lockAcquired;
        lock (CoverageComputeLock)
        {
            lockAcquired = CoverageComputingUserIds.Add(userId);
        }

        if (!lockAcquired)
        {
            // Another coverage computation is already running for this user. Re-queue the
            // deckIds so the next sweep retries them — but bound the number of retries per
            // user so a permanently-stuck lock can't cause an infinite re-queue loop.
            const int maxContentionRetries = 5;
            if (await pendingCoverageQueue.TryRecordContentionAsync(userId, maxContentionRetries))
            {
                await pendingCoverageQueue.AddManyAsync(deckIds);
                logger.LogInformation(
                    "ComputeUserDeckCoverageBatch: user {UserId} contended, re-queued {Count} decks for next sweep",
                    userId, deckIds.Length);
            }
            else
            {
                // Retry budget exhausted — mark the user dirty so RefreshAllDirtyUsers will
                // do a full recompute, which subsumes any dropped deck work.
                await using var fallbackContext = await userContextFactory.CreateDbContextAsync();
                var meta = await fallbackContext.UserMetadatas.FirstOrDefaultAsync(um => um.UserId == userId);
                if (meta == null)
                {
                    meta = new UserMetadata { UserId = userId, CoverageDirty = true, CoverageDirtyAt = DateTime.UtcNow };
                    fallbackContext.UserMetadatas.Add(meta);
                }
                else
                {
                    meta.CoverageDirty = true;
                    meta.CoverageDirtyAt = DateTime.UtcNow;
                }
                await fallbackContext.SaveChangesAsync();
                logger.LogWarning(
                    "ComputeUserDeckCoverageBatch: user {UserId} contention budget exhausted; marked CoverageDirty and dropping {Count} deckIds",
                    userId, deckIds.Length);
            }
            return;
        }

        await using var userContext = await userContextFactory.CreateDbContextAsync();

        try
        {
            var hasSufficientFsrsCards = await userContext.FsrsCards.CountAsync(fc => fc.UserId == userId) >= 10;
            var hasWordSetSubscriptions = await userContext.UserWordSetStates.AnyAsync(uwss => uwss.UserId == userId);

            if (!hasSufficientFsrsCards && !hasWordSetSubscriptions)
                return;

            await CoverageComputeService.ComputeSpecificDecksAsync(userContext, userId, deckIds);
        }
        finally
        {
            lock (CoverageComputeLock)
            {
                CoverageComputingUserIds.Remove(userId);
            }
        }
    }

    [Queue("coverage")]
    public async Task ComputeUserDeckCoverage(string userId, int deckId)
    {
        // Prevent duplicate concurrent computations for the same user
        lock (CoverageComputeLock)
        {
            if (!CoverageComputingUserIds.Add(userId))
                return;
        }

        await using var userContext = await userContextFactory.CreateDbContextAsync();

        try
        {
            var computedAt = DateTime.UtcNow;

            // Only compute coverage for users with at least 10 known words or any WordSet subscriptions
            var hasSufficientFsrsCards = await userContext.FsrsCards.CountAsync(fc => fc.UserId == userId) >= 10;
            var hasWordSetSubscriptions = await userContext.UserWordSetStates.AnyAsync(uwss => uwss.UserId == userId);

            if (!hasSufficientFsrsCards && !hasWordSetSubscriptions)
            {
                return;
            }

            var bp = await ComputeUserDeckCoverageBasisPoints(userContext, userId, deckId);
            await UpsertUserDeckCoverageChunks(userContext, userId, deckId, bp, computedAt);
        }
        finally
        {
            // Ensure removal even if an exception occurs
            lock (CoverageComputeLock)
            {
                CoverageComputingUserIds.Remove(userId);
            }
        }
    }

    [Queue("coverage")]
    public async Task ComputeUserChildrenCoverage(string userId, int parentDeckId)
    {
        lock (CoverageComputeLock)
        {
            if (!CoverageComputingUserIds.Add(userId))
                return;
        }

        await using var userContext = await userContextFactory.CreateDbContextAsync();

        try
        {
            var hasSufficientFsrsCards = await userContext.FsrsCards.CountAsync(fc => fc.UserId == userId) >= 10;
            var hasWordSetSubscriptions = await userContext.UserWordSetStates.AnyAsync(uwss => uwss.UserId == userId);
            if (!hasSufficientFsrsCards && !hasWordSetSubscriptions)
                return;

            await CoverageComputeService.ComputeAllChildrenAsync(userContext, userId, parentDeckId);
        }
        finally
        {
            lock (CoverageComputeLock)
            {
                CoverageComputingUserIds.Remove(userId);
            }
        }
    }

    private sealed class DeckCoverageBasisPoints
    {
        public short MatureCoverageBp { get; set; }
        public short MatureUniqueCoverageBp { get; set; }
        public short YoungCoverageBp { get; set; }
        public short YoungUniqueCoverageBp { get; set; }
    }

    private static async Task UpsertCoverageMetadata(UserDbContext userContext, string userId, DateTime computedAt, bool isDirty)
    {
        var metadata = await userContext.UserMetadatas.SingleOrDefaultAsync(um => um.UserId == userId);
        if (metadata is null)
        {
            metadata = new UserMetadata
            {
                UserId = userId,
                CoverageRefreshedAt = computedAt,
                CoverageDirty = isDirty,
                CoverageDirtyAt = isDirty ? computedAt : null
            };
            await userContext.UserMetadatas.AddAsync(metadata);
            return;
        }

        metadata.CoverageRefreshedAt = computedAt;
        metadata.CoverageDirty = isDirty;
        metadata.CoverageDirtyAt = isDirty ? computedAt : null;
    }

    private async Task RecomputeUserCoverageChunks(UserDbContext userContext, string userId, DateTime computedAt)
    {
        var userGuid = Guid.Parse(userId);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Step 1: Materialize user's mature known words
        // Kana-expand FsrsCards (small set), word sets already store specific forms
        await userContext.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE _mature_known ON COMMIT DROP AS
            WITH
            fsrs_mature_direct AS (
                SELECT fc."WordId", fc."ReadingIndex"
                FROM "user"."FsrsCards" fc
                WHERE fc."UserId" = {0}::uuid
                  AND (
                      fc."State" IN (4, 5, 6)
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
        var matureCount = await userContext.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM _mature_known").SingleAsync();
        logger.LogInformation("Coverage: mature_known materialized ({Count} rows) in {Elapsed}ms", matureCount, sw.ElapsedMilliseconds);
        sw.Restart();

        // Step 2: Materialize user's young words (kana-expanded, excluding mature)
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
        logger.LogInformation("Coverage: fsrs_young materialized in {Elapsed}ms", sw.ElapsedMilliseconds);
        sw.Restart();

        // Step 3a: Materialise parent deck IDs (~10K rows)
        await userContext.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE _parent_ids ON COMMIT DROP AS
            SELECT "DeckId" FROM "jiten"."Decks" WHERE "ParentDeckId" IS NULL;
            """);
        await userContext.Database.ExecuteSqlRawAsync("""CREATE INDEX ON _parent_ids ("DeckId");""");
        await userContext.Database.ExecuteSqlRawAsync("ANALYZE _parent_ids;");
        logger.LogInformation("Coverage: parent_ids materialized in {Elapsed}ms", sw.ElapsedMilliseconds);
        sw.Restart();

        // Step 3b: Mature hits — drive from _parent_ids (12K) into DeckWords via DeckId covering
        // index, then memoized probe against _mature_known (755K). Disable seqscan to prevent the
        // planner from choosing a full 187M-row sequential scan of DeckWords.
        // join_collapse_limit = 1 forces the written FROM order: drive from _parent_ids (13K)
        // through the DeckId covering index, then hash-join _mature_known last. Without this
        // the planner flips to driving from _mature_known via (WordId,ReadingIndex) once that
        // set grows, producing 80M random heap reads (~330s).
        await userContext.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off;");
        await userContext.Database.ExecuteSqlRawAsync("SET LOCAL join_collapse_limit = 1;");
        await userContext.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE _mature_hits ON COMMIT DROP AS
            SELECT dw."DeckId", SUM(dw."Occurrences") AS occ_hits, COUNT(*) AS uniq_hits
            FROM _parent_ids pd
            JOIN "jiten"."DeckWords" dw ON dw."DeckId" = pd."DeckId"
            JOIN _mature_known mk ON mk."WordId" = dw."WordId" AND mk."ReadingIndex" = dw."ReadingIndex"
            GROUP BY dw."DeckId";
            """);
        logger.LogInformation("Coverage: mature_hits computed in {Elapsed}ms", sw.ElapsedMilliseconds);
        sw.Restart();

        // Step 3c: Young hits — same _parent_ids → DeckId covering index path as mature.
        // Driving from _fsrs_young (~1K rows) via (WordId,ReadingIndex) would be faster in theory,
        // but requires a fully-vacuumed visibility map for Index Only Scan. With stale visibility
        // the heap fetches are random I/O across the entire table (29M reads). The DeckId path
        // has semi-sequential I/O patterns that tolerate stale visibility much better.
        await userContext.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE _young_hits ON COMMIT DROP AS
            SELECT dw."DeckId", SUM(dw."Occurrences") AS occ_hits, COUNT(*) AS uniq_hits
            FROM _parent_ids pd
            JOIN "jiten"."DeckWords" dw ON dw."DeckId" = pd."DeckId"
            JOIN _fsrs_young yk ON yk."WordId" = dw."WordId" AND yk."ReadingIndex" = dw."ReadingIndex"
            GROUP BY dw."DeckId";
            """);
        await userContext.Database.ExecuteSqlRawAsync("SET LOCAL join_collapse_limit = 8;");
        await userContext.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = on;");
        logger.LogInformation("Coverage: young_hits computed in {Elapsed}ms", sw.ElapsedMilliseconds);
        sw.Restart();

        // Step 4: Build per-deck coverage (parent decks only - child slots remain 0 to signal uncomputed)
        await userContext.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE _deck_cov ON COMMIT DROP AS
            SELECT d."DeckId",
                   CASE WHEN d."WordCount" = 0 THEN 0::smallint
                        ELSE LEAST(ROUND(COALESCE(mh.occ_hits, 0)::numeric * 10000 / d."WordCount")::int, 10000)::smallint
                   END AS m_cov,
                   CASE WHEN d."UniqueWordCount" = 0 THEN 0::smallint
                        ELSE LEAST(ROUND(COALESCE(mh.uniq_hits, 0)::numeric * 10000 / d."UniqueWordCount")::int, 10000)::smallint
                   END AS m_ucov,
                   CASE WHEN d."WordCount" = 0 THEN 0::smallint
                        ELSE LEAST(ROUND(COALESCE(yh.occ_hits, 0)::numeric * 10000 / d."WordCount")::int, 10000)::smallint
                   END AS y_cov,
                   CASE WHEN d."UniqueWordCount" = 0 THEN 0::smallint
                        ELSE LEAST(ROUND(COALESCE(yh.uniq_hits, 0)::numeric * 10000 / d."UniqueWordCount")::int, 10000)::smallint
                   END AS y_ucov
            FROM "jiten"."Decks" d
            LEFT JOIN _mature_hits mh ON mh."DeckId" = d."DeckId"
            LEFT JOIN _young_hits yh ON yh."DeckId" = d."DeckId"
            WHERE d."ParentDeckId" IS NULL;
            """);
        await userContext.Database.ExecuteSqlRawAsync("""CREATE INDEX ON _deck_cov ("DeckId");""");
        logger.LogInformation("Coverage: deck_cov built in {Elapsed}ms", sw.ElapsedMilliseconds);
        sw.Restart();

        // Step 5: Insert coverage chunks per metric
        var metrics = new (short id, string col)[]
        {
            ((short)UserCoverageMetric.MatureCoverage, "m_cov"),
            ((short)UserCoverageMetric.MatureUniqueCoverage, "m_ucov"),
            ((short)UserCoverageMetric.YoungCoverage, "y_cov"),
            ((short)UserCoverageMetric.YoungUniqueCoverage, "y_ucov"),
        };

        foreach (var (metricId, colName) in metrics)
        {
            var insertSql = $$"""
                INSERT INTO "user"."UserCoverageChunks" ("UserId", "Metric", "ChunkIndex", "Values", "ComputedAt")
                WITH
                deck_bounds AS (
                    SELECT COALESCE(MAX(d."DeckId"), 0) AS max_deck_id FROM "jiten"."Decks" d
                ),
                all_ids AS (
                    SELECT generate_series(
                        0,
                        ((SELECT max_deck_id FROM deck_bounds) / {{COVERAGE_CHUNK_SIZE}} + 1) * {{COVERAGE_CHUNK_SIZE}} - 1
                    )::int AS deck_id
                )
                SELECT
                    {0}::uuid,
                    {{metricId}}::smallint,
                    (ai.deck_id / {{COVERAGE_CHUNK_SIZE}})::int,
                    array_agg(COALESCE(dc."{{colName}}", 0::smallint) ORDER BY ai.deck_id),
                    {1}::timestamptz
                FROM all_ids ai
                LEFT JOIN _deck_cov dc ON dc."DeckId" = ai.deck_id
                GROUP BY (ai.deck_id / {{COVERAGE_CHUNK_SIZE}})::int
                ORDER BY (ai.deck_id / {{COVERAGE_CHUNK_SIZE}})::int;
                """;

            await userContext.Database.ExecuteSqlRawAsync(insertSql, userGuid, computedAt);
        }
        logger.LogInformation("Coverage: chunks inserted in {Elapsed}ms", sw.ElapsedMilliseconds);
    }

    private static async Task<DeckCoverageBasisPoints> ComputeUserDeckCoverageBasisPoints(UserDbContext userContext, string userId, int deckId)
    {
        var userGuid = Guid.Parse(userId);

        var sql = """
            WITH
        """ + CoverageWordStateCtes + """
            ,
            deck_denoms AS (
                SELECT d."WordCount", d."UniqueWordCount"
                FROM "jiten"."Decks" d
                WHERE d."DeckId" = {1}
            ),
            mature_hits AS (
                SELECT SUM(dw."Occurrences") AS occ_hits,
                       COUNT(*) AS uniq_hits
                FROM "jiten"."DeckWords" dw
                JOIN mature_known mk
                  ON mk."WordId" = dw."WordId"
                 AND mk."ReadingIndex" = dw."ReadingIndex"
                WHERE dw."DeckId" = {1}
            ),
            young_hits AS (
                SELECT SUM(dw."Occurrences") AS occ_hits,
                       COUNT(*) AS uniq_hits
                FROM "jiten"."DeckWords" dw
                JOIN fsrs_young yk
                  ON yk."WordId" = dw."WordId"
                 AND yk."ReadingIndex" = dw."ReadingIndex"
                WHERE dw."DeckId" = {1}
            )
            SELECT
                CASE WHEN dd."WordCount" = 0 THEN 0
                     ELSE LEAST(ROUND(COALESCE(mh.occ_hits, 0)::numeric * 10000 / dd."WordCount")::int, 10000)::smallint
                END AS "MatureCoverageBp",
                CASE WHEN dd."UniqueWordCount" = 0 THEN 0
                     ELSE LEAST(ROUND(COALESCE(mh.uniq_hits, 0)::numeric * 10000 / dd."UniqueWordCount")::int, 10000)::smallint
                END AS "MatureUniqueCoverageBp",
                CASE WHEN dd."WordCount" = 0 THEN 0
                     ELSE LEAST(ROUND(COALESCE(yh.occ_hits, 0)::numeric * 10000 / dd."WordCount")::int, 10000)::smallint
                END AS "YoungCoverageBp",
                CASE WHEN dd."UniqueWordCount" = 0 THEN 0
                     ELSE LEAST(ROUND(COALESCE(yh.uniq_hits, 0)::numeric * 10000 / dd."UniqueWordCount")::int, 10000)::smallint
                END AS "YoungUniqueCoverageBp"
            FROM deck_denoms dd
            LEFT JOIN mature_hits mh ON TRUE
            LEFT JOIN young_hits yh ON TRUE
            """;

        return await userContext.Database.SqlQueryRaw<DeckCoverageBasisPoints>(sql, userGuid, deckId).SingleAsync();
    }

    private static async Task UpsertUserDeckCoverageChunks(
        UserDbContext userContext,
        string userId,
        int deckId,
        DeckCoverageBasisPoints bp,
        DateTime computedAt)
    {
        var userGuid = Guid.Parse(userId);
        var chunkIndex = deckId / COVERAGE_CHUNK_SIZE;
        var slot = (deckId % COVERAGE_CHUNK_SIZE) + 1; // 1-based for PostgreSQL array subscripts

        await using var transaction = await userContext.Database.BeginTransactionAsync();

        var ensureSql = $$"""
            INSERT INTO "user"."UserCoverageChunks" ("UserId", "Metric", "ChunkIndex", "Values", "ComputedAt")
            VALUES
                ({0}::uuid, {{(short)UserCoverageMetric.MatureCoverage}}::smallint, {1}::int, array_fill(0::smallint, ARRAY[{{COVERAGE_CHUNK_SIZE}}]), {2}::timestamptz),
                ({0}::uuid, {{(short)UserCoverageMetric.MatureUniqueCoverage}}::smallint, {1}::int, array_fill(0::smallint, ARRAY[{{COVERAGE_CHUNK_SIZE}}]), {2}::timestamptz),
                ({0}::uuid, {{(short)UserCoverageMetric.YoungCoverage}}::smallint, {1}::int, array_fill(0::smallint, ARRAY[{{COVERAGE_CHUNK_SIZE}}]), {2}::timestamptz),
                ({0}::uuid, {{(short)UserCoverageMetric.YoungUniqueCoverage}}::smallint, {1}::int, array_fill(0::smallint, ARRAY[{{COVERAGE_CHUNK_SIZE}}]), {2}::timestamptz)
            ON CONFLICT ("UserId", "Metric", "ChunkIndex") DO NOTHING;
            """;

        await userContext.Database.ExecuteSqlRawAsync(ensureSql, userGuid, chunkIndex, computedAt);

        var updateSql = $$"""
            UPDATE "user"."UserCoverageChunks"
            SET "Values"[{{slot}}] = {3}::smallint,
                "ComputedAt" = {2}::timestamptz
            WHERE "UserId" = {0}::uuid
              AND "ChunkIndex" = {1}::int
              AND "Metric" = {4}::smallint;
            """;

        await userContext.Database.ExecuteSqlRawAsync(updateSql, userGuid, chunkIndex, computedAt, bp.MatureCoverageBp,
            (short)UserCoverageMetric.MatureCoverage);
        await userContext.Database.ExecuteSqlRawAsync(updateSql, userGuid, chunkIndex, computedAt, bp.MatureUniqueCoverageBp,
            (short)UserCoverageMetric.MatureUniqueCoverage);
        await userContext.Database.ExecuteSqlRawAsync(updateSql, userGuid, chunkIndex, computedAt, bp.YoungCoverageBp,
            (short)UserCoverageMetric.YoungCoverage);
        await userContext.Database.ExecuteSqlRawAsync(updateSql, userGuid, chunkIndex, computedAt, bp.YoungUniqueCoverageBp,
            (short)UserCoverageMetric.YoungUniqueCoverage);

        await transaction.CommitAsync();
    }

    private static readonly object KanjiGridComputeLock = new();
    private static readonly HashSet<string> KanjiGridComputingUserIds = new();

    [Queue("coverage")]
    public async Task ComputeUserKanjiGrid(string userId)
    {
        lock (KanjiGridComputeLock)
        {
            if (!KanjiGridComputingUserIds.Add(userId))
            {
                return;
            }
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            await using var userContext = await userContextFactory.CreateDbContextAsync();

            var youngWeight = double.Parse(configuration["KanjiGrid:YoungScoreWeight"] ?? "0.5", CultureInfo.InvariantCulture);
            var matureWeight = double.Parse(configuration["KanjiGrid:MatureScoreWeight"] ?? "1.0", CultureInfo.InvariantCulture);
            var masteredWeight = double.Parse(configuration["KanjiGrid:MasteredScoreWeight"] ?? "1.0", CultureInfo.InvariantCulture);

            var youngStr = youngWeight.ToString(CultureInfo.InvariantCulture);
            var matureStr = matureWeight.ToString(CultureInfo.InvariantCulture);
            var masteredStr = masteredWeight.ToString(CultureInfo.InvariantCulture);

            var sql = $$"""
                WITH user_known_words AS (
                    SELECT
                        fc."WordId",
                        fc."ReadingIndex",
                        CASE
                            WHEN fc."State" = 5 THEN {{masteredStr}}
                            WHEN fc."LastReview" IS NOT NULL
                                 AND (fc."Due" - fc."LastReview") >= INTERVAL '21 days'
                            THEN {{matureStr}}
                            WHEN fc."LastReview" IS NOT NULL
                            THEN {{youngStr}}
                            ELSE 0
                        END AS weight
                    FROM "user"."FsrsCards" fc
                    WHERE fc."UserId" = {0}::uuid
                      AND fc."State" NOT IN (0, 4)

                    UNION ALL

                    SELECT
                        wsm."WordId",
                        wsm."ReadingIndex",
                        {{masteredStr}} AS weight
                    FROM "user"."UserWordSetStates" uwss
                    JOIN "jiten"."WordSetMembers" wsm ON wsm."SetId" = uwss."SetId"
                    WHERE uwss."UserId" = {0}::uuid
                      AND uwss."State" = 2
                      AND NOT EXISTS (
                          SELECT 1 FROM "user"."FsrsCards" fc
                          WHERE fc."UserId" = {0}::uuid
                            AND fc."WordId" = wsm."WordId"
                            AND fc."ReadingIndex" = wsm."ReadingIndex"
                      )
                    GROUP BY wsm."WordId", wsm."ReadingIndex"
                ),
                user_kanji AS (
                    SELECT DISTINCT krw."KanjiCharacter"
                    FROM user_known_words ukw
                    JOIN jmdict."KanjiReadingWords" krw ON krw."WordId" = ukw."WordId"
                    WHERE ukw.weight > 0
                ),
                kanji_reading_raw AS (
                    SELECT krw."KanjiCharacter", krw."Reading",
                           COUNT(*) as total_words,
                           SUM(1.0 / sqrt(COALESCE(NULLIF(wff."FrequencyRank", 0), 200000))
                               * CASE WHEN COALESCE(NULLIF(wff."FrequencyRank", 0), 200000) <= 100000 THEN 1.0
                                      ELSE power(100000.0 / COALESCE(NULLIF(wff."FrequencyRank", 0), 200000), 2)
                                 END) as freq_score
                    FROM jmdict."KanjiReadingWords" krw
                    LEFT JOIN jmdict."WordFormFrequencies" wff
                        ON wff."WordId" = krw."WordId" AND wff."ReadingIndex" = krw."ReadingIndex"
                    WHERE krw."KanjiCharacter" IN (SELECT "KanjiCharacter" FROM user_kanji)
                    GROUP BY krw."KanjiCharacter", krw."Reading"
                ),
                kanji_reading_weighted AS (
                    SELECT "KanjiCharacter", "Reading", total_words,
                           freq_score / SUM(freq_score) OVER (PARTITION BY "KanjiCharacter") as raw_weight
                    FROM kanji_reading_raw
                ),
                kanji_reading_stats AS (
                    SELECT "KanjiCharacter", "Reading", total_words,
                           raw_weight / SUM(raw_weight) OVER (PARTITION BY "KanjiCharacter") as freq_weight
                    FROM kanji_reading_weighted
                    WHERE raw_weight >= 0.03
                ),
                user_known_per_reading AS (
                    SELECT krw."KanjiCharacter", krw."Reading",
                           COUNT(DISTINCT ukw."WordId") as known_count
                    FROM user_known_words ukw
                    JOIN jmdict."KanjiReadingWords" krw ON krw."WordId" = ukw."WordId"
                    WHERE ukw.weight > 0
                    GROUP BY krw."KanjiCharacter", krw."Reading"
                ),
                all_reading_scores AS (
                    SELECT krs."KanjiCharacter", krs."Reading",
                           krs.freq_weight, krs.total_words,
                           COALESCE(ukr.known_count, 0) as known_count,
                           LEAST(1.0, COALESCE(ukr.known_count, 0)::float
                               / LEAST(5, CEIL(0.3 * krs.total_words))) as reading_score
                    FROM kanji_reading_stats krs
                    LEFT JOIN user_known_per_reading ukr
                        ON ukr."KanjiCharacter" = krs."KanjiCharacter"
                       AND ukr."Reading" = krs."Reading"
                )
                SELECT "KanjiCharacter",
                       SUM(reading_score * freq_weight) as "Score",
                       SUM(known_count)::int as "WordCount",
                       json_agg(json_build_object(
                           'r', "Reading", 'k', known_count,
                           'q', LEAST(5, CEIL(0.3 * total_words))::int,
                           'w', ROUND(freq_weight::numeric, 3)
                       ) ORDER BY freq_weight DESC) as "ReadingsJson"
                FROM all_reading_scores
                GROUP BY "KanjiCharacter"
            """;

            var kanjiScores = await context.Database
                .SqlQueryRaw<KanjiScoreResult>(sql, userId)
                .ToListAsync();

            var scoresDict = kanjiScores.ToDictionary(
                ks => ks.KanjiCharacter,
                ks =>
                {
                    var entry = new KanjiScoreEntry
                    {
                        Score = Math.Round(ks.Score, 4),
                        WordCount = ks.WordCount
                    };
                    if (!string.IsNullOrEmpty(ks.ReadingsJson))
                    {
                        entry.Readings = System.Text.Json.JsonSerializer
                            .Deserialize<List<ReadingEntry>>(ks.ReadingsJson);
                    }
                    return entry;
                }
            );

            var existingGrid = await userContext.UserKanjiGrids
                .SingleOrDefaultAsync(ukg => ukg.UserId == userId);

            if (existingGrid is null)
            {
                existingGrid = new UserKanjiGrid
                {
                    UserId = userId,
                    KanjiScores = scoresDict,
                    LastComputedAt = DateTimeOffset.UtcNow
                };
                await userContext.UserKanjiGrids.AddAsync(existingGrid);
            }
            else
            {
                existingGrid.KanjiScores = scoresDict;
                existingGrid.LastComputedAt = DateTimeOffset.UtcNow;
            }

            await userContext.SaveChangesAsync();
        }
        finally
        {
            lock (KanjiGridComputeLock)
            {
                KanjiGridComputingUserIds.Remove(userId);
            }
        }
    }

    private class KanjiScoreResult
    {
        public string KanjiCharacter { get; set; } = string.Empty;
        public double Score { get; set; }
        public int WordCount { get; set; }
        public string? ReadingsJson { get; set; }
    }

    private static readonly object AccomplishmentComputeLock = new();
    private static readonly HashSet<string> AccomplishmentComputingUserIds = new();
    private const int GLOBAL_MEDIA_TYPE_KEY = -1;

    [Queue("coverage")]
    public async Task ComputeUserAccomplishments(string userId)
    {
        lock (AccomplishmentComputeLock)
        {
            if (!AccomplishmentComputingUserIds.Add(userId))
            {
                return;
            }
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            await using var userContext = await userContextFactory.CreateDbContextAsync();

            var completedDeckIds = await userContext.UserDeckPreferences
                                                    .Where(udp => udp.UserId == userId && udp.Status == DeckStatus.Completed)
                                                    .Select(udp => udp.DeckId)
                                                    .ToListAsync();

            if (completedDeckIds.Count == 0)
            {
                await userContext.UserAccomplishments
                                 .Where(ua => ua.UserId == userId)
                                 .ExecuteDeleteAsync();
                return;
            }

            // Load all completed decks (both parents and children)
            var allCompletedDecks = await context.Decks
                                                 .AsNoTracking()
                                                 .Where(d => completedDeckIds.Contains(d.DeckId))
                                                 .Select(d => new { d.DeckId, d.ParentDeckId, d.MediaType, d.CharacterCount, d.WordCount })
                                                 .ToListAsync();

            // Build effective deck set: include parents, and children only if their parent is NOT completed
            var completedParentIds = allCompletedDecks
                                     .Where(d => d.ParentDeckId == null)
                                     .Select(d => d.DeckId)
                                     .ToHashSet();

            var completedDecks = allCompletedDecks
                                 .Where(d => d.ParentDeckId == null || !completedParentIds.Contains(d.ParentDeckId.Value))
                                 .ToList();

            // Clear accomplishments if no effective decks remain
            if (completedDecks.Count == 0)
            {
                // Delete existing accomplishments
                await userContext.UserAccomplishments
                                 .Where(ua => ua.UserId == userId)
                                 .ExecuteDeleteAsync();
                return;
            }

            var usedDeckIds = completedDecks.Select(d => d.DeckId).ToList();
            var usedMediaTypes = completedDecks.Select(d => d.MediaType).Distinct().ToList();

            var uniqueWordCounts = await ComputeUniqueWordCounts(context, usedDeckIds, usedMediaTypes);
            var uniqueWordUsedOnceCounts = await ComputeUniqueWordUsedOnceCounts(context, usedDeckIds, usedMediaTypes);
            var uniqueKanjiCounts = await ComputeUniqueKanjiCounts(context, usedDeckIds, usedMediaTypes);

            var accomplishments = new List<UserAccomplishment>();
            var now = DateTimeOffset.UtcNow;

            // Global
            accomplishments.Add(new UserAccomplishment
                                {
                                    UserId = userId, MediaType = null, CompletedDeckCount = completedDecks.Count,
                                    TotalCharacterCount = completedDecks.Sum(d => (long)d.CharacterCount),
                                    TotalWordCount = completedDecks.Sum(d => (long)d.WordCount),
                                    UniqueWordCount = uniqueWordCounts.GetValueOrDefault(GLOBAL_MEDIA_TYPE_KEY, 0),
                                    UniqueWordUsedOnceCount = uniqueWordUsedOnceCounts.GetValueOrDefault(GLOBAL_MEDIA_TYPE_KEY, 0),
                                    UniqueKanjiCount = uniqueKanjiCounts.GetValueOrDefault(GLOBAL_MEDIA_TYPE_KEY, 0), LastComputedAt = now
                                });

            // By media type
            foreach (var mediaType in usedMediaTypes)
            {
                var typeDecks = completedDecks.Where(d => d.MediaType == mediaType).ToList();
                accomplishments.Add(new UserAccomplishment
                                    {
                                        UserId = userId, MediaType = mediaType, CompletedDeckCount = typeDecks.Count,
                                        TotalCharacterCount = typeDecks.Sum(d => (long)d.CharacterCount),
                                        TotalWordCount = typeDecks.Sum(d => (long)d.WordCount),
                                        UniqueWordCount = uniqueWordCounts.GetValueOrDefault((int)mediaType, 0),
                                        UniqueWordUsedOnceCount = uniqueWordUsedOnceCounts.GetValueOrDefault((int)mediaType, 0),
                                        UniqueKanjiCount = uniqueKanjiCounts.GetValueOrDefault((int)mediaType, 0), LastComputedAt = now
                                    });
            }

            // Delete existing accomplishments and insert new ones
            await userContext.UserAccomplishments
                             .Where(ua => ua.UserId == userId)
                             .ExecuteDeleteAsync();

            await userContext.UserAccomplishments.AddRangeAsync(accomplishments);
            await userContext.SaveChangesAsync();
        }
        finally
        {
            lock (AccomplishmentComputeLock)
            {
                AccomplishmentComputingUserIds.Remove(userId);
            }
        }
    }

    private async Task<Dictionary<int, int>> ComputeUniqueWordCounts(
        JitenDbContext context,
        List<int> deckIds,
        List<MediaType> mediaTypes)
    {
        var result = new Dictionary<int, int>();
        if (deckIds.Count == 0) return result;

        var deckIdsParam = string.Join(",", deckIds);

        // Global unique word count
        var globalSql = $"""
                             SELECT COUNT(DISTINCT ("WordId", "ReadingIndex"))::int AS "Value"
                             FROM jiten."DeckWords"
                             WHERE "DeckId" IN ({deckIdsParam})
                         """;
        var globalCount = await context.Database.SqlQueryRaw<int>(globalSql).FirstOrDefaultAsync();
        result[GLOBAL_MEDIA_TYPE_KEY] = globalCount;

        // Per-MediaType unique word counts
        foreach (var mediaType in mediaTypes)
        {
            var mediaTypeDecks = await context.Decks
                                              .AsNoTracking()
                                              .Where(d => deckIds.Contains(d.DeckId) && d.MediaType == mediaType)
                                              .Select(d => d.DeckId)
                                              .ToListAsync();

            if (mediaTypeDecks.Count == 0)
            {
                result[(int)mediaType] = 0;
                continue;
            }

            var mediaTypeDeckIdsParam = string.Join(",", mediaTypeDecks);
            var sql = $"""
                           SELECT COUNT(DISTINCT ("WordId", "ReadingIndex"))::int AS "Value"
                           FROM jiten."DeckWords"
                           WHERE "DeckId" IN ({mediaTypeDeckIdsParam})
                       """;
            var count = await context.Database.SqlQueryRaw<int>(sql).FirstOrDefaultAsync();
            result[(int)mediaType] = count;
        }

        return result;
    }

    private async Task<Dictionary<int, int>> ComputeUniqueKanjiCounts(
        JitenDbContext context,
        List<int> deckIds,
        List<MediaType> mediaTypes)
    {
        var result = new Dictionary<int, int>();
        if (deckIds.Count == 0) return result;

        // Get deck info including children for parent decks
        var decksWithChildren = await context.Decks
                                             .AsNoTracking()
                                             .Where(d => deckIds.Contains(d.DeckId))
                                             .Select(d => new
                                              {
                                                  d.DeckId,
                                                  d.MediaType,
                                                  ChildIds = d.Children.Select(c => c.DeckId).ToList()
                                              })
                                             .ToListAsync();

        var deckMediaTypes = new Dictionary<int, MediaType>();
        var rawTextDeckIds = new HashSet<int>();

        foreach (var deck in decksWithChildren)
        {
            if (deck.ChildIds.Count > 0)
            {
                // For parents, we need to fetch the child raw texts
                foreach (var childId in deck.ChildIds)
                {
                    rawTextDeckIds.Add(childId);
                    deckMediaTypes[childId] = deck.MediaType;
                }
            }
            else
            {
                rawTextDeckIds.Add(deck.DeckId);
                deckMediaTypes[deck.DeckId] = deck.MediaType;
            }
        }

        var rawTexts = await context.DeckRawTexts
                                    .AsNoTracking()
                                    .Where(rt => rawTextDeckIds.Contains(rt.DeckId))
                                    .Select(rt => new { rt.DeckId, rt.RawText })
                                    .ToListAsync();

        // Global kanji set
        var globalKanji = new HashSet<Rune>();

        // Per-MediaType kanji sets
        var mediaTypeKanji = mediaTypes.ToDictionary(mt => mt, _ => new HashSet<Rune>());

        foreach (var rt in rawTexts)
        {
            if (string.IsNullOrEmpty(rt.RawText)) continue;

            var mediaType = deckMediaTypes.GetValueOrDefault(rt.DeckId);

            foreach (var rune in rt.RawText.EnumerateRunes())
            {
                if (!JapaneseTextHelper.IsKanji(rune)) continue;

                globalKanji.Add(rune);
                if (mediaTypeKanji.TryGetValue(mediaType, out var kanjiSet))
                {
                    kanjiSet.Add(rune);
                }
            }
        }

        result[GLOBAL_MEDIA_TYPE_KEY] = globalKanji.Count;
        foreach (var mediaType in mediaTypes)
        {
            result[(int)mediaType] = mediaTypeKanji[mediaType].Count;
        }

        return result;
    }

    private async Task<Dictionary<int, int>> ComputeUniqueWordUsedOnceCounts(
        JitenDbContext context,
        List<int> deckIds,
        List<MediaType> mediaTypes)
    {
        var result = new Dictionary<int, int>();
        if (deckIds.Count == 0) return result;

        var deckIdsParam = string.Join(",", deckIds);

        var globalSql = $"""
                             SELECT COUNT(*)::int AS "Value"
                             FROM (
                                 SELECT "WordId", "ReadingIndex"
                                 FROM jiten."DeckWords"
                                 WHERE "DeckId" IN ({deckIdsParam})
                                 GROUP BY "WordId", "ReadingIndex"
                                 HAVING SUM("Occurrences") = 1
                             ) AS uniq
                         """;
        var globalCount = await context.Database.SqlQueryRaw<int>(globalSql).FirstOrDefaultAsync();
        result[GLOBAL_MEDIA_TYPE_KEY] = globalCount;

        // Per-MediaType unique words used once
        foreach (var mediaType in mediaTypes)
        {
            var mediaTypeDecks = await context.Decks
                                              .AsNoTracking()
                                              .Where(d => deckIds.Contains(d.DeckId) && d.MediaType == mediaType)
                                              .Select(d => d.DeckId)
                                              .ToListAsync();

            if (mediaTypeDecks.Count == 0)
            {
                result[(int)mediaType] = 0;
                continue;
            }

            var mediaTypeDeckIdsParam = string.Join(",", mediaTypeDecks);
            var sql = $"""
                           SELECT COUNT(*)::int AS "Value"
                           FROM (
                               SELECT "WordId", "ReadingIndex"
                               FROM jiten."DeckWords"
                               WHERE "DeckId" IN ({mediaTypeDeckIdsParam})
                               GROUP BY "WordId", "ReadingIndex"
                               HAVING SUM("Occurrences") = 1
                           ) AS uniq
                       """;
            var count = await context.Database.SqlQueryRaw<int>(sql).FirstOrDefaultAsync();
            result[(int)mediaType] = count;
        }

        return result;
    }

    public async Task RecomputeFrequencies()
    {
        string path = Path.Join(configuration["StaticFilesPath"], "yomitan");
        Directory.CreateDirectory(path);

        Console.WriteLine("Computing global frequencies...");
        var (wordFrequencies, formFrequencies) = await JitenHelper.ComputeFrequencies(contextFactory, null);
        await JitenHelper.SaveFrequenciesToDatabase(contextFactory, wordFrequencies, formFrequencies);

        // Save frequencies to CSV
        await SaveFrequenciesToCsv(wordFrequencies, formFrequencies, Path.Join(path, "jiten_freq_global.csv"));

        // Generate Yomitan deck
        string index = YomitanHelper.GetIndexJson(null);
        var bytes = await YomitanHelper.GenerateYomitanFrequencyDeck(contextFactory, wordFrequencies, formFrequencies, null, index);
        var filePath = Path.Join(path, "jiten_freq_global.zip");
        string indexFilePath = Path.Join(path, "jiten_freq_global.json");
        await File.WriteAllBytesAsync(filePath, bytes);
        await File.WriteAllTextAsync(indexFilePath, index);

        foreach (var mediaType in Enum.GetValues<MediaType>())
        {
            Console.WriteLine($"Computing {mediaType} frequencies...");
            (wordFrequencies, formFrequencies) = await JitenHelper.ComputeFrequencies(contextFactory, mediaType);

            // Save frequencies to CSV
            await SaveFrequenciesToCsv(wordFrequencies, formFrequencies, Path.Join(path, $"jiten_freq_{mediaType.ToString()}.csv"));

            // Generate Yomitan deck
            index = YomitanHelper.GetIndexJson(mediaType);
            bytes = await YomitanHelper.GenerateYomitanFrequencyDeck(contextFactory, wordFrequencies, formFrequencies, mediaType, index);
            filePath = Path.Join(path, $"jiten_freq_{mediaType.ToString()}.zip");
            indexFilePath = Path.Join(path, $"jiten_freq_{mediaType.ToString()}.json");
            await File.WriteAllBytesAsync(filePath, bytes);
            await File.WriteAllTextAsync(indexFilePath, index);
        }
    }

    public async Task RecomputeKanjiFrequencies()
    {
        string path = Path.Join(configuration["StaticFilesPath"], "yomitan");
        Directory.CreateDirectory(path);

        Console.WriteLine("Computing kanji frequencies...");
        var kanjiFrequencies = await JitenHelper.ComputeKanjiFrequencies(contextFactory);

        // Save to CSV
        await SaveKanjiFrequenciesToCsv(kanjiFrequencies, Path.Join(path, "jiten_kanji_freq.csv"));

        // Generate Yomitan deck
        string index = YomitanHelper.GetKanjiIndexJson();
        var bytes = YomitanHelper.GenerateYomitanKanjiFrequencyDeck(kanjiFrequencies);
        var filePath = Path.Join(path, "jiten_kanji_freq.zip");
        string indexFilePath = Path.Join(path, "jiten_kanji_freq.json");
        await File.WriteAllBytesAsync(filePath, bytes);
        await File.WriteAllTextAsync(indexFilePath, index);

        Console.WriteLine("Updating kanji frequency ranks in database...");
        await using var jitenContext = await contextFactory.CreateDbContextAsync();

        var kanjiRanks = kanjiFrequencies.ToDictionary(f => f.kanji, f => f.rank);
        var kanjiChars = kanjiRanks.Keys.ToHashSet();

        var kanjisToUpdate = await jitenContext.Kanjis
            .Where(k => kanjiChars.Contains(k.Character))
            .ToListAsync();

        foreach (var kanji in kanjisToUpdate)
        {
            if (kanjiRanks.TryGetValue(kanji.Character, out int rank))
            {
                kanji.FrequencyRank = rank;
            }
        }

        await jitenContext.SaveChangesAsync();

        Console.WriteLine("Kanji frequency computation complete.");
    }

    private async Task SaveKanjiFrequenciesToCsv(List<(string kanji, int rank)> frequencies, string filePath)
    {
        using var stream = new MemoryStream();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

        var frequencyListCsv = frequencies.Select(f => new { Kanji = f.kanji, Rank = f.rank }).ToArray();

        await csv.WriteRecordsAsync(frequencyListCsv);
        await writer.FlushAsync();

        stream.Position = 0;
        await using var fileStream = new FileStream(filePath, FileMode.Create);
        await stream.CopyToAsync(fileStream);
    }

    private async Task SaveFrequenciesToCsv(List<JmDictWordFrequency> frequencies,
        List<JmDictWordFormFrequency> formFrequencies, string filePath)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var wordIds = frequencies.Select(f => f.WordId).ToList();
        var allForms = await context.WordForms.AsNoTracking()
            .Where(wf => wordIds.Contains(wf.WordId))
            .ToListAsync();
        var formsByWord = allForms.GroupBy(wf => wf.WordId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var formFreqLookup = formFrequencies
            .ToDictionary(ff => (ff.WordId, ff.ReadingIndex));

        List<(string word, string form, int rank)> frequencyList = new();
        var addedEntries = new HashSet<string>();

        foreach (var frequency in frequencies)
        {
            if (!formsByWord.TryGetValue(frequency.WordId, out var wordForms))
                continue;

            var kanaForms = wordForms.Where(wf => wf.FormType == JmDictFormType.KanaForm)
                .OrderBy(wf => wf.ReadingIndex).ToList();
            var kanjiForms = wordForms.Where(wf => wf.FormType == JmDictFormType.KanjiForm).ToList();

            string mainKanaReading = kanaForms.FirstOrDefault()?.Text ?? "";

            foreach (var kanaForm in kanaForms)
            {
                var formFreq = formFreqLookup.GetValueOrDefault((frequency.WordId, kanaForm.ReadingIndex));
                if (formFreq == null || formFreq.UsedInMediaAmount <= 0) continue;
                if (!addedEntries.Add(kanaForm.Text)) continue;

                frequencyList.Add((kanaForm.Text, kanaForm.Text, formFreq.FrequencyRank));
            }

            foreach (var kanjiForm in kanjiForms)
            {
                var formFreq = formFreqLookup.GetValueOrDefault((frequency.WordId, kanjiForm.ReadingIndex));
                if (formFreq == null || formFreq.UsedInMediaAmount <= 0) continue;

                frequencyList.Add((kanjiForm.Text, mainKanaReading, formFreq.FrequencyRank));
            }
        }

        frequencyList.Sort((a, b) => a.rank.CompareTo(b.rank));

        using var stream = new MemoryStream();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

        var frequencyListCsv = frequencyList.Select(f => new { Word = f.word, Form = f.form, Rank = f.rank }).ToArray();

        await csv.WriteRecordsAsync(frequencyListCsv);
        await writer.FlushAsync();

        stream.Position = 0;
        await using var fileStream = new FileStream(filePath, FileMode.Create);
        await stream.CopyToAsync(fileStream);
    }
}
