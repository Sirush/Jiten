using Hangfire;
using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Api.Jobs;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

/// <summary>BatchId is null when nothing was inserted, since an empty batch is not kept.</summary>
public record CardBatchSeedResult(Guid? BatchId, int Inserted, int InsertedWords, int Pruned);

public interface ICardBatchService
{
    /// <summary>Marks every form ranked <paramref name="minRank"/> to <paramref name="maxRank"/> as mastered under one new batch; WordsInRange counts the words ranked there.</summary>
    Task<(CardBatchSeedResult Seed, int WordsInRange)> SeedFrequencyRangeAsync(string userId, int minRank, int maxRank, CardBatchDetails details);

    /// <summary>Inserts the unsaved <paramref name="cards"/> whose form is valid and missing from the collection, under one new batch, and queues a coverage recompute.</summary>
    Task<CardBatchSeedResult> SeedAsync(string userId, IReadOnlyCollection<FsrsCard> cards, CardBatchDetails details);

    /// <summary>Unsaved batch for the caller to stamp; a <paramref name="continueBatchId"/> naming the user's live batch of the same source resumes it.</summary>
    Task<CardBatch> BeginAsync(string userId, CardBatchDetails details, FsrsState? cardState, Guid? continueBatchId = null);

    /// <summary>Recounts the batch's cards and saves; a batch left with no cards is dropped. Call after the cards are saved.</summary>
    Task<int> CompleteAsync(CardBatch batch);

    /// <summary>Marks the current user's deck words mastered or blacklisted under one deck-learn batch; returns the cards inserted.</summary>
    Task<int> LearnDeckWordsAsync(IEnumerable<DeckWord> words, bool mastered, CardBatchDetails details, bool countAsNewlyLearned);

    Task<PaginatedResponse<List<CardBatchDto>>> ListAsync(string userId, int offset, int limit);

    /// <summary>Deletes the batch's untouched cards, archiving any with history; null when the user has no such batch, a no-op when repeated.</summary>
    Task<CardBatchUndoResult?> UndoAsync(string userId, Guid batchId);
}

