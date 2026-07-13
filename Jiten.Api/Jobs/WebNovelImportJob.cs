using Hangfire;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Providers;
using Jiten.Core.Data.WebNovel;
using Jiten.Core.WebNovel;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

public class WebNovelImportJob(
    IDbContextFactory<JitenDbContext> contextFactory,
    IWebNovelSourceResolver sourceResolver,
    ParseJob parseJob,
    IConfiguration config,
    ILogger<WebNovelImportJob> logger)
{
    /// <summary>
    /// Fetches a whole novel and creates the parent deck plus its chapter-range subdecks.
    ///
    /// Runs on the per-site queue: a 1,000-episode novel is ~20 minutes of polite fetching, so it must
    /// never run in-request, and it must serialise with syncs so the site never sees two requests at once.
    /// </summary>
    [Queue(WebNovelQueues.Syosetu)]
    [AutomaticRetry(Attempts = 1)]
    public async Task Import(WebNovelProvider provider, string sourceId, string? coverPath, int? chunkCharBudget)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        if (await context.WebNovelSources.AnyAsync(s => s.Provider == provider && s.SourceId == sourceId))
        {
            logger.LogWarning("WebNovelImport: {Provider}/{SourceId} is already tracked, skipping", provider, sourceId);
            return;
        }

        var source = sourceResolver.Resolve(provider);

        var info = await source.GetInfoAsync(sourceId);

        // InsertDeck dedupes new decks on (OriginalTitle, MediaType) and silently keeps the existing one, which
        // would leave us with a ledger pointing at someone else's deck. Catch it before the long fetch.
        if (await context.Decks.AnyAsync(d => d.OriginalTitle == info.Title && d.MediaType == MediaType.WebNovel))
        {
            throw new InvalidOperationException(
                $"A webnovel deck titled '{info.Title}' already exists. Rename or remove it before importing {sourceId}.");
        }

        var toc = await source.GetTocAsync(sourceId);

        if (toc.Count == 0)
            throw new InvalidOperationException($"{provider}/{sourceId} has no episodes.");

        logger.LogInformation("WebNovelImport: {Title} ({SourceId}) — {Episodes} episodes, ~{Chars} chars",
                              info.Title, sourceId, toc.Count, info.TotalCharacters);

        var budget = chunkCharBudget ?? SubdeckChunker.DefaultCharBudget;

        var workingDirectory = Path.Join(config["StaticFilesPath"], "tmp", $"webnovel-{sourceId}-{Guid.NewGuid()}");
        Directory.CreateDirectory(workingDirectory);

        try
        {
            var episodes = new List<(WebNovelEpisodeRef Reference, ChunkEpisode Chunk, string Text)>();

            foreach (var reference in toc)
            {
                var text = await source.GetEpisodeTextAsync(sourceId, reference);
                episodes.Add((reference,
                              new ChunkEpisode(reference.Number, reference.Title, SubdeckChunker.CountCharacters(text)),
                              text));
            }

            var plans = SubdeckChunker.Plan([], episodes.Select(e => e.Chunk).ToList(), budget);
            var textByEpisode = episodes.ToDictionary(e => e.Chunk.Number, e => e.Text);

            var metadata = info.ToMetadata();

            foreach (var plan in plans)
            {
                var chunkPath = Path.Join(workingDirectory, $"chunk-{plan.ChunkIndex:D4}.txt");
                await File.WriteAllTextAsync(chunkPath, JoinEpisodes(plan, textByEpisode));

                metadata.Children.Add(new Metadata
                {
                    FilePath = chunkPath,
                    OriginalTitle = plan.Title
                });
            }

            if (!string.IsNullOrEmpty(coverPath) && File.Exists(coverPath))
                metadata.Image = coverPath;

            var parentDeckId = await parseJob.ParseAndGetDeckId(metadata, MediaType.WebNovel, storeRawText: true);

            await WriteLedgerAsync(parentDeckId, provider, sourceId, info, plans, episodes, chunkCharBudget);

            // Only on success: a Hangfire retry of a failed import still needs the uploaded cover
            DeleteCoverDirectory(coverPath);

            logger.LogInformation("WebNovelImport: created deck {DeckId} with {Subdecks} subdecks for {Title}",
                                  parentDeckId, plans.Count, info.Title);
        }
        finally
        {
            try
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "WebNovelImport: could not clean up {Directory}", workingDirectory);
            }
        }
    }

    private void DeleteCoverDirectory(string? coverPath)
    {
        if (string.IsNullOrEmpty(coverPath))
            return;

        try
        {
            var directory = Path.GetDirectoryName(coverPath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WebNovelImport: could not clean up the cover at {CoverPath}", coverPath);
        }
    }

    private static string JoinEpisodes(ChunkPlan plan, Dictionary<int, string> textByEpisode) =>
        string.Join("\n\n", plan.EpisodesToAppend.Select(e => textByEpisode[e.Number]));

    private async Task WriteLedgerAsync(int parentDeckId, WebNovelProvider provider, string sourceId, WebNovelInfo info,
                                        List<ChunkPlan> plans,
                                        List<(WebNovelEpisodeRef Reference, ChunkEpisode Chunk, string Text)> episodes,
                                        int? chunkCharBudget)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        // Children were created in chunk order, so DeckOrder maps back to the chunk that produced them
        var childIdsByOrder = await context.Decks
                                           .Where(d => d.ParentDeckId == parentDeckId)
                                           .OrderBy(d => d.DeckOrder)
                                           .Select(d => new { d.DeckId, d.DeckOrder })
                                           .ToDictionaryAsync(d => d.DeckOrder, d => d.DeckId);

        // No subdecks means the deck wasn't actually created (a same-title deck already existed and the insert
        // was skipped). Writing the ledger now would bind this novel to a deck it doesn't own.
        if (childIdsByOrder.Count == 0)
        {
            throw new InvalidOperationException(
                $"Deck {parentDeckId} has no subdecks after import — the insert was skipped, so {sourceId} was not tracked.");
        }

        context.WebNovelSources.Add(new WebNovelSource
        {
            DeckId = parentDeckId,
            Provider = provider,
            SourceId = sourceId,
            LastEpisodeCount = episodes.Count,
            LastSourceUpdate = info.LastUpdatedAt,
            LastSyncedAt = DateTimeOffset.UtcNow,
            NextCheckAt = WebNovelSchedule.NextCheck(info.IsCompleted),
            CompletedAtSource = info.IsCompleted,
            OnHiatusAtSource = info.IsOnHiatus,
            ChunkCharBudget = chunkCharBudget
        });

        var referenceByNumber = episodes.ToDictionary(e => e.Chunk.Number, e => e.Reference);

        foreach (var plan in plans)
        {
            if (!childIdsByOrder.TryGetValue(plan.ChunkIndex, out var childDeckId))
            {
                logger.LogError("WebNovelImport: no subdeck was created for chunk {Chunk} of deck {DeckId}",
                                plan.ChunkIndex, parentDeckId);
                continue;
            }

            foreach (var episode in plan.EpisodesToAppend)
            {
                context.WebNovelChapters.Add(new WebNovelChapter
                {
                    DeckId = parentDeckId,
                    EpisodeNumber = episode.Number,
                    ChildDeckId = childDeckId,
                    Title = Truncate(episode.Title, 500),
                    SourceUpdatedAt = referenceByNumber[episode.Number].UpdatedAt,
                    CharCount = episode.CharCount
                });
            }
        }

        await context.SaveChangesAsync();
    }

    internal static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
