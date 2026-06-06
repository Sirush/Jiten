using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Providers;
using Microsoft.EntityFrameworkCore;
using Hangfire;
using Jiten.Cli;
using Jiten.Parser;

namespace Jiten.Api.Jobs;

public class ParseJob(
    IDbContextFactory<JitenDbContext> contextFactory,
    IBackgroundJobClient backgroundJobs,
    IPendingCoverageQueue pendingCoverageQueue,
    IPendingEmbeddingQueue pendingEmbeddingQueue,
    IIndexNowService indexNow)
{
    [Queue("parse")]
    public async Task Parse(Metadata metadata, MediaType deckType, bool storeRawText = false)
    {
        Deck deck = new();
        string filePath = metadata.FilePath!;

        await using var context = await contextFactory.CreateDbContextAsync();

        if (!string.IsNullOrEmpty(metadata.FilePath))
        {
            if (!File.Exists(metadata.FilePath))
            {
                throw new FileNotFoundException($"File {filePath} not found.");
            }

            string text = "";
            if (Path.GetExtension(filePath).ToLower() == ".epub")
            {
                var extractor = new EbookExtractor();
                text = await extractor.ExtractTextFromEbook(filePath);

                if (string.IsNullOrEmpty(text))
                {
                    throw new Exception("No text found in the ebook.");
                }
            }
            else if (Path.GetExtension(filePath).ToLower() == ".mokuro")
            {
                var extractor = new MokuroExtractor();
                text = await extractor.Extract(filePath, false);

                if (string.IsNullOrEmpty(text))
                {
                    throw new Exception("No text found in the mokuro file.");
                }
            }
            else if (new[] { ".ass", ".srt", ".ssa" }.Contains(Path.GetExtension(filePath).ToLower()))
            {
                var extractor = new SubtitleExtractor();
                text = await extractor.Extract(filePath);

                if (string.IsNullOrEmpty(text))
                {
                    throw new Exception("No text found in the subtitle file.");
                }

                if (metadata.SpeechDuration is null or 0)
                {
                    var items = await extractor.ExtractItems(filePath);
                    if (items.Count > 0)
                    {
                        var stats = await SubtitleMoraRateCalculator.ComputeAsync(items);
                        metadata.SpeechDuration = stats.DurationMs;
                        metadata.SpeechMoraCount = stats.MoraCount;
                    }
                }
            }
            else
            {
                text = await File.ReadAllTextAsync(filePath);
            }

            deck = await Parser.Parser.ParseTextToDeck(contextFactory, text, storeRawText, true, deckType,
                dictionaryEntries: metadata.DictionaryEntries);
        }

        deck.SpeechDuration = metadata.SpeechDuration ?? 0;
        deck.SpeechMoraCount = metadata.SpeechMoraCount ?? 0;
        deck.DictionaryEntries = metadata.DictionaryEntries;

        // Batch process ALL descendants in one Sudachi call
        if (metadata.Children.Count > 0)
        {
            await ParseChildrenBatched(metadata.Children, deck, deckType, storeRawText, metadata.DictionaryEntries);
        }

        await deck.AddChildDeckWords(context);

        deck.OriginalTitle = metadata.OriginalTitle;
        deck.MediaType = deckType;

        if (metadata.ReleaseDate != null)
            deck.ReleaseDate = DateOnly.FromDateTime(metadata.ReleaseDate.Value);

        if (deckType is MediaType.Manga or MediaType.Anime or MediaType.Movie or MediaType.Drama or MediaType.Audio)
            deck.SentenceCount = 0;

        deck.RomajiTitle = metadata.RomajiTitle;
        deck.EnglishTitle = metadata.EnglishTitle;
        deck.Description = metadata.Description?.Length > 2000 ? metadata.Description?[..2000] : metadata.Description;;
        deck.Links = metadata.Links;
        deck.CoverName = metadata.Image ?? "nocover.jpg";
        deck.CreationDate = DateTimeOffset.UtcNow;
        deck.LastUpdate = DateTime.UtcNow;
        deck.DifficultyOverride = -1;
        deck.Titles = metadata.Aliases.Select(a => new DeckTitle { DeckId = deck.DeckId, Title = a, TitleType = DeckTitleType.Alias }).ToList();
        deck.ExternalRating = metadata.Rating != null ? (byte)metadata.Rating : (byte)0;

        foreach (var link in deck.Links)
        {
            link.Deck = deck;
        }

        // Apply genre and tag mappings based on first link
        if (metadata.Links.Any())
        {
            var firstLink = metadata.Links.First();
            await MetadataProviderHelper.ApplyGenreAndTagMappings(context, deck, metadata, firstLink.LinkType);
        }

        var coverImage = await File.ReadAllBytesAsync(metadata.Image ?? throw new Exception("No cover image found."));

        // Insert the deck into the database
        await JitenHelper.InsertDeck(contextFactory, deck, coverImage ?? [], false);

        // Process relations from metadata
        if (metadata.Relations.Count > 0)
        {
            await using var relationContext = await contextFactory.CreateDbContextAsync();
            await MetadataProviderHelper.ProcessRelations(relationContext, deck.DeckId, metadata.Relations);
        }

        // Enqueue decks for the periodic coverage sweeper (coalesces per-user work)
        await QueueCoverageComputationForDeckTree(deck);

        // Enqueue the parent deck for the periodic embedding sweeper (vocabulary similarity)
        await pendingEmbeddingQueue.AddAsync(deck.DeckId);

        // Queue coverage statistics computation for main deck and all children
        QueueStatsComputationForDeckTree(deck);

        // Queue difficulty computation (job handles children internally)
        backgroundJobs.Enqueue<DifficultyComputationJob>(
            job => job.ComputeDeckDifficulty(deck.DeckId, true));

        // Notify IndexNow of the new (top-level) deck page so Bing can index it quickly.
        await indexNow.SubmitDeckAsync(deck.DeckId);
    }

    /// <summary>
    /// Flattens all descendants, batches text extraction, makes ONE Sudachi call,
    /// then reassembles the hierarchy.
    /// </summary>
    private async Task ParseChildrenBatched(List<Metadata> children, Deck rootDeck, MediaType deckType, bool storeRawText,
                                               List<DeckDictionaryEntry>? dictionaryEntries = null)
    {
        // Flatten ALL descendants with their hierarchy info
        // Using Metadata as key to track parent relationships
        var flatList = new List<(Metadata meta, string text, Metadata? parentMeta, int order)>();
        FlattenDescendants(children, null, flatList, startOrder: 1);

        // Extract text for each (handles .epub, .mokuro, .txt) and compute speech stats for subtitles
        for (int i = 0; i < flatList.Count; i++)
        {
            var (meta, _, parentMeta, order) = flatList[i];
            var text = await ExtractTextFromMetadata(meta);
            await ComputeSpeechStatsIfNeeded(meta);
            flatList[i] = (meta, text, parentMeta, order);
        }

        // Filter out empty texts but keep track of all metadata for hierarchy
        var validItems = flatList.Where(x => !string.IsNullOrEmpty(x.text)).ToList();
        if (validItems.Count == 0) return;

        // Batch parse ALL texts - SINGLE Sudachi call for entire tree (children inherit parent's dictionary entries)
        var decks = await Parser.Parser.ParseTextsToDeck(
            contextFactory,
            validItems.Select(x => x.text).ToList(),
            storeRawText,
            predictDifficulty: true,
            deckType,
            dictionaryEntries: dictionaryEntries);

        // Create mapping from Metadata -> Deck for hierarchy reassembly
        var metaToDeck = new Dictionary<Metadata, Deck>();
        for (int i = 0; i < decks.Count; i++)
        {
            var deck = decks[i];
            var (meta, _, parentMeta, order) = validItems[i];

            deck.DeckOrder = order;
            deck.OriginalTitle = meta.OriginalTitle;
            deck.MediaType = deckType;
            deck.DifficultyOverride = -1;
            deck.SpeechDuration = meta.SpeechDuration ?? 0;
            deck.SpeechMoraCount = meta.SpeechMoraCount ?? 0;

            if (deckType is MediaType.Manga or MediaType.Anime or MediaType.Movie or MediaType.Drama or MediaType.Audio)
                deck.SentenceCount = 0;

            metaToDeck[meta] = deck;
        }

        // Reassemble hierarchy
        foreach (var (meta, _, parentMeta, _) in validItems)
        {
            var deck = metaToDeck[meta];

            if (parentMeta == null)
            {
                // Direct child of root
                deck.ParentDeck = rootDeck;
                rootDeck.Children.Add(deck);
            }
            else if (metaToDeck.TryGetValue(parentMeta, out var parentDeck))
            {
                // Child of another parsed deck
                deck.ParentDeck = parentDeck;
                parentDeck.Children.Add(deck);
            }
        }
    }

    /// <summary>
    /// Recursively flattens the metadata tree, preserving parent references.
    /// </summary>
    private void FlattenDescendants(
        List<Metadata> children,
        Metadata? parentMeta,
        List<(Metadata meta, string text, Metadata? parentMeta, int order)> flatList,
        int startOrder)
    {
        int order = startOrder;
        foreach (var child in children)
        {
            flatList.Add((child, "", parentMeta, order++));

            if (child.Children.Count > 0)
            {
                FlattenDescendants(child.Children, child, flatList, startOrder: 1);
            }
        }
    }

    private static async Task ComputeSpeechStatsIfNeeded(Metadata metadata)
    {
        if (string.IsNullOrEmpty(metadata.FilePath) || metadata.SpeechDuration is not null and not 0)
            return;

        var ext = Path.GetExtension(metadata.FilePath).ToLower();
        if (ext is not (".ass" or ".srt" or ".ssa"))
            return;

        var items = await new SubtitleExtractor().ExtractItems(metadata.FilePath);
        if (items.Count > 0)
        {
            var stats = await SubtitleMoraRateCalculator.ComputeAsync(items);
            metadata.SpeechDuration = stats.DurationMs;
            metadata.SpeechMoraCount = stats.MoraCount;
        }
    }

    private async Task<string> ExtractTextFromMetadata(Metadata metadata)
    {
        if (string.IsNullOrEmpty(metadata.FilePath)) return "";

        string filePath = metadata.FilePath;
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File {filePath} not found.");

        return Path.GetExtension(filePath).ToLower() switch
        {
            ".epub" => await new EbookExtractor().ExtractTextFromEbook(filePath),
            ".mokuro" => await new MokuroExtractor().Extract(filePath, false),
            ".ass" or ".srt" or ".ssa" => await new SubtitleExtractor().Extract(filePath),
            _ => await File.ReadAllTextAsync(filePath)
        };
    }

    private async Task QueueCoverageComputationForDeckTree(Deck deck)
    {
        var deckIds = new List<int>();
        CollectDeckIds(deck, deckIds);
        await pendingCoverageQueue.AddManyAsync(deckIds);
    }

    private static void CollectDeckIds(Deck deck, List<int> acc)
    {
        acc.Add(deck.DeckId);
        foreach (var child in deck.Children)
            CollectDeckIds(child, acc);
    }

    private void QueueStatsComputationForDeckTree(Deck deck)
    {
        backgroundJobs.Enqueue<StatsComputationJob>(job => job.ComputeDeckCoverageStats(deck.DeckId));

        foreach (var child in deck.Children)
        {
            QueueStatsComputationForDeckTree(child);
        }
    }
}
