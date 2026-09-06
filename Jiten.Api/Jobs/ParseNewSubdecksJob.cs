using Hangfire;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

public class ParseNewSubdecksJob(
    IDbContextFactory<JitenDbContext> contextFactory,
    IBackgroundJobClient backgroundJobs,
    IPendingCoverageQueue pendingCoverageQueue,
    ILogger<ParseNewSubdecksJob> logger)
{
    [Queue("reparse")]
    [AutomaticRetry(Attempts = 1)]
    public async Task ParseNewSubdecks(int parentDeckId, List<int> newChildDeckIds)
    {
        logger.LogInformation("ParseNewSubdecks: parent={ParentId}, newChildren=[{Ids}]",
            parentDeckId, string.Join(", ", newChildDeckIds));

        await using var context = await contextFactory.CreateDbContextAsync();

        var newChildren = await context.Decks
            .Include(d => d.RawText)
            .Where(d => newChildDeckIds.Contains(d.DeckId))
            .ToListAsync();

        var childrenWithText = newChildren.Where(d => d.RawText != null).ToList();
        if (childrenWithText.Count == 0)
        {
            logger.LogWarning("ParseNewSubdecks: no new children with raw text found, skipping");
            return;
        }

        var parent = await context.Decks
            .FirstOrDefaultAsync(d => d.DeckId == parentDeckId);

        if (parent == null)
        {
            logger.LogWarning("ParseNewSubdecks: parent deck {ParentId} not found", parentDeckId);
            return;
        }

        var dictEntries = await context.Set<DeckDictionaryEntry>()
            .Where(e => e.DeckId == parentDeckId)
            .ToListAsync();

        var texts = childrenWithText.Select(c => c.RawText!.RawText).ToList();
        var parsedDecks = await Parser.Parser.ParseTextsToDeck(
            contextFactory,
            texts,
            storeRawText: true,
            predictDifficulty: true,
            parent.MediaType,
            dictionaryEntries: dictEntries.Count > 0 ? dictEntries : null);

        for (int i = 0; i < childrenWithText.Count; i++)
        {
            var original = childrenWithText[i];
            var parsed = parsedDecks[i];

            original.CharacterCount = parsed.CharacterCount;
            original.WordCount = parsed.WordCount;
            original.UniqueWordCount = parsed.UniqueWordCount;
            original.UniqueWordUsedOnceCount = parsed.UniqueWordUsedOnceCount;
            original.UniqueKanjiCount = parsed.UniqueKanjiCount;
            original.UniqueKanjiUsedOnceCount = parsed.UniqueKanjiUsedOnceCount;
            original.SentenceCount = parsed.SentenceCount;
            original.DialoguePercentage = parsed.DialoguePercentage;

            if (original.MediaType is MediaType.Manga or MediaType.Anime or MediaType.Movie or MediaType.Drama or MediaType.Audio or MediaType.YouTube)
                original.SentenceCount = 0;

            await context.SaveChangesAsync();

            await JitenHelper.DeleteDeckData(context, original.DeckId);
            await JitenHelper.BulkInsertDeckData(contextFactory, original.DeckId,
                parsed.DeckWords?.ToList() ?? [],
                parsed.ExampleSentences?.ToList() ?? []);

            logger.LogInformation("ParseNewSubdecks: parsed child {DeckId} ({Title})",
                original.DeckId, original.OriginalTitle);
        }

        var parentWithChildren = await context.Decks
                                              .Include(d => d.Children).ThenInclude(c => c.DeckWords)
                                              .FirstAsync(d => d.DeckId == parentDeckId);

        var parentSentences = await context.ExampleSentences
                                           .AsNoTracking()
                                           .Where(s => s.DeckId == parentDeckId)
                                           .ToListAsync();

        await parentWithChildren.AddChildDeckWords(context);

        var parentWords = parentWithChildren.DeckWords?.ToList() ?? [];
        parentWithChildren.DeckWords = new List<DeckWord>();

        // Update parent stats and persist
        await context.SaveChangesAsync();

        await JitenHelper.DeleteDeckData(context, parentDeckId);
        await JitenHelper.BulkInsertDeckData(contextFactory, parentDeckId, parentWords, parentSentences);

        // Enqueue decks for the periodic coverage sweeper (coalesces per-user work)
        var pendingDeckIds = childrenWithText.Select(c => c.DeckId).Append(parentDeckId).ToList();
        await pendingCoverageQueue.AddManyAsync(pendingDeckIds);

        foreach (var child in childrenWithText)
            backgroundJobs.Enqueue<StatsComputationJob>(job => job.ComputeDeckCoverageStats(child.DeckId));
        backgroundJobs.Enqueue<StatsComputationJob>(job => job.ComputeDeckCoverageStats(parentDeckId));

        // Queue difficulty — will skip children that already have difficulty computed
        backgroundJobs.Enqueue<DifficultyComputationJob>(job => job.ComputeDeckDifficulty(parentDeckId, false));

        logger.LogInformation("ParseNewSubdecks: completed for parent {ParentId}", parentDeckId);
    }
}
