using Hangfire;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

public class ReparseJob(
    IDbContextFactory<JitenDbContext> contextFactory,
    IBackgroundJobClient backgroundJobs,
    Jiten.Api.Services.IPendingEmbeddingQueue pendingEmbeddingQueue,
    Jiten.Api.Services.IIndexNowService indexNow)
{
    // Single-deck reparse: notifies IndexNow. Kept as a 1-arg overload so in-flight Hangfire jobs
    // serialized before this change still resolve.
    [Queue("reparse")]
    public Task Reparse(int deckId) => Reparse(deckId, notifyIndexNow: true);

    // Bulk reparse endpoints pass notifyIndexNow: false — never mass-submit the catalogue to IndexNow.
    [Queue("reparse")]
    public async Task Reparse(int deckId, bool notifyIndexNow)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var deck = await context.Decks.AsNoTracking()
                                .Include(d => d.RawText).Include(d => d.ExampleSentences)
                                .Include(d => d.Children).ThenInclude(deck => deck.RawText)
                                .Include(deck => deck.ExampleSentences)
                                .Include(d => d.DictionaryEntries)
                                .FirstOrDefaultAsync(d => d.DeckId == deckId);
        if (deck == null)
            throw new Exception($"Deck with ID {deckId} not found.");

        if (deck.RawText == null && deck.Children.Count == 0)
            throw new Exception($"Deck with ID {deckId} has no raw text to reparse.");

        // Load dictionary entries: own entries, or parent's if this is a child
        var dictEntries = deck.DictionaryEntries.Count > 0
            ? deck.DictionaryEntries
            : deck.ParentDeckId != null
                ? await context.Set<DeckDictionaryEntry>().Where(e => e.DeckId == deck.ParentDeckId).ToListAsync()
                : null;

        var children = deck.Children.ToList();

        if (deck.Children.Count == 0)
        {
            Deck newDeck = await Parser.Parser.ParseTextToDeck(contextFactory, deck.RawText!.RawText, true, true, deck.MediaType,
                dictionaryEntries: dictEntries);
            deck.CharacterCount = newDeck.CharacterCount;
            deck.WordCount = newDeck.WordCount;
            deck.UniqueWordCount = newDeck.UniqueWordCount;
            deck.UniqueWordUsedOnceCount = newDeck.UniqueWordUsedOnceCount;
            deck.UniqueKanjiCount = newDeck.UniqueKanjiCount;
            deck.UniqueKanjiUsedOnceCount = newDeck.UniqueKanjiUsedOnceCount;
            deck.SentenceCount = newDeck.SentenceCount;
            deck.DeckWords = newDeck.DeckWords;
            deck.ExampleSentences = newDeck.ExampleSentences;
            deck.DialoguePercentage = newDeck.DialoguePercentage;

            if (deck.MediaType is MediaType.Manga or MediaType.Anime or MediaType.Movie or MediaType.Drama or MediaType.Audio)
                deck.SentenceCount = 0;
        }
        else
        {
            // Validate all children have raw text
            foreach (var child in children)
            {
                if (child.RawText == null)
                    throw new Exception($"Child deck with ID {child.DeckId} has no raw text to reparse.");
            }


            var texts = children.Select(c => c.RawText!.RawText).ToList();
            var newDecks = await Parser.Parser.ParseTextsToDeck(
                contextFactory,
                texts,
                storeRawText: true,
                predictDifficulty: true,
                deck.MediaType,
                dictionaryEntries: dictEntries);

            // Copy properties back to original deck objects
            for (int i = 0; i < children.Count; i++)
            {
                var original = children[i];
                var parsed = newDecks[i];

                original.CharacterCount = parsed.CharacterCount;
                original.WordCount = parsed.WordCount;
                original.UniqueWordCount = parsed.UniqueWordCount;
                original.UniqueWordUsedOnceCount = parsed.UniqueWordUsedOnceCount;
                original.UniqueKanjiCount = parsed.UniqueKanjiCount;
                original.UniqueKanjiUsedOnceCount = parsed.UniqueKanjiUsedOnceCount;
                original.SentenceCount = parsed.SentenceCount;
                original.DeckWords = parsed.DeckWords;
                original.ExampleSentences = parsed.ExampleSentences;
                original.DialoguePercentage = parsed.DialoguePercentage;

                if (original.MediaType is MediaType.Manga or MediaType.Anime or MediaType.Movie or MediaType.Drama or MediaType.Audio)
                    original.SentenceCount = 0;
            }

            deck.Children = children;
            await deck.AddChildDeckWords(context);
        }

        deck.LastUpdate = DateTime.UtcNow;

        await JitenHelper.InsertDeck(contextFactory, deck, [], true);

        QueueStatsComputationForDeckTree(deck);
        backgroundJobs.Enqueue<DifficultyComputationJob>(job => job.ComputeDeckDifficulty(deck.DeckId, false));

        // Re-embed the affected parent deck for vocabulary similarity (periodic sweep batches these).
        await pendingEmbeddingQueue.AddAsync(deck.ParentDeckId ?? deck.DeckId);

        // Notify IndexNow of the top-level deck page (skipped for bulk reparses to respect Bing's quota).
        if (notifyIndexNow)
            await indexNow.SubmitDeckAsync(deck.ParentDeckId ?? deck.DeckId);
    }

    private void QueueStatsComputationForDeckTree(Deck deck)
    {
        backgroundJobs.Enqueue<StatsComputationJob>(job => job.ComputeDeckCoverageStats(deck.DeckId));
        foreach (var child in deck.Children)
            QueueStatsComputationForDeckTree(child);
    }
}