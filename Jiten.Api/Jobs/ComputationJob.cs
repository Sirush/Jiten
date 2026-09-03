using System.Text;
using CsvHelper;
using Hangfire;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Services;
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
    private const string COVERAGE_WORK_MEM = "256MB";
    private const int COVERAGE_PARALLEL_WORKERS = 4;

    [Queue(CoverageQueues.Incremental)]
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

        // Same single-worker queue as the user jobs, so the index is fresh before the first of them runs.
        backgroundJobs.Enqueue<ComputationJob>(job => job.RebuildWordParentDeckIndex());
        foreach (var userId in userIds)
        {
            backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserCoverage(userId));
        }
    }

    [AutomaticRetry(Attempts = 0)]
    [Queue(CoverageQueues.Full)]
    public async Task RebuildWordParentDeckIndex()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        context.Database.SetCommandTimeout(TimeSpan.FromMinutes(15));
        await WordParentDeckIndexService.RebuildAsync(context, logger);
    }

    [AutomaticRetry(Attempts = 0)]
    [Queue(CoverageQueues.Full)]
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

            // Queued duplicates coalesce here: a burst of enqueues each marks dirty, the first job
            // recomputes and clears the flag, and the rest land on a clean flag and exit. Every
            // enqueue site must mark CoverageDirty first (or target a user without coverage chunks).
            var alreadyClean = await userContext.UserMetadatas.AsNoTracking()
                                                .AnyAsync(um => um.UserId == userId && !um.CoverageDirty);
            if (alreadyClean && await userContext.UserCoverageChunks.AnyAsync(uc => uc.UserId == userId))
                return;

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
            // A first-time or catch-up rebuild scans all of DeckWords and needs more than the per-statement budget below.
            userContext.Database.SetCommandTimeout(TimeSpan.FromMinutes(15));
            var build = await WordParentDeckIndexService.EnsureFreshAsync(userContext, logger);
            userContext.Database.SetCommandTimeout(TimeSpan.FromMinutes(3));

            await using var transaction = await userContext.Database.BeginTransactionAsync();
            await userContext.Database.ExecuteSqlRawAsync($"SET LOCAL work_mem = '{COVERAGE_WORK_MEM}';");
            await userContext.UserCoverageChunks.Where(uc => uc.UserId == userId).ExecuteDeleteAsync();
            logger.LogInformation("Coverage: old chunks deleted in {Elapsed}ms", totalSw.ElapsedMilliseconds);
            await RecomputeUserCoverageChunks(userContext, userId, computedAt, build);
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
    [Queue(CoverageQueues.Incremental)]
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

    [Queue(CoverageQueues.Incremental)]
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

    [Queue(CoverageQueues.Incremental)]
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

        // A mark that landed mid-compute must survive the clear, or the job it enqueued would skip.
        if (!isDirty && metadata.CoverageDirty && metadata.CoverageDirtyAt > computedAt)
            return;

        metadata.CoverageDirty = isDirty;
        metadata.CoverageDirtyAt = isDirty ? computedAt : null;
    }

    private async Task RecomputeUserCoverageChunks(UserDbContext userContext, string userId, DateTime computedAt,
                                                   WordParentDeckIndexService.BuildState build)
    {
        var userGuid = Guid.Parse(userId);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var derivationCategoryIds = await CoverageComputeService.LoadDerivationCategoryIds(userContext, userId);

        await CoverageComputeService.CreateKnownWordsTempTablesAsync(userContext, userGuid, derivationCategoryIds);
        var matureCount = await userContext.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM _mature_known").SingleAsync();
        var youngCount = await userContext.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM _fsrs_young").SingleAsync();
        logger.LogInformation("Coverage: known forms materialized ({Mature} mature, {Young} young) in {Elapsed}ms",
                              matureCount, youngCount, sw.ElapsedMilliseconds);
        sw.Restart();

        // Parallel workers cannot read temp tables, and the reloption is what lets a table this small drive a
        // parallel plan; created inside the transaction so a failure rolls it back with everything else.
        var knownTable = $"\"jiten\".\"_coverage_known_{Guid.NewGuid():N}\"";
        await userContext.Database.ExecuteSqlRawAsync($"""
            CREATE UNLOGGED TABLE {knownTable} AS
            SELECT "WordId", "ReadingIndex", TRUE AS "Mature" FROM _mature_known
            UNION ALL
            SELECT "WordId", "ReadingIndex", FALSE FROM _fsrs_young;
            """);
        await userContext.Database.ExecuteSqlRawAsync($"ALTER TABLE {knownTable} SET (parallel_workers = {COVERAGE_PARALLEL_WORKERS});");
        await userContext.Database.ExecuteSqlRawAsync($"ANALYZE {knownTable};");
        // The known set has unique keys, so a Memoize node over the probes only churns its cache.
        await userContext.Database.ExecuteSqlRawAsync("SET LOCAL enable_memoize = off;");
        await userContext.Database.ExecuteSqlRawAsync($"SET LOCAL max_parallel_workers_per_gather = {COVERAGE_PARALLEL_WORKERS};");

        await userContext.Database.ExecuteSqlRawAsync($$"""
            CREATE TEMP TABLE _hits ON COMMIT DROP AS
            SELECT u.deck_id AS "DeckId",
                   SUM(u.occ) FILTER (WHERE k."Mature") AS m_occ,
                   COUNT(*) FILTER (WHERE k."Mature") AS m_uniq,
                   SUM(u.occ) FILTER (WHERE NOT k."Mature") AS y_occ,
                   COUNT(*) FILTER (WHERE NOT k."Mature") AS y_uniq
            FROM {{knownTable}} k
            JOIN "jiten"."WordParentDeckIndex" w ON w."WordId" = k."WordId" AND w."ReadingIndex" = k."ReadingIndex"
            CROSS JOIN LATERAL unnest(w."DeckIds", w."Occurrences") AS u(deck_id, occ)
            GROUP BY u.deck_id;
            """);
        logger.LogInformation("Coverage: hits computed from WordParentDeckIndex in {Elapsed}ms", sw.ElapsedMilliseconds);
        sw.Restart();

        // EnsureFreshAsync bounds this set, so the whole-deck fetch below stays small.
        await userContext.Database.ExecuteSqlRawAsync($"""
            CREATE TEMP TABLE _stale_parents ON COMMIT DROP AS
            {WordParentDeckIndexService.StaleParentsQuery};
            """, build.CoveredUntil, build.DeckIds);
        await userContext.Database.ExecuteSqlRawAsync("ANALYZE _stale_parents;");
        var staleCount = await userContext.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM _stale_parents").SingleAsync();
        if (staleCount > 0)
        {
            // Fetched by DeckId alone, before the known set enters the picture: joined in one query the
            // planner can start from the known forms and walk DeckWords through the word index instead.
            await userContext.Database.ExecuteSqlRawAsync("""
                CREATE TEMP TABLE _stale_words ON COMMIT DROP AS
                SELECT dw."DeckId", dw."WordId", dw."ReadingIndex", dw."Occurrences"
                FROM _stale_parents s
                JOIN "jiten"."DeckWords" dw ON dw."DeckId" = s."DeckId";
                """);
            await userContext.Database.ExecuteSqlRawAsync("ANALYZE _stale_words;");
            await userContext.Database.ExecuteSqlRawAsync("""DELETE FROM _hits h USING _stale_parents s WHERE s."DeckId" = h."DeckId";""");
            await userContext.Database.ExecuteSqlRawAsync($$"""
                INSERT INTO _hits ("DeckId", m_occ, m_uniq, y_occ, y_uniq)
                SELECT sw."DeckId",
                       SUM(sw."Occurrences") FILTER (WHERE k."Mature"),
                       COUNT(*) FILTER (WHERE k."Mature"),
                       SUM(sw."Occurrences") FILTER (WHERE NOT k."Mature"),
                       COUNT(*) FILTER (WHERE NOT k."Mature")
                FROM _stale_words sw
                JOIN {{knownTable}} k ON k."WordId" = sw."WordId" AND k."ReadingIndex" = sw."ReadingIndex"
                GROUP BY sw."DeckId";
                """);
        }
        await userContext.Database.ExecuteSqlRawAsync($"DROP TABLE {knownTable};");
        logger.LogInformation("Coverage: {Count} stale parents recomputed from DeckWords in {Elapsed}ms", staleCount, sw.ElapsedMilliseconds);
        sw.Restart();

        // Child slots stay 0 to signal uncomputed; only live parents get values.
        await userContext.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE _deck_cov ON COMMIT DROP AS
            SELECT d."DeckId",
                   CASE WHEN d."WordCount" = 0 THEN 0::smallint
                        ELSE LEAST(ROUND(COALESCE(h.m_occ, 0)::numeric * 10000 / d."WordCount")::int, 10000)::smallint
                   END AS m_cov,
                   CASE WHEN d."UniqueWordCount" = 0 THEN 0::smallint
                        ELSE LEAST(ROUND(COALESCE(h.m_uniq, 0)::numeric * 10000 / d."UniqueWordCount")::int, 10000)::smallint
                   END AS m_ucov,
                   CASE WHEN d."WordCount" = 0 THEN 0::smallint
                        ELSE LEAST(ROUND(COALESCE(h.y_occ, 0)::numeric * 10000 / d."WordCount")::int, 10000)::smallint
                   END AS y_cov,
                   CASE WHEN d."UniqueWordCount" = 0 THEN 0::smallint
                        ELSE LEAST(ROUND(COALESCE(h.y_uniq, 0)::numeric * 10000 / d."UniqueWordCount")::int, 10000)::smallint
                   END AS y_ucov
            FROM "jiten"."Decks" d
            LEFT JOIN _hits h ON h."DeckId" = d."DeckId"
            WHERE d."ParentDeckId" IS NULL;
            """);
        await userContext.Database.ExecuteSqlRawAsync("""CREATE INDEX ON _deck_cov ("DeckId");""");
        logger.LogInformation("Coverage: deck_cov built in {Elapsed}ms", sw.ElapsedMilliseconds);
        sw.Restart();

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

    private static readonly object KanjiGridComputeLock = new();
    private static readonly HashSet<string> KanjiGridComputingUserIds = new();

    [Queue(CoverageQueues.Incremental)]
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
                    JOIN jmdict."KanjiReadingWords" krw
                        ON krw."WordId" = ukw."WordId" AND krw."ReadingIndex" = ukw."ReadingIndex"
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
                    JOIN jmdict."KanjiReadingWords" krw
                        ON krw."WordId" = ukw."WordId" AND krw."ReadingIndex" = ukw."ReadingIndex"
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

    internal sealed record CompletedDeckInfo(int DeckId, int? ParentDeckId, MediaType MediaType, int CharacterCount, int WordCount);

    /// <summary>Effective set + leaf units per effective deck</summary>
    internal static (List<CompletedDeckInfo> EffectiveDecks, Dictionary<int, int> UnitCounts) ResolveCompletedUnits(
        IReadOnlyList<CompletedDeckInfo> allCompletedDecks,
        IReadOnlyDictionary<int, int> childCounts)
    {
        var completedRootIds = allCompletedDecks.Where(d => d.ParentDeckId == null).Select(d => d.DeckId).ToHashSet();

        var effectiveDecks = allCompletedDecks
                             .Where(d => d.ParentDeckId == null || !completedRootIds.Contains(d.ParentDeckId.Value))
                             .ToList();

        var unitCounts = effectiveDecks.ToDictionary(d => d.DeckId,
                                                     d => d.ParentDeckId == null
                                                         ? Math.Max(childCounts.GetValueOrDefault(d.DeckId, 0), 1)
                                                         : 1);

        return (effectiveDecks, unitCounts);
    }

    [Queue(CoverageQueues.Incremental)]
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
                                                 .Select(d => new CompletedDeckInfo(d.DeckId, d.ParentDeckId, d.MediaType, d.CharacterCount,
                                                                                    d.WordCount))
                                                 .ToListAsync();

            var completedRootIds = allCompletedDecks.Where(d => d.ParentDeckId == null).Select(d => d.DeckId).ToList();

            var childCounts = completedRootIds.Count == 0
                ? new Dictionary<int, int>()
                : await context.Decks
                               .AsNoTracking()
                               .Where(d => d.ParentDeckId != null && completedRootIds.Contains(d.ParentDeckId.Value))
                               .GroupBy(d => d.ParentDeckId!.Value)
                               .Select(g => new { ParentDeckId = g.Key, Count = g.Count() })
                               .ToDictionaryAsync(g => g.ParentDeckId, g => g.Count);

            var (completedDecks, unitCounts) = ResolveCompletedUnits(allCompletedDecks, childCounts);

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

            // CompletedDeckCount counts whole works (roots only); completed children of an unfinished parent feed units and totals but not the headline count.
            accomplishments.Add(new UserAccomplishment
                                {
                                    UserId = userId, MediaType = null, CompletedDeckCount = completedDecks.Count(d => d.ParentDeckId == null),
                                    CompletedUnitCount = completedDecks.Sum(d => unitCounts[d.DeckId]),
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
                                        UserId = userId, MediaType = mediaType, CompletedDeckCount = typeDecks.Count(d => d.ParentDeckId == null),
                                        CompletedUnitCount = typeDecks.Sum(d => unitCounts[d.DeckId]),
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

        Console.WriteLine("Loading deck word aggregates...");
        var batch = await JitenHelper.LoadFrequencyBatch(contextFactory);

        Console.WriteLine("Computing global frequencies...");
        var (wordFrequencies, formFrequencies) = batch.Compute(null);
        await JitenHelper.SaveFrequenciesToDatabase(contextFactory, wordFrequencies, formFrequencies);

        // Save frequencies to CSV
        await SaveFrequenciesToCsv(wordFrequencies, formFrequencies, Path.Join(path, "jiten_freq_global.csv"), batch.WordForms);

        // Generate Yomitan deck
        string index = YomitanHelper.GetIndexJson(null);
        var bytes = await YomitanHelper.GenerateYomitanFrequencyDeck(contextFactory, wordFrequencies, formFrequencies, null, index, batch.WordForms);
        var filePath = Path.Join(path, "jiten_freq_global.zip");
        string indexFilePath = Path.Join(path, "jiten_freq_global.json");
        await File.WriteAllBytesAsync(filePath, bytes);
        await File.WriteAllTextAsync(indexFilePath, index);

        foreach (var mediaType in MediaTypes.Listed)
        {
            Console.WriteLine($"Computing {mediaType} frequencies...");
            (wordFrequencies, formFrequencies) = batch.Compute(mediaType);

            try
            {
                await JitenHelper.SaveFrequenciesByTypeToDatabase(contextFactory, mediaType, wordFrequencies, formFrequencies);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "RecomputeFrequencies: failed persisting {MediaType} frequencies", mediaType);
            }

            // Save frequencies to CSV
            await SaveFrequenciesToCsv(wordFrequencies, formFrequencies, Path.Join(path, $"jiten_freq_{mediaType.ToString()}.csv"), batch.WordForms);

            // Generate Yomitan deck
            index = YomitanHelper.GetIndexJson(mediaType);
            bytes = await YomitanHelper.GenerateYomitanFrequencyDeck(contextFactory, wordFrequencies, formFrequencies, mediaType, index, batch.WordForms);
            filePath = Path.Join(path, $"jiten_freq_{mediaType.ToString()}.zip");
            indexFilePath = Path.Join(path, $"jiten_freq_{mediaType.ToString()}.json");
            await File.WriteAllBytesAsync(filePath, bytes);
            await File.WriteAllTextAsync(indexFilePath, index);
        }

        backgroundJobs.Enqueue<FrequencyListJob>(job => job.RegenerateAutoUpdateLists());
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
        List<JmDictWordFormFrequency> formFrequencies, string filePath, List<JmDictWordForm>? preloadedForms = null)
    {
        var allForms = preloadedForms
                       ?? await YomitanHelper.LoadFormsForWordIds(contextFactory, frequencies.Select(f => f.WordId).ToList());
        var formsByWord = allForms.GroupBy(wf => wf.WordId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var frequencyList = YomitanHelper.BuildFrequencyCsvRows(frequencies, formFrequencies, formsByWord);

        using var stream = new MemoryStream();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

        var frequencyListCsv = frequencyList.Select(f => new { f.Word, f.Form, f.Rank }).ToArray();

        await csv.WriteRecordsAsync(frequencyListCsv);
        await writer.FlushAsync();

        stream.Position = 0;
        await using var fileStream = new FileStream(filePath, FileMode.Create);
        await stream.CopyToAsync(fileStream);
    }
}
