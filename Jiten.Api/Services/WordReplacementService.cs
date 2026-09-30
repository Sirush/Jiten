using Hangfire;
using Jiten.Api.Dtos;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Services;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Jiten.Api.Services;

public class WordReplacementService(
    IDbContextFactory<JitenDbContext> contextFactory,
    IDbContextFactory<UserDbContext> userContextFactory,
    IBackgroundJobClient backgroundJobs,
    IConnectionMultiplexer redis,
    ILogger<WordReplacementService> logger)
{
    private readonly IDatabase _redis = redis.GetDatabase();

    private async Task QueueParentDeckRecalcIfNeeded(int parentDeckId)
    {
        var key = $"jiten:parent-deck-recalc-pending:{parentDeckId}";
        var wasSet = await _redis.StringSetAsync(key, "1", TimeSpan.FromMinutes(120), When.NotExists);

        if (wasSet)
        {
            backgroundJobs.Enqueue<WordReplacementService>(s => s.RecalculateParentDeck(parentDeckId));
            logger.LogDebug("Queued recalculation for parent deck {DeckId}", parentDeckId);
        }
        else
        {
            logger.LogDebug("Skipping recalc queue for deck {DeckId} - job already pending", parentDeckId);
        }
    }

    private void QueueIncrementalParentUpdate(
        int parentDeckId,
        int oldWordId, byte oldReadingIndex,
        int newWordId, byte newReadingIndex,
        int occurrenceDelta)
    {
        backgroundJobs.Enqueue<WordReplacementService>(s =>
            s.IncrementalParentUpdate(parentDeckId, oldWordId, oldReadingIndex, newWordId, newReadingIndex, occurrenceDelta));

        logger.LogDebug("Queued incremental parent update for deck {DeckId}: {OldWord}:{OldReading} -> {NewWord}:{NewReading}, delta {Delta}",
            parentDeckId, oldWordId, oldReadingIndex, newWordId, newReadingIndex, occurrenceDelta);
    }

    private void QueueIncrementalParentRemove(
        int parentDeckId,
        int wordId, byte readingIndex,
        int occurrenceDelta)
    {
        backgroundJobs.Enqueue<WordReplacementService>(s =>
            s.IncrementalParentRemove(parentDeckId, wordId, readingIndex, occurrenceDelta));

        logger.LogDebug("Queued incremental parent remove for deck {DeckId}: word {WordId}:{ReadingIndex}, delta {Delta}",
            parentDeckId, wordId, readingIndex, occurrenceDelta);
    }

    private void QueueIncrementalParentAdd(
        int parentDeckId,
        int wordId, byte readingIndex,
        int occurrenceDelta)
    {
        backgroundJobs.Enqueue<WordReplacementService>(s =>
            s.IncrementalParentAdd(parentDeckId, wordId, readingIndex, occurrenceDelta));

        logger.LogDebug("Queued incremental parent add for deck {DeckId}: word {WordId}:{ReadingIndex}, delta {Delta}",
            parentDeckId, wordId, readingIndex, occurrenceDelta);
    }

    public async Task IncrementalParentUpdate(
        int parentDeckId,
        int oldWordId, byte oldReadingIndex,
        int newWordId, byte newReadingIndex,
        int occurrenceDelta)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        // Decrement old word occurrences
        await context.Database.ExecuteSqlRawAsync(@"
            UPDATE jiten.""DeckWords""
            SET ""Occurrences"" = ""Occurrences"" - {0}
            WHERE ""DeckId"" = {1} AND ""WordId"" = {2} AND ""ReadingIndex"" = {3}",
            occurrenceDelta, parentDeckId, oldWordId, oldReadingIndex);

        // Delete if occurrences dropped to zero or below
        await context.Database.ExecuteSqlRawAsync(@"
            DELETE FROM jiten.""DeckWords""
            WHERE ""DeckId"" = {0} AND ""WordId"" = {1} AND ""ReadingIndex"" = {2}
              AND ""Occurrences"" <= 0",
            parentDeckId, oldWordId, oldReadingIndex);

        // Upsert new word
        await context.Database.ExecuteSqlRawAsync(@"
            INSERT INTO jiten.""DeckWords"" (""DeckId"", ""WordId"", ""ReadingIndex"", ""Occurrences"")
            VALUES ({0}, {1}, {2}, {3})
            ON CONFLICT (""DeckId"", ""WordId"", ""ReadingIndex"")
            DO UPDATE SET ""Occurrences"" = jiten.""DeckWords"".""Occurrences"" + EXCLUDED.""Occurrences""",
            parentDeckId, newWordId, newReadingIndex, occurrenceDelta);

        await UpdateParentDeckStats(context, parentDeckId);

        logger.LogDebug("Incremental parent update completed: deck {DeckId}, {OldWord}:{OldReading} -> {NewWord}:{NewReading}, delta {Delta}",
            parentDeckId, oldWordId, oldReadingIndex, newWordId, newReadingIndex, occurrenceDelta);
    }

    public async Task IncrementalParentRemove(
        int parentDeckId,
        int wordId, byte readingIndex,
        int occurrenceDelta)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        // Decrement word occurrences
        await context.Database.ExecuteSqlRawAsync(@"
            UPDATE jiten.""DeckWords""
            SET ""Occurrences"" = ""Occurrences"" - {0}
            WHERE ""DeckId"" = {1} AND ""WordId"" = {2} AND ""ReadingIndex"" = {3}",
            occurrenceDelta, parentDeckId, wordId, readingIndex);

        // Delete if occurrences dropped to zero or below
        await context.Database.ExecuteSqlRawAsync(@"
            DELETE FROM jiten.""DeckWords""
            WHERE ""DeckId"" = {0} AND ""WordId"" = {1} AND ""ReadingIndex"" = {2}
              AND ""Occurrences"" <= 0",
            parentDeckId, wordId, readingIndex);

        await UpdateParentDeckStats(context, parentDeckId);

        logger.LogDebug("Incremental parent remove completed: deck {DeckId}, word {WordId}:{ReadingIndex}, delta {Delta}",
            parentDeckId, wordId, readingIndex, occurrenceDelta);
    }

    public async Task IncrementalParentAdd(
        int parentDeckId,
        int wordId, byte readingIndex,
        int occurrenceDelta)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        // Upsert word
        await context.Database.ExecuteSqlRawAsync(@"
            INSERT INTO jiten.""DeckWords"" (""DeckId"", ""WordId"", ""ReadingIndex"", ""Occurrences"")
            VALUES ({0}, {1}, {2}, {3})
            ON CONFLICT (""DeckId"", ""WordId"", ""ReadingIndex"")
            DO UPDATE SET ""Occurrences"" = jiten.""DeckWords"".""Occurrences"" + EXCLUDED.""Occurrences""",
            parentDeckId, wordId, readingIndex, occurrenceDelta);

        await UpdateParentDeckStats(context, parentDeckId);

        logger.LogDebug("Incremental parent add completed: deck {DeckId}, word {WordId}:{ReadingIndex}, delta {Delta}",
            parentDeckId, wordId, readingIndex, occurrenceDelta);
    }

    private async Task UpdateParentDeckStats(JitenDbContext context, int parentDeckId)
    {
        await context.Database.ExecuteSqlRawAsync(@"
            UPDATE jiten.""Decks"" d
            SET ""UniqueWordCount"" = (
                SELECT COUNT(DISTINCT (""WordId"", ""ReadingIndex""))
                FROM jiten.""DeckWords"" WHERE ""DeckId"" = d.""DeckId""
            ),
            ""UniqueWordUsedOnceCount"" = (
                SELECT COUNT(*)
                FROM jiten.""DeckWords"" WHERE ""DeckId"" = d.""DeckId"" AND ""Occurrences"" = 1
            )
            WHERE d.""DeckId"" = {0}",
            parentDeckId);
    }

    public async Task<WordReplacementResult> ReplaceAsync(
        int oldWordId, byte oldReadingIndex,
        int newWordId, byte newReadingIndex,
        bool dryRun = false)
    {
        var result = new WordReplacementResult { WasDryRun = dryRun };

        await using var context = await contextFactory.CreateDbContextAsync();
        await using var userContext = await userContextFactory.CreateDbContextAsync();

        if (dryRun)
        {
            return await ComputeDryRunCounts(context, userContext, oldWordId, oldReadingIndex, newWordId, newReadingIndex);
        }

        await using var transaction = await context.Database.BeginTransactionAsync();
        await using var userTransaction = await userContext.Database.BeginTransactionAsync();

        try
        {
            // Collect affected deck IDs before making changes
            var affectedDeckIds = await context.DeckWords
                .Where(dw => dw.WordId == oldWordId && dw.ReadingIndex == oldReadingIndex)
                .Select(dw => dw.DeckId)
                .Distinct()
                .ToListAsync();

            result.AffectedDeckCount = affectedDeckIds.Count;

            // Capture parent deck deltas BEFORE modifying children
            var parentDeltas = await context.DeckWords
                .Where(dw => dw.WordId == oldWordId && dw.ReadingIndex == oldReadingIndex)
                .Join(context.Decks.Where(d => d.ParentDeckId != null),
                      dw => dw.DeckId,
                      d => d.DeckId,
                      (dw, d) => new { d.ParentDeckId, dw.Occurrences })
                .GroupBy(x => x.ParentDeckId)
                .Select(g => new { ParentDeckId = g.Key!.Value, TotalOccurrences = g.Sum(x => x.Occurrences) })
                .ToListAsync();

            // Step 1: DeckWords - Merge occurrences where both old and new exist
            result.DeckWordsMerged = await context.Database.ExecuteSqlRawAsync(@"
                UPDATE jiten.""DeckWords"" correct
                SET ""Occurrences"" = correct.""Occurrences"" + wrong.""Occurrences""
                FROM jiten.""DeckWords"" wrong
                WHERE correct.""WordId"" = {0}
                  AND correct.""ReadingIndex"" = {1}
                  AND wrong.""WordId"" = {2}
                  AND wrong.""ReadingIndex"" = {3}
                  AND correct.""DeckId"" = wrong.""DeckId""",
                newWordId, newReadingIndex, oldWordId, oldReadingIndex);

            // Step 2: DeckWords - Delete merged old entries
            await context.Database.ExecuteSqlRawAsync(@"
                DELETE FROM jiten.""DeckWords"" wrong
                USING jiten.""DeckWords"" correct
                WHERE wrong.""WordId"" = {0}
                  AND wrong.""ReadingIndex"" = {1}
                  AND correct.""WordId"" = {2}
                  AND correct.""ReadingIndex"" = {3}
                  AND wrong.""DeckId"" = correct.""DeckId""",
                oldWordId, oldReadingIndex, newWordId, newReadingIndex);

            // Step 3: DeckWords - Update remaining (no conflict)
            result.DeckWordsUpdated = await context.Database.ExecuteSqlRawAsync(@"
                UPDATE jiten.""DeckWords""
                SET ""WordId"" = {0}, ""ReadingIndex"" = {1}
                WHERE ""WordId"" = {2} AND ""ReadingIndex"" = {3}",
                newWordId, newReadingIndex, oldWordId, oldReadingIndex);

            result.ExampleSentenceWordsUpdated = await RewriteSentenceTokens(
                context, oldWordId, oldReadingIndex, token => [token with { WordId = newWordId, ReadingIndex = newReadingIndex }]);
            await SentenceProfileService.RewriteWordAsync(context, affectedDeckIds, ExampleSentenceTokens.WordKey(oldWordId, oldReadingIndex),
                                                          [ExampleSentenceTokens.WordKey(newWordId, newReadingIndex)]);

            var cardUserIds = await userContext.FsrsCards
                .Where(c => c.WordId == oldWordId && c.ReadingIndex == oldReadingIndex)
                .Select(c => c.UserId)
                .Distinct()
                .ToListAsync();

            var affectedSetIds = await context.WordSetMembers
                .Where(m => m.WordId == oldWordId && m.ReadingIndex == oldReadingIndex)
                .Select(m => m.SetId)
                .Distinct()
                .ToListAsync();

            result.FsrsCardsSkipped = await ResolveCardCollisions(userContext, oldWordId, oldReadingIndex, newWordId, newReadingIndex);

            // Step 6: FsrsCards - Only update if user doesn't have the new reading already
            result.FsrsCardsUpdated = await userContext.Database.ExecuteSqlRawAsync(@"
                UPDATE ""user"".""FsrsCards"" old
                SET ""WordId"" = {0}, ""ReadingIndex"" = {1}
                WHERE old.""WordId"" = {2}
                  AND old.""ReadingIndex"" = {3}
                  AND NOT EXISTS (
                    SELECT 1 FROM ""user"".""FsrsCards"" existing
                    WHERE existing.""UserId"" = old.""UserId""
                      AND existing.""WordId"" = {0}
                      AND existing.""ReadingIndex"" = {1}
                  )",
                newWordId, newReadingIndex, oldWordId, oldReadingIndex);

            // Step 6c: archive rows need the same remap, or they rot into (WordId, ReadingIndex) pairs that fail
            // validation on every future restore.
            await userContext.Database.ExecuteSqlRawAsync(@"
                UPDATE ""user"".""FsrsCardArchives"" old
                SET ""WordId"" = {0}, ""ReadingIndex"" = {1}
                WHERE old.""WordId"" = {2}
                  AND old.""ReadingIndex"" = {3}
                  AND NOT EXISTS (
                    SELECT 1 FROM ""user"".""FsrsCardArchives"" existing
                    WHERE existing.""UserId"" = old.""UserId""
                      AND existing.""WordId"" = {0}
                      AND existing.""ReadingIndex"" = {1}
                  )",
                newWordId, newReadingIndex, oldWordId, oldReadingIndex);

            // Step 6d: users left with a row on both sides get them merged into one.
            var staleArchives = await userContext.FsrsCardArchives
                .Where(a => a.WordId == oldWordId && a.ReadingIndex == oldReadingIndex)
                .ToListAsync();

            if (staleArchives.Count > 0)
            {
                var archiveUserIds = staleArchives.Select(a => a.UserId).Distinct().ToList();
                var targets = await userContext.FsrsCardArchives
                    .Where(a => a.WordId == newWordId && a.ReadingIndex == newReadingIndex
                                && archiveUserIds.Contains(a.UserId))
                    .ToDictionaryAsync(a => a.UserId);

                foreach (var stale in staleArchives)
                    if (targets.TryGetValue(stale.UserId, out var target))
                        CardArchiveService.MergeArchiveRows(target, stale);

                userContext.FsrsCardArchives.RemoveRange(staleArchives);
                await userContext.SaveChangesAsync();
            }

            await RemapStudyDeckWords(userContext, oldWordId, oldReadingIndex, newWordId, newReadingIndex, result);
            await RemapWordSetMembers(context, oldWordId, oldReadingIndex, newWordId, newReadingIndex, result);
            await RemapUserAuthoredContent(userContext, oldWordId, oldReadingIndex, newWordId, newReadingIndex, result);

            // Step 7: Update UniqueWordCount on affected decks
            if (affectedDeckIds.Count > 0)
            {
                var deckIdsArray = affectedDeckIds.ToArray();
                await context.Database.ExecuteSqlAsync($@"
                    UPDATE jiten.""Decks"" d
                    SET ""UniqueWordCount"" = (
                        SELECT COUNT(DISTINCT (""WordId"", ""ReadingIndex""))
                        FROM jiten.""DeckWords"" WHERE ""DeckId"" = d.""DeckId""
                    ),
                    ""UniqueWordUsedOnceCount"" = (
                        SELECT COUNT(*)
                        FROM jiten.""DeckWords"" WHERE ""DeckId"" = d.""DeckId"" AND ""Occurrences"" = 1
                    )
                    WHERE d.""DeckId"" = ANY({deckIdsArray})");
            }

            await transaction.CommitAsync();
            await userTransaction.CommitAsync();

            var setUserIds = affectedSetIds.Count == 0
                ? []
                : await userContext.UserWordSetStates
                                   .Where(s => affectedSetIds.Contains(s.SetId))
                                   .Select(s => s.UserId)
                                   .Distinct()
                                   .ToListAsync();
            var dirtyUserIds = cardUserIds.Union(setUserIds).ToList();
            foreach (var userId in dirtyUserIds)
                await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
            result.CoverageUsersMarked = dirtyUserIds.Count;

            // Step 8: Queue incremental parent updates (outside transaction)
            foreach (var delta in parentDeltas)
            {
                QueueIncrementalParentUpdate(
                    delta.ParentDeckId,
                    oldWordId, oldReadingIndex,
                    newWordId, newReadingIndex,
                    delta.TotalOccurrences);
            }

            result.ParentDecksQueued = parentDeltas.Count;

            logger.LogInformation(
                "Word replacement completed: {OldWordId}:{OldReadingIndex} -> {NewWordId}:{NewReadingIndex}. " +
                "DeckWords: {Updated} updated, {Merged} merged. ExampleSentences: {ESUpdated}. " +
                "FsrsCards: {FsrsUpdated} updated, {FsrsSkipped} collisions. StudyDeckWords: {StudyDeckWords}. " +
                "WordSetMembers: {WordSetMembers}. CardMedia: {CardMedia}. UserSentences: {UserSentences}. " +
                "Coverage marked: {CoverageUsers}. Parent decks queued: {Parents}",
                oldWordId, oldReadingIndex, newWordId, newReadingIndex,
                result.DeckWordsUpdated, result.DeckWordsMerged, result.ExampleSentenceWordsUpdated,
                result.FsrsCardsUpdated, result.FsrsCardsSkipped, result.StudyDeckWordsUpdated,
                result.WordSetMembersUpdated, result.CardMediaMoved, result.UserSentencesMoved,
                result.CoverageUsersMarked, result.ParentDecksQueued);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            await userTransaction.RollbackAsync();
            logger.LogError(ex, "Word replacement failed: {OldWordId}:{OldReadingIndex} -> {NewWordId}:{NewReadingIndex}",
                oldWordId, oldReadingIndex, newWordId, newReadingIndex);
            throw;
        }

        return result;
    }

    /// <summary>Remaps every form JMdict moved to another entry; ambiguous moves and deleted entries are left to a reparse.</summary>
    public async Task<List<MovedFormMigrationRow>> MigrateMovedFormsAsync(bool dryRun)
    {
        List<MovedFormMigrator.DetectedMove> moves;
        await using (var context = await contextFactory.CreateDbContextAsync())
            moves = await MovedFormMigrator.DetectMoves(context);

        var rows = new List<MovedFormMigrationRow>();
        foreach (var m in moves)
        {
            var row = new MovedFormMigrationRow
            {
                Text = m.Text, OldWordId = m.OldWordId, OldReadingIndex = m.OldReadingIndex,
                NewWordId = m.NewWordId, NewReadingIndex = m.NewReadingIndex, OwnerCount = m.OwnerCount,
                Skipped = m.OldEntryDeleted ? "deleted" : m.Ambiguous ? "ambiguous" : null,
            };

            if (row.Skipped == null)
                row.Result = await ReplaceAsync(m.OldWordId, (byte)m.OldReadingIndex, m.NewWordId, (byte)m.NewReadingIndex, dryRun);

            rows.Add(row);
        }

        logger.LogInformation("Moved-form migration {Mode}: {Remapped} remapped, {Skipped} skipped",
                              dryRun ? "dry run" : "applied", rows.Count(r => r.Skipped == null), rows.Count(r => r.Skipped != null));
        return rows;
    }

    /// <summary>More reviews wins, then the more recent review; a full tie keeps the card already on the new pair.</summary>
    public static bool KeepsOldCard(FsrsCard oldCard, int oldReviews, FsrsCard newCard, int newReviews)
    {
        if (oldReviews != newReviews)
            return oldReviews > newReviews;

        return (oldCard.LastReview ?? DateTime.MinValue) > (newCard.LastReview ?? DateTime.MinValue);
    }

    /// <summary>For users holding cards on both pairs, archives and deletes the weaker card so the remap has no unique-key clash.</summary>
    private static async Task<int> ResolveCardCollisions(
        UserDbContext userContext,
        int oldWordId, byte oldReadingIndex,
        int newWordId, byte newReadingIndex)
    {
        var oldCards = await userContext.FsrsCards
            .Where(c => c.WordId == oldWordId && c.ReadingIndex == oldReadingIndex)
            .ToListAsync();
        if (oldCards.Count == 0)
            return 0;

        var userIds = oldCards.Select(c => c.UserId).ToList();
        var newCards = await userContext.FsrsCards
            .Where(c => c.WordId == newWordId && c.ReadingIndex == newReadingIndex && userIds.Contains(c.UserId))
            .ToDictionaryAsync(c => c.UserId);
        if (newCards.Count == 0)
            return 0;

        var collisions = oldCards.Where(c => newCards.ContainsKey(c.UserId)).ToList();
        var cardIds = collisions.Select(c => c.CardId).Concat(newCards.Values.Select(c => c.CardId)).ToList();
        var reviewCounts = await userContext.FsrsReviewLogs
            .Where(l => cardIds.Contains(l.CardId))
            .GroupBy(l => l.CardId)
            .Select(g => new { CardId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CardId, x => x.Count);

        foreach (var oldCard in collisions)
        {
            var newCard = newCards[oldCard.UserId];
            var loser = KeepsOldCard(oldCard, reviewCounts.GetValueOrDefault(oldCard.CardId),
                                     newCard, reviewCounts.GetValueOrDefault(newCard.CardId))
                ? newCard
                : oldCard;

            await CardArchiveService.ArchiveCardsAsync(userContext, loser.UserId, [loser], CardArchiveReason.WordReplacementMerge);
            userContext.FsrsCards.Remove(loser);
        }

        // Flushed before the remap UPDATE, which would otherwise hit the unique key on a surviving old card.
        await userContext.SaveChangesAsync();
        return collisions.Count;
    }

    private static async Task RemapStudyDeckWords(
        UserDbContext userContext,
        int oldWordId, byte oldReadingIndex,
        int newWordId, byte newReadingIndex,
        WordReplacementResult result)
    {
        await userContext.Database.ExecuteSqlRawAsync(@"
            UPDATE ""user"".""UserStudyDeckWords"" correct
            SET ""Occurrences"" = correct.""Occurrences"" + wrong.""Occurrences""
            FROM ""user"".""UserStudyDeckWords"" wrong
            WHERE correct.""UserStudyDeckId"" = wrong.""UserStudyDeckId""
              AND correct.""WordId"" = {0} AND correct.""ReadingIndex"" = {1}
              AND wrong.""WordId"" = {2} AND wrong.""ReadingIndex"" = {3}",
            newWordId, newReadingIndex, oldWordId, oldReadingIndex);

        result.StudyDeckWordsUpdated = await userContext.Database.ExecuteSqlRawAsync(@"
            DELETE FROM ""user"".""UserStudyDeckWords"" wrong
            USING ""user"".""UserStudyDeckWords"" correct
            WHERE correct.""UserStudyDeckId"" = wrong.""UserStudyDeckId""
              AND correct.""WordId"" = {0} AND correct.""ReadingIndex"" = {1}
              AND wrong.""WordId"" = {2} AND wrong.""ReadingIndex"" = {3}",
            newWordId, newReadingIndex, oldWordId, oldReadingIndex);

        result.StudyDeckWordsUpdated += await userContext.Database.ExecuteSqlRawAsync(@"
            UPDATE ""user"".""UserStudyDeckWords""
            SET ""WordId"" = {0}, ""ReadingIndex"" = {1}
            WHERE ""WordId"" = {2} AND ""ReadingIndex"" = {3}",
            newWordId, newReadingIndex, oldWordId, oldReadingIndex);
    }

    private static async Task RemapWordSetMembers(
        JitenDbContext context,
        int oldWordId, byte oldReadingIndex,
        int newWordId, byte newReadingIndex,
        WordReplacementResult result)
    {
        result.WordSetMembersUpdated = await context.Database.ExecuteSqlRawAsync(@"
            DELETE FROM jiten.""WordSetMembers"" wrong
            USING jiten.""WordSetMembers"" correct
            WHERE correct.""SetId"" = wrong.""SetId""
              AND correct.""WordId"" = {0} AND correct.""ReadingIndex"" = {1}
              AND wrong.""WordId"" = {2} AND wrong.""ReadingIndex"" = {3}",
            newWordId, newReadingIndex, oldWordId, oldReadingIndex);

        result.WordSetMembersUpdated += await context.Database.ExecuteSqlRawAsync(@"
            UPDATE jiten.""WordSetMembers""
            SET ""WordId"" = {0}, ""ReadingIndex"" = {1}
            WHERE ""WordId"" = {2} AND ""ReadingIndex"" = {3}",
            newWordId, newReadingIndex, oldWordId, oldReadingIndex);
    }

    /// <summary>Card media and custom sentences move only where the new pair is free; a clashing row stays on the old pair rather than be deleted.</summary>
    private static async Task RemapUserAuthoredContent(
        UserDbContext userContext,
        int oldWordId, byte oldReadingIndex,
        int newWordId, byte newReadingIndex,
        WordReplacementResult result)
    {
        result.CardMediaMoved = await userContext.Database.ExecuteSqlRawAsync(@"
            UPDATE ""user"".""UserCardMedia"" old
            SET ""WordId"" = {0}, ""ReadingIndex"" = {1}
            WHERE old.""WordId"" = {2} AND old.""ReadingIndex"" = {3}
              AND NOT EXISTS (
                SELECT 1 FROM ""user"".""UserCardMedia"" existing
                WHERE existing.""UserId"" = old.""UserId"" AND existing.""Kind"" = old.""Kind""
                  AND existing.""WordId"" = {0} AND existing.""ReadingIndex"" = {1}
              )",
            newWordId, newReadingIndex, oldWordId, oldReadingIndex);

        result.UserSentencesMoved = await userContext.Database.ExecuteSqlRawAsync(@"
            UPDATE ""user"".""UserExampleSentences"" old
            SET ""WordId"" = {0}, ""ReadingIndex"" = {1}
            WHERE old.""WordId"" = {2} AND old.""ReadingIndex"" = {3}
              AND NOT EXISTS (
                SELECT 1 FROM ""user"".""UserExampleSentences"" existing
                WHERE existing.""UserId"" = old.""UserId""
                  AND existing.""WordId"" = {0} AND existing.""ReadingIndex"" = {1}
              )",
            newWordId, newReadingIndex, oldWordId, oldReadingIndex);
    }

    private async Task<WordReplacementResult> ComputeDryRunCounts(
        JitenDbContext context,
        UserDbContext userContext,
        int oldWordId, byte oldReadingIndex,
        int newWordId, byte newReadingIndex)
    {
        var result = new WordReplacementResult { WasDryRun = true };

        // Count DeckWords with old reading
        var oldDeckWordCount = await context.DeckWords
            .CountAsync(dw => dw.WordId == oldWordId && dw.ReadingIndex == oldReadingIndex);

        // Count decks that have BOTH old and new reading (merge case)
        var decksWithOld = context.DeckWords
            .Where(dw => dw.WordId == oldWordId && dw.ReadingIndex == oldReadingIndex)
            .Select(dw => dw.DeckId);

        var decksWithNew = context.DeckWords
            .Where(dw => dw.WordId == newWordId && dw.ReadingIndex == newReadingIndex)
            .Select(dw => dw.DeckId);

        var mergeCount = await decksWithOld.Intersect(decksWithNew).CountAsync();

        result.DeckWordsMerged = mergeCount;
        result.DeckWordsUpdated = oldDeckWordCount - mergeCount;

        // Count affected decks
        result.AffectedDeckCount = await context.DeckWords
            .Where(dw => dw.WordId == oldWordId && dw.ReadingIndex == oldReadingIndex)
            .Select(dw => dw.DeckId)
            .Distinct()
            .CountAsync();

        result.ExampleSentenceWordsUpdated = await CountSentencesWithForm(context, oldWordId, oldReadingIndex);

        // Count FsrsCards - those that would be updated (user doesn't have new reading)
        var usersWithOld = userContext.FsrsCards
            .Where(c => c.WordId == oldWordId && c.ReadingIndex == oldReadingIndex)
            .Select(c => c.UserId);

        var usersWithNew = userContext.FsrsCards
            .Where(c => c.WordId == newWordId && c.ReadingIndex == newReadingIndex)
            .Select(c => c.UserId);

        var usersWithBoth = await usersWithOld.Intersect(usersWithNew).CountAsync();
        var totalWithOld = await userContext.FsrsCards
            .CountAsync(c => c.WordId == oldWordId && c.ReadingIndex == oldReadingIndex);

        result.FsrsCardsUpdated = totalWithOld - usersWithBoth;
        result.FsrsCardsSkipped = usersWithBoth;

        result.StudyDeckWordsUpdated = await userContext.UserStudyDeckWords
            .CountAsync(w => w.WordId == oldWordId && w.ReadingIndex == oldReadingIndex);
        result.WordSetMembersUpdated = await context.WordSetMembers
            .CountAsync(m => m.WordId == oldWordId && m.ReadingIndex == oldReadingIndex);
        result.CardMediaMoved = await userContext.UserCardMedia
            .CountAsync(m => m.WordId == oldWordId && m.ReadingIndex == oldReadingIndex);
        result.UserSentencesMoved = await userContext.UserExampleSentences
            .CountAsync(s => s.WordId == oldWordId && s.ReadingIndex == oldReadingIndex);

        // Count parent decks that would need recalculation
        var affectedDeckIds = await context.DeckWords
            .Where(dw => dw.WordId == oldWordId && dw.ReadingIndex == oldReadingIndex)
            .Select(dw => dw.DeckId)
            .Distinct()
            .ToListAsync();

        result.ParentDecksQueued = await context.Decks
            .Where(d => affectedDeckIds.Contains(d.DeckId) && d.ParentDeckId != null)
            .Select(d => d.ParentDeckId!.Value)
            .Distinct()
            .CountAsync();

        return result;
    }

    public async Task RecalculateParentDeck(int parentDeckId)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();

            var parentDeck = await context.Decks
                                          .Include(d => d.Children)
                                          .ThenInclude(c => c.DeckWords).Include(deck => deck.DeckWords)
                                          .FirstOrDefaultAsync(d => d.DeckId == parentDeckId);

            if (parentDeck == null)
            {
                logger.LogWarning("Parent deck {DeckId} not found for recalculation", parentDeckId);
                return;
            }

            if (parentDeck.Children.Count == 0)
            {
                logger.LogWarning("Deck {DeckId} has no children to aggregate", parentDeckId);
                return;
            }

            // Delete existing parent DeckWords
            await context.Database.ExecuteSqlRawAsync(
                @"DELETE FROM jiten.""DeckWords"" WHERE ""DeckId"" = {0}",
                parentDeckId);

            // Recalculate using the existing method
            await parentDeck.AddChildDeckWords(context);

            // Bulk insert new DeckWords
            await JitenHelper.BulkInsertDeckWords(contextFactory, parentDeck.DeckWords, parentDeckId);

            // Update deck statistics
            parentDeck.LastUpdate = DateTime.UtcNow;
            await context.SaveChangesAsync();

            logger.LogInformation("Parent deck {DeckId} recalculated with {WordCount} unique words",
                parentDeckId, parentDeck.UniqueWordCount);
        }
        finally
        {
            // Always clear the pending flag so deck can be recalculated again
            await _redis.KeyDeleteAsync($"jiten:parent-deck-recalc-pending:{parentDeckId}");
        }
    }

    public async Task<SplitWordResult> SplitAsync(
        int oldWordId, byte oldReadingIndex,
        List<WordReadingPair> newWords,
        bool dryRun = false)
    {
        var result = new SplitWordResult { WasDryRun = dryRun };

        if (newWords.Count < 2)
            throw new ArgumentException("Split requires at least 2 new words", nameof(newWords));

        await using var context = await contextFactory.CreateDbContextAsync();

        if (dryRun)
        {
            return await ComputeSplitDryRunCounts(context, oldWordId, oldReadingIndex, newWords);
        }

        await using var transaction = await context.Database.BeginTransactionAsync();

        try
        {
            // Collect affected deck IDs before making changes
            var affectedDeckIds = await context.DeckWords
                .Where(dw => dw.WordId == oldWordId && dw.ReadingIndex == oldReadingIndex)
                .Select(dw => dw.DeckId)
                .Distinct()
                .ToListAsync();

            result.AffectedDeckCount = affectedDeckIds.Count;

            // Capture parent deck deltas BEFORE modifying children
            var parentDeltas = await context.DeckWords
                .Where(dw => dw.WordId == oldWordId && dw.ReadingIndex == oldReadingIndex)
                .Join(context.Decks.Where(d => d.ParentDeckId != null),
                      dw => dw.DeckId,
                      d => d.DeckId,
                      (dw, d) => new { d.ParentDeckId, dw.Occurrences })
                .GroupBy(x => x.ParentDeckId)
                .Select(g => new { ParentDeckId = g.Key!.Value, TotalOccurrences = g.Sum(x => x.Occurrences) })
                .ToListAsync();

            // Get old DeckWords with their occurrences (we need these for insertion)
            var oldDeckWords = await context.DeckWords
                .Where(dw => dw.WordId == oldWordId && dw.ReadingIndex == oldReadingIndex)
                .Select(dw => new { dw.DeckId, dw.Occurrences })
                .ToListAsync();

            result.DeckWordsDeleted = oldDeckWords.Count;

            // For each new word, merge or insert
            foreach (var newWord in newWords)
            {
                // Step 1: Merge - add occurrences to existing entries
                var merged = await context.Database.ExecuteSqlRawAsync(@"
                    UPDATE jiten.""DeckWords"" existing
                    SET ""Occurrences"" = existing.""Occurrences"" + old.""Occurrences""
                    FROM jiten.""DeckWords"" old
                    WHERE existing.""WordId"" = {0}
                      AND existing.""ReadingIndex"" = {1}
                      AND old.""WordId"" = {2}
                      AND old.""ReadingIndex"" = {3}
                      AND existing.""DeckId"" = old.""DeckId""",
                    newWord.WordId, newWord.ReadingIndex, oldWordId, oldReadingIndex);

                result.DeckWordsMerged += merged;

                // Step 2: Insert where new word doesn't exist in deck
                var inserted = await context.Database.ExecuteSqlRawAsync(@"
                    INSERT INTO jiten.""DeckWords"" (""WordId"", ""ReadingIndex"", ""DeckId"", ""Occurrences"")
                    SELECT {0}, {1}, old.""DeckId"", old.""Occurrences""
                    FROM jiten.""DeckWords"" old
                    WHERE old.""WordId"" = {2}
                      AND old.""ReadingIndex"" = {3}
                      AND NOT EXISTS (
                        SELECT 1 FROM jiten.""DeckWords"" existing
                        WHERE existing.""DeckId"" = old.""DeckId""
                          AND existing.""WordId"" = {0}
                          AND existing.""ReadingIndex"" = {1}
                      )",
                    newWord.WordId, newWord.ReadingIndex, oldWordId, oldReadingIndex);

                result.DeckWordsInserted += inserted;
            }

            // Step 3: Delete old entries
            await context.Database.ExecuteSqlRawAsync(@"
                DELETE FROM jiten.""DeckWords""
                WHERE ""WordId"" = {0} AND ""ReadingIndex"" = {1}",
                oldWordId, oldReadingIndex);

            // Look up reading lengths for each new word
            var newWordIds = newWords.Select(w => w.WordId).ToList();
            var replForms = await context.WordForms
                .AsNoTracking()
                .Where(wf => newWordIds.Contains(wf.WordId))
                .ToDictionaryAsync(wf => (wf.WordId, wf.ReadingIndex));

            // Calculate lengths for each new word
            var wordLengths = new List<int>();
            foreach (var newWord in newWords)
            {
                if (replForms.TryGetValue((newWord.WordId, (short)newWord.ReadingIndex), out var form))
                {
                    wordLengths.Add(form.Text.Length);
                }
                else
                {
                    wordLengths.Add(1);
                }
            }

            result.ExampleSentenceWordsDeleted = await RewriteSentenceTokens(
                context, oldWordId, oldReadingIndex, token => SplitToken(token, newWords, wordLengths));
            await SentenceProfileService.RewriteWordAsync(context, affectedDeckIds, ExampleSentenceTokens.WordKey(oldWordId, oldReadingIndex),
                                                          newWords.Select(w => ExampleSentenceTokens.WordKey(w.WordId, (byte)w.ReadingIndex)).ToList());
            result.ExampleSentenceWordsInserted = result.ExampleSentenceWordsDeleted * newWords.Count;

            // Update UniqueWordCount on affected decks
            if (affectedDeckIds.Count > 0)
            {
                var deckIdsArray = affectedDeckIds.ToArray();
                await context.Database.ExecuteSqlAsync($@"
                    UPDATE jiten.""Decks"" d
                    SET ""UniqueWordCount"" = (
                        SELECT COUNT(DISTINCT (""WordId"", ""ReadingIndex""))
                        FROM jiten.""DeckWords"" WHERE ""DeckId"" = d.""DeckId""
                    ),
                    ""UniqueWordUsedOnceCount"" = (
                        SELECT COUNT(*)
                        FROM jiten.""DeckWords"" WHERE ""DeckId"" = d.""DeckId"" AND ""Occurrences"" = 1
                    )
                    WHERE d.""DeckId"" = ANY({deckIdsArray})");
            }

            await transaction.CommitAsync();

            // Queue incremental parent updates (outside transaction)
            foreach (var delta in parentDeltas)
            {
                // Remove old word from parent
                QueueIncrementalParentRemove(delta.ParentDeckId, oldWordId, oldReadingIndex, delta.TotalOccurrences);

                // Add each new word to parent
                foreach (var newWord in newWords)
                {
                    QueueIncrementalParentAdd(delta.ParentDeckId, newWord.WordId, newWord.ReadingIndex, delta.TotalOccurrences);
                }
            }

            result.ParentDecksQueued = parentDeltas.Count;

            logger.LogInformation(
                "Word split completed: {OldWordId}:{OldReadingIndex} -> {NewWordCount} words. " +
                "DeckWords: {Deleted} deleted, {Inserted} inserted, {Merged} merged. " +
                "ExampleSentences: {EsDeleted} deleted, {EsInserted} inserted. Parent decks queued: {Parents}",
                oldWordId, oldReadingIndex, newWords.Count,
                result.DeckWordsDeleted, result.DeckWordsInserted, result.DeckWordsMerged,
                result.ExampleSentenceWordsDeleted, result.ExampleSentenceWordsInserted, result.ParentDecksQueued);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            logger.LogError(ex, "Word split failed: {OldWordId}:{OldReadingIndex}",
                oldWordId, oldReadingIndex);
            throw;
        }

        return result;
    }

    private async Task<SplitWordResult> ComputeSplitDryRunCounts(
        JitenDbContext context,
        int oldWordId, byte oldReadingIndex,
        List<WordReadingPair> newWords)
    {
        var result = new SplitWordResult { WasDryRun = true };

        // Count DeckWords to be deleted
        result.DeckWordsDeleted = await context.DeckWords
            .CountAsync(dw => dw.WordId == oldWordId && dw.ReadingIndex == oldReadingIndex);

        result.AffectedDeckCount = await context.DeckWords
            .Where(dw => dw.WordId == oldWordId && dw.ReadingIndex == oldReadingIndex)
            .Select(dw => dw.DeckId)
            .Distinct()
            .CountAsync();

        // For each new word, count merges vs inserts
        var decksWithOld = context.DeckWords
            .Where(dw => dw.WordId == oldWordId && dw.ReadingIndex == oldReadingIndex)
            .Select(dw => dw.DeckId);

        foreach (var newWord in newWords)
        {
            var decksWithNew = context.DeckWords
                .Where(dw => dw.WordId == newWord.WordId && dw.ReadingIndex == newWord.ReadingIndex)
                .Select(dw => dw.DeckId);

            var mergeCount = await decksWithOld.Intersect(decksWithNew).CountAsync();
            result.DeckWordsMerged += mergeCount;
            result.DeckWordsInserted += result.DeckWordsDeleted - mergeCount;
        }

        result.ExampleSentenceWordsDeleted = await CountSentencesWithForm(context, oldWordId, oldReadingIndex);
        result.ExampleSentenceWordsInserted = result.ExampleSentenceWordsDeleted * newWords.Count;

        // Count parent decks
        var affectedDeckIds = await context.DeckWords
            .Where(dw => dw.WordId == oldWordId && dw.ReadingIndex == oldReadingIndex)
            .Select(dw => dw.DeckId)
            .Distinct()
            .ToListAsync();

        result.ParentDecksQueued = await context.Decks
            .Where(d => affectedDeckIds.Contains(d.DeckId) && d.ParentDeckId != null)
            .Select(d => d.ParentDeckId!.Value)
            .Distinct()
            .CountAsync();

        return result;
    }

    public async Task<RemoveWordResult> RemoveAsync(
        int wordId, byte readingIndex,
        bool dryRun = false)
    {
        var result = new RemoveWordResult { WasDryRun = dryRun };

        await using var context = await contextFactory.CreateDbContextAsync();
        await using var userContext = await userContextFactory.CreateDbContextAsync();

        if (dryRun)
        {
            return await ComputeRemoveDryRunCounts(context, wordId, readingIndex);
        }

        await using var transaction = await context.Database.BeginTransactionAsync();

        try
        {
            // Collect affected deck IDs before making changes
            var affectedDeckIds = await context.DeckWords
                .Where(dw => dw.WordId == wordId && dw.ReadingIndex == readingIndex)
                .Select(dw => dw.DeckId)
                .Distinct()
                .ToListAsync();

            result.AffectedDeckCount = affectedDeckIds.Count;

            // Capture parent deck deltas BEFORE modifying children
            var parentDeltas = await context.DeckWords
                .Where(dw => dw.WordId == wordId && dw.ReadingIndex == readingIndex)
                .Join(context.Decks.Where(d => d.ParentDeckId != null),
                      dw => dw.DeckId,
                      d => d.DeckId,
                      (dw, d) => new { d.ParentDeckId, dw.Occurrences })
                .GroupBy(x => x.ParentDeckId)
                .Select(g => new { ParentDeckId = g.Key!.Value, TotalOccurrences = g.Sum(x => x.Occurrences) })
                .ToListAsync();

            // Delete DeckWords
            result.DeckWordsDeleted = await context.Database.ExecuteSqlRawAsync(@"
                DELETE FROM jiten.""DeckWords""
                WHERE ""WordId"" = {0} AND ""ReadingIndex"" = {1}",
                wordId, readingIndex);

            result.ExampleSentenceWordsDeleted = await RewriteSentenceTokens(context, wordId, readingIndex, _ => []);
            await SentenceProfileService.RewriteWordAsync(context, affectedDeckIds, ExampleSentenceTokens.WordKey(wordId, readingIndex), []);

            // Update UniqueWordCount on affected decks
            if (affectedDeckIds.Count > 0)
            {
                var deckIdsArray = affectedDeckIds.ToArray();
                await context.Database.ExecuteSqlAsync($@"
                    UPDATE jiten.""Decks"" d
                    SET ""UniqueWordCount"" = (
                        SELECT COUNT(DISTINCT (""WordId"", ""ReadingIndex""))
                        FROM jiten.""DeckWords"" WHERE ""DeckId"" = d.""DeckId""
                    ),
                    ""UniqueWordUsedOnceCount"" = (
                        SELECT COUNT(*)
                        FROM jiten.""DeckWords"" WHERE ""DeckId"" = d.""DeckId"" AND ""Occurrences"" = 1
                    )
                    WHERE d.""DeckId"" = ANY({deckIdsArray})");
            }

            await transaction.CommitAsync();

            // Queue incremental parent updates (outside transaction)
            foreach (var delta in parentDeltas)
            {
                QueueIncrementalParentRemove(delta.ParentDeckId, wordId, readingIndex, delta.TotalOccurrences);
            }

            result.ParentDecksQueued = parentDeltas.Count;

            logger.LogInformation(
                "Word removal completed: {WordId}:{ReadingIndex}. " +
                "DeckWords: {DwDeleted}. ExampleSentences: {EsDeleted}." +
                "Affected decks: {Decks}. Parent decks queued: {Parents}",
                wordId, readingIndex,
                result.DeckWordsDeleted, result.ExampleSentenceWordsDeleted,
                result.AffectedDeckCount, result.ParentDecksQueued);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            logger.LogError(ex, "Word removal failed: {WordId}:{ReadingIndex}",
                wordId, readingIndex);
            throw;
        }

        return result;
    }

    private async Task<RemoveWordResult> ComputeRemoveDryRunCounts(
        JitenDbContext context,
        int wordId, byte readingIndex)
    {
        var result = new RemoveWordResult { WasDryRun = true };

        result.DeckWordsDeleted = await context.DeckWords
            .CountAsync(dw => dw.WordId == wordId && dw.ReadingIndex == readingIndex);

        result.AffectedDeckCount = await context.DeckWords
            .Where(dw => dw.WordId == wordId && dw.ReadingIndex == readingIndex)
            .Select(dw => dw.DeckId)
            .Distinct()
            .CountAsync();

        result.ExampleSentenceWordsDeleted = await CountSentencesWithForm(context, wordId, readingIndex);

        var affectedDeckIds = await context.DeckWords
            .Where(dw => dw.WordId == wordId && dw.ReadingIndex == readingIndex)
            .Select(dw => dw.DeckId)
            .Distinct()
            .ToListAsync();

        result.ParentDecksQueued = await context.Decks
            .Where(d => affectedDeckIds.Contains(d.DeckId) && d.ParentDeckId != null)
            .Select(d => d.ParentDeckId!.Value)
            .Distinct()
            .CountAsync();

        return result;
    }

    private const int TokenRewriteChunk = 2000;

    /// <summary>Rewrites the form's tokens in every sentence holding it, in the caller's transaction, keeping each sampling bucket.</summary>
    private static async Task<int> RewriteSentenceTokens(JitenDbContext context, int wordId, byte readingIndex,
                                                         Func<SentenceToken, IEnumerable<SentenceToken>> rewrite)
    {
        var key = ExampleSentenceTokens.WordKey(wordId, readingIndex);
        var sentenceIds = await context.ExampleSentences
            .Where(s => s.WordKeys.Contains(key))
            .Select(s => s.SentenceId)
            .ToListAsync();

        int changed = 0;
        foreach (var chunk in sentenceIds.Chunk(TokenRewriteChunk))
        {
            var sentences = await context.ExampleSentences
                .Where(s => chunk.Contains(s.SentenceId))
                .ToListAsync();

            foreach (var sentence in sentences)
            {
                var rewritten = new List<SentenceToken>();
                var seen = new HashSet<(byte Position, int WordKey)>();
                foreach (var token in ExampleSentenceTokens.Decode(sentence.Tokens))
                {
                    var replacements = token.WordId == wordId && token.ReadingIndex == readingIndex
                        ? rewrite(token)
                        : [token];

                    foreach (var replacement in replacements)
                    {
                        if (seen.Add((replacement.Position, replacement.WordKey)))
                            rewritten.Add(replacement);
                    }
                }

                sentence.Tokens = ExampleSentenceTokens.Encode(rewritten, ExampleSentenceTokens.IsPartial(sentence.Tokens));
                sentence.WordKeys = ExampleSentenceTokens.WordKeys(rewritten,
                    ExampleSentenceTokens.FineBucketOf(sentence.WordKeys) ?? Random.Shared.Next(ExampleSentenceTokens.FineBucketCount));
                changed++;
            }

            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }

        return changed;
    }

    private static Task<int> CountSentencesWithForm(JitenDbContext context, int wordId, byte readingIndex)
    {
        var key = ExampleSentenceTokens.WordKey(wordId, readingIndex);
        return context.ExampleSentences.CountAsync(s => s.WordKeys.Contains(key));
    }

    /// <summary>Lays the new words end to end from the old token's start, clipped to its span, as the link rows are.</summary>
    private static IEnumerable<SentenceToken> SplitToken(SentenceToken token, List<WordReadingPair> newWords, List<int> wordLengths)
    {
        int end = token.Position + token.Length;
        int position = token.Position;
        for (int i = 0; i < newWords.Count && position < end; i++)
        {
            int length = Math.Min(wordLengths[i], end - position);
            if (ExampleSentenceTokens.CanEncode(newWords[i].WordId, position, length))
                yield return token with
                {
                    WordId = newWords[i].WordId, ReadingIndex = newWords[i].ReadingIndex,
                    Position = (byte)position, Length = (byte)length, IsFunctionWord = false
                };

            position += wordLengths[i];
        }
    }
}
