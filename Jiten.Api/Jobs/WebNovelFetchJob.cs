using Hangfire;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.WebNovel;
using Jiten.Core.WebNovel;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

public class WebNovelFetchJob(
    IDbContextFactory<JitenDbContext> contextFactory,
    IWebNovelSourceResolver sourceResolver,
    IBackgroundJobClient backgroundJobs,
    ILogger<WebNovelFetchJob> logger)
{
    /// <summary>
    /// Appends any new episodes to the novel's subdecks.
    ///
    /// Subdeck rows and their raw text are updated <b>by DeckId</b>. The InsertDeck(update: true) path must
    /// never be used here: JitenHelper.CollectChildDeckUpdates matches children by OriginalTitle and
    /// orphan-deletes the ones it can't match, and the open subdeck's title changes on every append — that
    /// would delete the subdeck and every user's progress on it.
    /// </summary>
    [Queue(WebNovelQueues.Syosetu)]
    [AutomaticRetry(Attempts = 1)]
    public async Task Sync(int parentDeckId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var tracked = await context.WebNovelSources
                                   .Include(s => s.Chapters)
                                   .FirstOrDefaultAsync(s => s.DeckId == parentDeckId);

        if (tracked == null)
        {
            logger.LogWarning("WebNovelSync: deck {DeckId} is not a tracked webnovel", parentDeckId);
            return;
        }

        try
        {
            var source = sourceResolver.Resolve(tracked.Provider);

            var info = await source.GetInfoAsync(tracked.SourceId);
            var toc = await source.GetTocAsync(tracked.SourceId);

            var ledger = tracked.Chapters.ToDictionary(c => c.EpisodeNumber);

            // Episode numbers at the source are positional: deleting one renumbers everything after it,
            // so the ledger no longer lines up with the table of contents. Appending would attach the
            // wrong text to the wrong episode — stop and surface it instead.
            if (toc.Count < ledger.Count)
            {
                throw new InvalidOperationException(
                    $"The source lists {toc.Count} episodes but {ledger.Count} are ingested — episodes were " +
                    "deleted or renumbered at the source. Rebuild the affected subdecks or re-import the novel.");
            }

            var newEpisodes = toc.Where(e => !ledger.ContainsKey(e.Number)).OrderBy(e => e.Number).ToList();

            // Episodes we already hold whose text changed at the source (改稿). Applying these means
            // rebuilding the subdeck that holds them, so they are surfaced for a manual refresh instead.
            var revisedCount = toc.Count(e => ledger.TryGetValue(e.Number, out var known) &&
                                              e.UpdatedAt != null && known.SourceUpdatedAt != null &&
                                              e.UpdatedAt > known.SourceUpdatedAt);

            if (newEpisodes.Count == 0)
            {
                ApplySuccessState(tracked, info, revisedCount);
                await context.SaveChangesAsync();
                logger.LogInformation("WebNovelSync: deck {DeckId} has no new episodes ({Revised} revised)",
                                      parentDeckId, revisedCount);
                return;
            }

            logger.LogInformation("WebNovelSync: deck {DeckId} has {Count} new episodes", parentDeckId, newEpisodes.Count);

            var fetched = new List<(WebNovelEpisodeRef Reference, ChunkEpisode Chunk, string Text)>();
            foreach (var reference in newEpisodes)
            {
                var text = await source.GetEpisodeTextAsync(tracked.SourceId, reference);
                fetched.Add((reference,
                             new ChunkEpisode(reference.Number, reference.Title, SubdeckChunker.CountCharacters(text)),
                             text));
            }

            var existingChunks = await BuildExistingChunksAsync(context, tracked);
            var plans = SubdeckChunker.Plan(existingChunks,
                                            fetched.Select(f => f.Chunk).ToList(),
                                            tracked.ChunkCharBudget ?? SubdeckChunker.DefaultCharBudget);

            var textByEpisode = fetched.ToDictionary(f => f.Chunk.Number, f => f.Text);
            var referenceByEpisode = fetched.ToDictionary(f => f.Chunk.Number, f => f.Reference);

            // One transaction around the whole apply: appended raw text must never be committed without the
            // matching ledger rows and success state, or the subdeck's text and words silently diverge.
            await using var transaction = await context.Database.BeginTransactionAsync();

            var changedChildIds = await ApplyPlansAsync(context, tracked, plans, textByEpisode, referenceByEpisode);

            tracked.LastEpisodeCount = tracked.Chapters.Count;
            ApplySuccessState(tracked, info, revisedCount);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();

            // Reparses exactly the changed subdecks by DeckId, re-aggregates the parent and preserves progress
            backgroundJobs.Enqueue<ParseNewSubdecksJob>(job => job.ParseNewSubdecks(parentDeckId, changedChildIds));

            logger.LogInformation("WebNovelSync: deck {DeckId} appended {Episodes} episodes across {Subdecks} subdecks",
                                  parentDeckId, newEpisodes.Count, changedChildIds.Count);
        }
        catch (Exception ex)
        {
            // This context's tracker may hold half-applied changes (or be the thing that threw), so the
            // failure bookkeeping runs on its own context.
            await RecordFailureAsync(parentDeckId, ex);
            throw;
        }
    }

    private static void ApplySuccessState(WebNovelSource tracked, WebNovelInfo info, int revisedCount)
    {
        tracked.CompletedAtSource = info.IsCompleted;
        tracked.OnHiatusAtSource = info.IsOnHiatus;
        tracked.PendingRevisionCount = revisedCount;
        tracked.LastSourceUpdate = info.LastUpdatedAt;
        tracked.LastSyncedAt = DateTimeOffset.UtcNow;
        tracked.NextCheckAt = WebNovelSchedule.NextCheck(info.IsCompleted);
        tracked.ConsecutiveFailures = 0;
        tracked.LastError = null;
    }

    private async Task RecordFailureAsync(int parentDeckId, Exception ex)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var tracked = await context.WebNovelSources.FirstOrDefaultAsync(s => s.DeckId == parentDeckId);
            if (tracked == null)
                return;

            tracked.ConsecutiveFailures++;
            tracked.LastError = WebNovelImportJob.Truncate(ex.Message, 1000);
            tracked.NextCheckAt = WebNovelSchedule.NextCheckAfterFailure(tracked.ConsecutiveFailures);
            await context.SaveChangesAsync();

            if (tracked.ConsecutiveFailures >= 3)
            {
                logger.LogError(ex, "WebNovelSync: deck {DeckId} has failed {Count} times in a row — the site's markup " +
                                    "has probably changed, re-check the narou.rb site definitions",
                                parentDeckId, tracked.ConsecutiveFailures);
            }
            else
            {
                logger.LogWarning(ex, "WebNovelSync: deck {DeckId} failed", parentDeckId);
            }
        }
        catch (Exception saveEx)
        {
            logger.LogError(saveEx, "WebNovelSync: could not record the failure for deck {DeckId}", parentDeckId);
        }
    }

    /// <summary>
    /// Re-fetches every episode in one subdeck and replaces its text, picking up revisions (改稿) to episodes
    /// that were already ingested. Manual, because a rebuild costs one request per episode in the range.
    /// </summary>
    [Queue(WebNovelQueues.Syosetu)]
    [AutomaticRetry(Attempts = 1)]
    public async Task RebuildSubdeck(int parentDeckId, int childDeckId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var tracked = await context.WebNovelSources
                                   .Include(s => s.Chapters)
                                   .FirstOrDefaultAsync(s => s.DeckId == parentDeckId);

        if (tracked == null)
            return;

        var chapters = tracked.Chapters.Where(c => c.ChildDeckId == childDeckId)
                              .OrderBy(c => c.EpisodeNumber)
                              .ToList();

        if (chapters.Count == 0)
        {
            logger.LogWarning("WebNovelRebuild: subdeck {ChildDeckId} holds no episodes", childDeckId);
            return;
        }

        var source = sourceResolver.Resolve(tracked.Provider);
        var toc = (await source.GetTocAsync(tracked.SourceId)).ToDictionary(e => e.Number);

        var texts = new List<string>();
        foreach (var chapter in chapters)
        {
            if (!toc.TryGetValue(chapter.EpisodeNumber, out var reference))
            {
                // The episode was deleted at the source; keep the range intact and skip it
                logger.LogWarning("WebNovelRebuild: episode {Episode} no longer exists at the source",
                                  chapter.EpisodeNumber);
                continue;
            }

            var text = await source.GetEpisodeTextAsync(tracked.SourceId, reference);
            texts.Add(text);

            chapter.Title = WebNovelImportJob.Truncate(reference.Title, 500);
            chapter.SourceUpdatedAt = reference.UpdatedAt;
            chapter.CharCount = SubdeckChunker.CountCharacters(text);
        }

        var rawText = await context.DeckRawTexts.FirstOrDefaultAsync(t => t.DeckId == childDeckId);
        if (rawText == null)
        {
            rawText = new DeckRawText { DeckId = childDeckId };
            context.DeckRawTexts.Add(rawText);
        }

        rawText.RawText = string.Join("\n\n", texts);

        // Recompute over the whole ledger: this rebuild refreshed its own chapters' SourceUpdatedAt, but
        // other subdecks may still hold revised episodes awaiting their own rebuild.
        tracked.PendingRevisionCount = tracked.Chapters.Count(c => toc.TryGetValue(c.EpisodeNumber, out var e) &&
                                                                   e.UpdatedAt != null && c.SourceUpdatedAt != null &&
                                                                   e.UpdatedAt > c.SourceUpdatedAt);
        await context.SaveChangesAsync();

        backgroundJobs.Enqueue<ParseNewSubdecksJob>(job => job.ParseNewSubdecks(parentDeckId, new List<int> { childDeckId }));

        logger.LogInformation("WebNovelRebuild: rebuilt subdeck {ChildDeckId} from {Count} episodes",
                              childDeckId, texts.Count);
    }

    private static async Task<List<ExistingChunk>> BuildExistingChunksAsync(JitenDbContext context, WebNovelSource tracked)
    {
        if (tracked.Chapters.Count == 0)
            return [];

        var orderByChild = await context.Decks
                                        .Where(d => d.ParentDeckId == tracked.DeckId)
                                        .Select(d => new { d.DeckId, d.DeckOrder })
                                        .ToDictionaryAsync(d => d.DeckId, d => d.DeckOrder);

        return tracked.Chapters
                      .GroupBy(c => c.ChildDeckId)
                      .Select(g => new ExistingChunk(
                                  ChunkIndex: orderByChild.GetValueOrDefault(g.Key),
                                  ChildDeckId: g.Key,
                                  StartEpisode: g.Min(c => c.EpisodeNumber),
                                  EndEpisode: g.Max(c => c.EpisodeNumber),
                                  EpisodeCount: g.Count(),
                                  CharCount: g.Sum(c => c.CharCount)))
                      .OrderBy(c => c.ChunkIndex)
                      .ToList();
    }

    /// <summary>
    /// Extends the open subdeck and opens new ones, all keyed by DeckId so existing subdecks keep their identity.
    /// </summary>
    private async Task<List<int>> ApplyPlansAsync(JitenDbContext context,
                                                  WebNovelSource tracked,
                                                  List<ChunkPlan> plans,
                                                  Dictionary<int, string> textByEpisode,
                                                  Dictionary<int, WebNovelEpisodeRef> referenceByEpisode)
    {
        var parent = await context.Decks.FirstAsync(d => d.DeckId == tracked.DeckId);
        var changedChildIds = new List<int>();

        foreach (var plan in plans)
        {
            var appendedText = string.Join("\n\n", plan.EpisodesToAppend.Select(e => textByEpisode[e.Number]));
            int childDeckId;

            if (plan.ChildDeckId is { } existingChildId)
            {
                var child = await context.Decks.FirstAsync(d => d.DeckId == existingChildId);
                var rawText = await context.DeckRawTexts.FirstOrDefaultAsync(t => t.DeckId == existingChildId);

                if (rawText == null)
                {
                    // Raw-text storage was off when this subdeck was created; there is nothing to append to
                    logger.LogError("WebNovelSync: subdeck {ChildDeckId} has no stored raw text, cannot append",
                                    existingChildId);
                    continue;
                }

                rawText.RawText = $"{rawText.RawText}\n\n{appendedText}";

                // The open subdeck's episode range grew
                child.OriginalTitle = plan.Title;
                child.LastUpdate = DateTimeOffset.UtcNow;

                childDeckId = existingChildId;
            }
            else
            {
                var child = new Deck
                {
                    ParentDeckId = tracked.DeckId,
                    DeckOrder = plan.ChunkIndex,
                    MediaType = parent.MediaType,
                    OriginalTitle = plan.Title,
                    DifficultyOverride = -1,
                    CreationDate = DateTimeOffset.UtcNow,
                    LastUpdate = DateTimeOffset.UtcNow,
                    RawText = new DeckRawText(appendedText)
                };

                context.Decks.Add(child);
                await context.SaveChangesAsync();

                childDeckId = child.DeckId;
            }

            foreach (var episode in plan.EpisodesToAppend)
            {
                tracked.Chapters.Add(new WebNovelChapter
                {
                    DeckId = tracked.DeckId,
                    EpisodeNumber = episode.Number,
                    ChildDeckId = childDeckId,
                    Title = WebNovelImportJob.Truncate(episode.Title, 500),
                    SourceUpdatedAt = referenceByEpisode[episode.Number].UpdatedAt,
                    CharCount = episode.CharCount
                });
            }

            changedChildIds.Add(childDeckId);
        }

        return changedChildIds;
    }
}