public class CardBatchService(
    UserDbContext userContext,
    JitenDbContext jitenContext,
    IWordFormSiblingCache wordFormCache,
    IBackgroundJobClient backgroundJobs,
    IStudySessionService sessionService,
    ICurrentUserService currentUserService,
    IOnboardingStore onboarding) : ICardBatchService
{
    public async Task<(CardBatchSeedResult Seed, int WordsInRange)> SeedFrequencyRangeAsync(string userId, int minRank, int maxRank,
                                                                                           CardBatchDetails details)
    {
        var forms = await jitenContext.WordFormFrequencies
                                      .AsNoTracking()
                                      .Where(f => f.FrequencyRank > 0 && f.FrequencyRank >= minRank && f.FrequencyRank <= maxRank)
                                      .Select(f => new { f.WordId, f.ReadingIndex })
                                      .ToListAsync();

        var now = DateTime.UtcNow;
        var cards = forms.Select(f => new FsrsCard(userId, f.WordId, (byte)f.ReadingIndex, due: now, lastReview: now, state: FsrsState.Mastered))
                         .ToList();
        var seed = await SeedAsync(userId, cards, details);
        return (seed, forms.Select(f => f.WordId).Distinct().Count());
    }

    public async Task<CardBatchSeedResult> SeedAsync(string userId, IReadOnlyCollection<FsrsCard> cards, CardBatchDetails details)
    {
        var requestedCards = cards.DistinctBy(c => (c.WordId, c.ReadingIndex)).ToList();
        if (requestedCards.Count == 0)
            return new CardBatchSeedResult(null, 0, 0, 0);

        var requested = requestedCards.Select(c => (c.WordId, c.ReadingIndex)).ToList();

        var wordIds = requested.Select(f => f.WordId).Distinct().ToList();

        var validForms = (await jitenContext.WordForms
                                            .AsNoTracking()
                                            .Where(wf => wordIds.Contains(wf.WordId))
                                            .Select(wf => new { wf.WordId, wf.ReadingIndex })
                                            .ToListAsync())
                         .Select(f => (f.WordId, (byte)f.ReadingIndex))
                         .ToHashSet();

        var existing = (await userContext.FsrsCards
                                         .AsNoTracking()
                                         .Where(c => c.UserId == userId && wordIds.Contains(c.WordId))
                                         .Select(c => new { c.WordId, c.ReadingIndex })
                                         .ToListAsync())
                       .Select(c => (c.WordId, c.ReadingIndex))
                       .ToHashSet();

        var toInsert = requestedCards.Where(c => validForms.Contains((c.WordId, c.ReadingIndex)) && !existing.Contains((c.WordId, c.ReadingIndex)))
                                     .ToList();
        var states = toInsert.Select(c => c.State).Distinct().ToList();
        var batch = await BeginAsync(userId, details, states.Count == 1 ? states[0] : null);
        batch.Stamp(toInsert);

        var redundant = await WordFormHelper.ArchiveRedundantImportCards(
            userContext, wordFormCache, userId, toInsert, requested.Concat(existing).ToHashSet(), _ => []);
        toInsert.RemoveAll(redundant.Contains);

        if (toInsert.Count > 0)
        {
            await userContext.FsrsCards.AddRangeAsync(toInsert);
            await userContext.SaveChangesAsync();
        }

        var pruned = await WordFormHelper.PruneRedundantForms(userContext, wordFormCache, userId, wordIds);
        if (pruned > 0)
            await userContext.SaveChangesAsync();

        var inserted = await CompleteAsync(batch);
        var insertedWords = inserted == 0
            ? 0
            : await userContext.FsrsCards
                               .Where(c => c.UserId == userId && c.SourceBatchId == batch.BatchId)
                               .Select(c => c.WordId)
                               .Distinct()
                               .CountAsync();

        await QueueCoverageRefreshAsync(userId);

        return new CardBatchSeedResult(inserted > 0 ? batch.BatchId : null, inserted, insertedWords, pruned);
    }

    public async Task<int> LearnDeckWordsAsync(IEnumerable<DeckWord> words, bool mastered, CardBatchDetails details, bool countAsNewlyLearned)
    {
        var userId = currentUserService.UserId!;
        var batch = await BeginAsync(userId, details, mastered ? FsrsState.Mastered : FsrsState.Blacklisted);
        var result = mastered
            ? await currentUserService.AddKnownWords(words, batch.Source, batch.BatchId, countAsNewlyLearned: countAsNewlyLearned)
            : await currentUserService.BlacklistWords(words, batch.Source, batch.BatchId);
        await CompleteAsync(batch);

        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
        await userContext.SaveChangesAsync();
        return result.Inserted;
    }

    public async Task<CardBatch> BeginAsync(string userId, CardBatchDetails details, FsrsState? cardState, Guid? continueBatchId = null)
    {
        var source = details.Kind.Source();
        var batchId = Guid.NewGuid();
        if (continueBatchId is { } requestedId && requestedId != Guid.Empty)
        {
            var current = await userContext.CardBatches.FirstOrDefaultAsync(b => b.BatchId == requestedId);
            if (current != null && current.UserId == userId && current.Source == source && !current.Undone)
            {
                if (current.CardState != cardState)
                    current.CardState = null;
                return current;
            }

            if (current == null)
                batchId = requestedId;
        }

        var batch = new CardBatch
                    {
                        BatchId = batchId,
                        UserId = userId,
                        Source = source,
                        CreatedAt = DateTime.UtcNow,
                        Details = details.Sanitised(),
                        CardState = cardState
                    };
        await userContext.CardBatches.AddAsync(batch);
        return batch;
    }

    public async Task<int> CompleteAsync(CardBatch batch)
    {
        var count = await userContext.FsrsCards.CountAsync(c => c.UserId == batch.UserId && c.SourceBatchId == batch.BatchId);

        var entry = userContext.Entry(batch);
        if (entry.State == EntityState.Detached)
            entry.State = await userContext.CardBatches.AnyAsync(b => b.BatchId == batch.BatchId)
                ? EntityState.Unchanged
                : EntityState.Added;

        if (count == 0)
        {
            if (entry.State == EntityState.Added)
                entry.State = EntityState.Detached;
            else
                userContext.CardBatches.Remove(batch);
        }
        else
        {
            batch.CardCount = count;
        }

        await userContext.SaveChangesAsync();
        return count;
    }

    public async Task<PaginatedResponse<List<CardBatchDto>>> ListAsync(string userId, int offset, int limit)
    {
        var query = userContext.CardBatches.AsNoTracking().Where(b => b.UserId == userId);
        var totalItems = await query.CountAsync();
        var batches = await query.OrderByDescending(b => b.CreatedAt)
                                 .ThenBy(b => b.BatchId)
                                 .Skip(offset)
                                 .Take(limit)
                                 .ToListAsync();

        var liveIds = batches.Where(b => !b.Undone).Select(b => (Guid?)b.BatchId).ToList();
        var wordCounts = await userContext.FsrsCards
                                          .Where(c => c.UserId == userId && liveIds.Contains(c.SourceBatchId))
                                          .GroupBy(c => c.SourceBatchId)
                                          .Select(g => new { BatchId = g.Key, Words = g.Select(c => c.WordId).Distinct().Count() })
                                          .ToDictionaryAsync(g => g.BatchId!.Value, g => g.Words);
        var removableCounts = await RemovableCards(userId, liveIds)
                                    .GroupBy(c => c.SourceBatchId)
                                    .Select(g => new { BatchId = g.Key, Count = g.Select(c => c.WordId).Distinct().Count() })
                                    .ToDictionaryAsync(g => g.BatchId!.Value, g => g.Count);

        var deckIds = batches.Select(b => b.Details.DeckId).OfType<int>().Distinct().ToList();
        var decks = deckIds.Count == 0
            ? new Dictionary<int, CardBatchDeckDto>()
            : await jitenContext.Decks.AsNoTracking()
                                .Where(d => deckIds.Contains(d.DeckId))
                                .Select(d => new CardBatchDeckDto(d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle))
                                .ToDictionaryAsync(d => d.DeckId);

        var items = batches.Select(batch => new CardBatchDto
                                            {
                                                BatchId = batch.BatchId,
                                                Source = batch.Source,
                                                Details = batch.Details,
                                                Deck = batch.Details.DeckId is { } deckId ? decks.GetValueOrDefault(deckId) : null,
                                                CreatedAt = batch.CreatedAt,
                                                CardCount = batch.CardCount,
                                                WordCount = batch.Undone ? batch.CardCount : wordCounts.GetValueOrDefault(batch.BatchId),
                                                RemovableCount = removableCounts.GetValueOrDefault(batch.BatchId),
                                                Undone = batch.Undone
                                            })
                           .ToList();

        return new PaginatedResponse<List<CardBatchDto>>(items, totalItems, limit, offset);
    }

    public async Task<CardBatchUndoResult?> UndoAsync(string userId, Guid batchId)
    {
        if (!await userContext.CardBatches.AnyAsync(b => b.BatchId == batchId && b.UserId == userId))
            return null;

        int removed, removedWords, kept;
        await using (var transaction = await userContext.Database.BeginTransactionAsync())
        {
            var claimed = await userContext.CardBatches
                                           .Where(b => b.BatchId == batchId && b.UserId == userId && !b.Undone)
                                           .ExecuteUpdateAsync(s => s.SetProperty(b => b.Undone, true));
            if (claimed == 0)
            {
                await transaction.RollbackAsync();
                return new CardBatchUndoResult { Kept = await CountBatchWords(userId, batchId), AlreadyUndone = true };
            }

            await CardArchiveService.ArchiveByQueryAsync(userContext, userId, RemovableCards(userId, batchId).Where(c => c.ReviewLogs.Any()),
                                                         CardArchiveReason.ImportUndo);
            removedWords = await RemovableCards(userId, batchId).Select(c => c.WordId).Distinct().CountAsync();
            removed = await RemovableCards(userId, batchId).ExecuteDeleteAsync();

            kept = await CountBatchWords(userId, batchId);
            await transaction.CommitAsync();
        }

        if (removed > 0)
        {
            await QueueCoverageRefreshAsync(userId);
            await sessionService.BumpStudyOverviewVersion(userId);
        }

        await onboarding.ForgetUndoneBatchAsync(userId, batchId);
        return new CardBatchUndoResult { Removed = removedWords, Kept = kept };
    }

    private Task<int> CountBatchWords(string userId, Guid batchId)
        => userContext.FsrsCards.Where(c => c.UserId == userId && c.SourceBatchId == batchId).Select(c => c.WordId).Distinct().CountAsync();

    private IQueryable<FsrsCard> RemovableCards(string userId, Guid batchId)
        => RemovableCards(userId, [batchId]);

    /// <summary>The batches' cards the user has not touched since: no review after the batch, and still in the state it wrote when it wrote one.</summary>
    private IQueryable<FsrsCard> RemovableCards(string userId, List<Guid?> batchIds)
        => userContext.FsrsCards.Where(c => c.UserId == userId
                                            && batchIds.Contains(c.SourceBatchId)
                                            && userContext.CardBatches.Any(b => b.BatchId == c.SourceBatchId
                                                                                && !c.ReviewLogs.Any(l => l.ReviewDateTime > b.CreatedAt)
                                                                                && (b.CardState == null || c.State == b.CardState)));

    private async Task QueueCoverageRefreshAsync(string userId)
    {
        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
        await userContext.SaveChangesAsync();
        backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserCoverage(userId));
    }
}
