using System.Text.RegularExpressions;
using Hangfire;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Jimaku;
using Jiten.Core.Data.Providers;
using Jiten.Core.Jimaku;
using Jiten.Parser;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

public partial class JimakuSyncJob(
    IDbContextFactory<JitenDbContext> contextFactory,
    JimakuClient client,
    EpisodeAiringLookup airingLookup,
    ParseJob parseJob,
    IBackgroundJobClient backgroundJobs,
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<JimakuSyncJob> logger)
{
    private const string ErrorPrefix = "error:";

    [GeneratedRegex(@"^Episode \d+$")]
    private static partial Regex PositionalTitle();

    /// <summary>
    /// Daily pass over every show past its check time: one AniList request per fifty shows for the episode
    /// totals, then one Jimaku file listing per show. A failing show never stops the rest.
    /// </summary>
    [Queue(JimakuQueues.Sync)]
    public async Task Sweep()
    {
        List<int> dueIds;
        await using (var context = await contextFactory.CreateDbContextAsync())
        {
            var now = DateTimeOffset.UtcNow;
            var due = await context.JimakuSources
                                   .Where(s => s.SyncEnabled && s.NextCheckAt <= now)
                                   .OrderBy(s => s.NextCheckAt)
                                   .ToListAsync();
            if (due.Count == 0)
            {
                logger.LogInformation("JimakuSweep: nothing due");
                return;
            }

            await RefreshAiringAsync(due);
            await context.SaveChangesAsync();
            dueIds = due.Select(s => s.DeckId).ToList();
        }

        var ingested = 0;
        foreach (var deckId in dueIds)
            ingested += await SyncAsync(deckId);

        logger.LogInformation("JimakuSweep: checked {Count} shows, ingested {Ingested} episodes", dueIds.Count, ingested);
    }

    [Queue(JimakuQueues.Sync)]
    [AutomaticRetry(Attempts = 0)]
    public async Task SyncOne(int deckId)
    {
        await using (var context = await contextFactory.CreateDbContextAsync())
        {
            var source = await context.JimakuSources.FirstOrDefaultAsync(s => s.DeckId == deckId);
            if (source == null)
                return;

            await RefreshAiringAsync([source]);
            await context.SaveChangesAsync();
        }

        await SyncAsync(deckId);
    }

    /// <summary>
    /// Creates the deck from every file the pattern clears today and starts tracking the entry.
    /// </summary>
    [Queue(JimakuQueues.Sync)]
    [AutomaticRetry(Attempts = 0)]
    public async Task Create(int jimakuEntryId, string pattern, int? tmdbSeason, int? expectedEpisodesOverride)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        if (await context.JimakuSources.AnyAsync(s => s.JimakuEntryId == jimakuEntryId))
        {
            logger.LogWarning("JimakuCreate: entry {EntryId} is already tracked, skipping", jimakuEntryId);
            return;
        }

        var entry = await client.GetEntryAsync(jimakuEntryId)
                    ?? throw new InvalidOperationException($"Jimaku entry {jimakuEntryId} was not found.");
        if (entry.Flags.Movie)
            throw new InvalidOperationException("A film has no further episodes to track.");

        var source = new JimakuSource
        {
            JimakuEntryId = entry.Id,
            FilePattern = pattern,
            AnilistId = entry.Flags.Anime ? entry.AnilistId : null,
            TmdbId = entry.TmdbId,
            TmdbSeason = tmdbSeason,
            ExpectedEpisodesOverride = expectedEpisodesOverride
        };
        await RefreshAiringAsync([source]);

        var files = await client.GetFilesAsync(entry.Id) ?? [];
        var decisions = JimakuSyncPlanner.Plan(pattern, files.Select(JimakuRemoteFile.From).ToList(), [], source.EffectiveExpectedEpisodes);
        var accepted = decisions.Where(d => d.Status == JimakuFileStatus.Queued).ToList();
        if (accepted.Count == 0)
            throw new InvalidOperationException($"No file of Jimaku entry {entry.Id} can be ingested with the pattern '{pattern}'.");

        var metadata = await MetadataProviderHelper.JimakuEntryMetadataAsync(entry, config["TmdbApiKey"] ?? "")
                       ?? throw new InvalidOperationException($"Jimaku entry {entry.Id} has no AniList or TMDB id.");
        var mediaType = entry.Flags.Anime ? MediaType.Anime : MediaType.Drama;

        // InsertDeck dedupes on (OriginalTitle, MediaType) and would bind the tracker to the other deck
        if (await context.Decks.AnyAsync(d => d.OriginalTitle == metadata.OriginalTitle && d.MediaType == mediaType))
        {
            throw new InvalidOperationException(
                $"A deck titled '{metadata.OriginalTitle}' already exists. Track that deck instead of creating a new one.");
        }

        var workingDirectory = NewWorkingDirectory(entry.Id);
        try
        {
            metadata.Image = await DownloadCoverAsync(metadata.Image, workingDirectory);

            for (var i = 0; i < accepted.Count; i++)
            {
                var file = accepted[i].File;
                var path = Path.Join(workingDirectory, $"{i:D4}{Path.GetExtension(file.Name).ToLowerInvariant()}");
                await client.DownloadAsync(file.Url, path);

                metadata.Children.Add(new Metadata
                {
                    FilePath = path,
                    OriginalFileName = file.Name,
                    OriginalTitle = $"Episode {i + 1}"
                });
            }

            var deckId = await parseJob.ParseAndGetDeckId(metadata, mediaType, bool.Parse(config["StoreRawText"] ?? "false"));

            var childIdByOrder = await context.Decks
                                              .Where(d => d.ParentDeckId == deckId)
                                              .ToDictionaryAsync(d => d.DeckOrder, d => d.DeckId);
            if (childIdByOrder.Count == 0)
                throw new InvalidOperationException($"Deck {deckId} has no subdecks after import, so entry {entry.Id} was not tracked.");

            var now = DateTimeOffset.UtcNow;
            source.DeckId = deckId;
            source.LastCheckedAt = now;
            source.LastSyncedAt = now;
            source.NextCheckAt = JimakuSchedule.NextCheck(source.FinishedAiring);

            for (var i = 0; i < accepted.Count; i++)
            {
                var row = NewLedgerRow(accepted[i], now);
                if (childIdByOrder.TryGetValue(i + 1, out var childDeckId))
                {
                    row.Status = JimakuFileStatus.Imported;
                    row.ChildDeckId = childDeckId;
                }
                else
                {
                    row.Status = JimakuFileStatus.Held;
                    row.HoldReason = $"{ErrorPrefix} the subtitle file has no text";
                }

                source.Files.Add(row);
            }

            foreach (var held in decisions.Where(d => d.Status == JimakuFileStatus.Held))
                source.Files.Add(NewLedgerRow(held, now));

            CompleteIfDone(source);
            context.JimakuSources.Add(source);
            await context.SaveChangesAsync();

            logger.LogInformation("JimakuCreate: deck {DeckId} tracks entry {EntryId} with {Episodes} episodes, {Held} held",
                                  deckId, entry.Id, childIdByOrder.Count, source.Files.Count(f => f.Status == JimakuFileStatus.Held));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    private async Task<int> SyncAsync(int deckId)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var source = await context.JimakuSources.Include(s => s.Files).FirstOrDefaultAsync(s => s.DeckId == deckId);
            if (source == null)
                return 0;

            var files = await client.GetFilesAsync(source.JimakuEntryId)
                        ?? throw new InvalidOperationException($"Jimaku entry {source.JimakuEntryId} no longer exists.");

            var now = DateTimeOffset.UtcNow;
            var ledger = source.Files.Select(f => new JimakuLedgerEntry(f.FileName, PlannerStatus(f), f.EpisodeNumber)).ToList();
            var decisions = JimakuSyncPlanner.Plan(source.FilePattern, files.Select(JimakuRemoteFile.From).ToList(), ledger,
                                                   source.EffectiveExpectedEpisodes);

            foreach (var decision in decisions)
            {
                var row = source.Files.FirstOrDefault(f => f.FileName == decision.File.Name);
                if (row == null)
                {
                    source.Files.Add(NewLedgerRow(decision, now));
                    continue;
                }

                row.Url = decision.File.Url;
                row.Size = decision.File.Size;
                row.SourceModifiedAt = decision.File.ModifiedAt;
                row.EpisodeNumber = decision.Episode;
                row.Status = decision.Status;
                row.HoldReason = decision.HoldReason;
            }

            source.LastCheckedAt = now;
            await context.SaveChangesAsync();

            var queued = source.Files.Where(f => f.Status == JimakuFileStatus.Queued)
                               .OrderBy(f => f.EpisodeNumber ?? double.MaxValue)
                               .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                               .ToList();

            var (ingested, downloadError) = queued.Count > 0 ? await IngestAsync(context, source, queued) : (0, null);

            if (ingested > 0)
                source.LastSyncedAt = DateTimeOffset.UtcNow;

            CompleteIfDone(source);
            source.NextCheckAt = JimakuSchedule.NextCheck(source.FinishedAiring);
            source.ConsecutiveFailures = downloadError == null ? 0 : source.ConsecutiveFailures + 1;
            source.LastError = downloadError;
            await context.SaveChangesAsync();

            logger.LogInformation("JimakuSync: deck {DeckId} ingested {Ingested} episodes, {Held} held",
                                  deckId, ingested, source.Files.Count(f => f.Status == JimakuFileStatus.Held));
            return ingested;
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(deckId, ex);
            return 0;
        }
    }

    /// <summary>A file that failed to ingest waits for an admin instead of being downloaded again every day.</summary>
    private static JimakuFileStatus PlannerStatus(JimakuTrackedFile file) =>
        file.Status == JimakuFileStatus.Held && file.HoldReason?.StartsWith(ErrorPrefix, StringComparison.Ordinal) == true
            ? JimakuFileStatus.Ignored
            : file.Status;

    private async Task<(int Ingested, string? DownloadError)> IngestAsync(JitenDbContext context, JimakuSource source,
                                                                          List<JimakuTrackedFile> queued)
    {
        var parent = await context.Decks.Include(d => d.Children).FirstAsync(d => d.DeckId == source.DeckId);
        if (parent.Children.Count == 0 && parent.CharacterCount > 0)
        {
            throw new InvalidOperationException(
                "This deck holds its text directly. Move the text into a subdeck in the media editor, then sync again.");
        }

        var workingDirectory = NewWorkingDirectory(source.JimakuEntryId);
        try
        {
            var extractor = new SubtitleExtractor();
            var added = new List<(JimakuTrackedFile File, Deck Child)>();
            var nextOrder = parent.Children.Count == 0 ? 1 : parent.Children.Max(c => c.DeckOrder) + 1;
            string? downloadError = null;

            for (var i = 0; i < queued.Count; i++)
            {
                var file = queued[i];
                var path = Path.Join(workingDirectory, $"{i:D4}{Path.GetExtension(file.FileName).ToLowerInvariant()}");

                try
                {
                    await client.DownloadAsync(file.Url, path);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                {
                    downloadError = Truncate($"Could not download '{file.FileName}': {ex.Message}", 1000);
                    logger.LogWarning(ex, "JimakuSync: download of {FileName} failed for deck {DeckId}", file.FileName, source.DeckId);
                    continue;
                }

                string text;
                SubtitleStats stats = default;
                try
                {
                    text = await extractor.Extract(path);
                    var items = await extractor.ExtractItems(path);
                    if (items.Count > 0)
                        stats = await SubtitleMoraRateCalculator.ComputeAsync(items);
                }
                catch (Exception ex)
                {
                    file.Status = JimakuFileStatus.Held;
                    file.HoldReason = Truncate($"{ErrorPrefix} the subtitle file could not be read ({ex.Message})", 500);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    file.Status = JimakuFileStatus.Held;
                    file.HoldReason = $"{ErrorPrefix} the subtitle file has no text";
                    continue;
                }

                var child = new Deck
                {
                    ParentDeckId = parent.DeckId,
                    MediaType = parent.MediaType,
                    OriginalFileName = file.FileName,
                    DeckOrder = nextOrder++,
                    DifficultyOverride = -1,
                    CreationDate = DateTimeOffset.UtcNow,
                    LastUpdate = DateTimeOffset.UtcNow,
                    RawText = new DeckRawText(text),
                    SpeechDuration = stats.DurationMs,
                    SpeechMoraCount = stats.MoraCount
                };
                parent.Children.Add(child);
                added.Add((file, child));
            }

            if (added.Count == 0)
            {
                await context.SaveChangesAsync();
                return (0, downloadError);
            }

            var episodeByChildId = source.Files.Where(f => f.ChildDeckId != null && f.Status == JimakuFileStatus.Imported)
                                         .GroupBy(f => f.ChildDeckId!.Value)
                                         .ToDictionary(g => g.Key, g => g.First().EpisodeNumber);
            var episodeByChild = parent.Children.ToDictionary(c => c, c => episodeByChildId.GetValueOrDefault(c.DeckId));
            foreach (var (file, child) in added)
                episodeByChild[child] = file.EpisodeNumber;

            ArrangeEpisodes(parent.Children.ToList(), episodeByChild, added.Select(a => a.Child).ToHashSet());

            // The subdecks and their ledger rows land together, or a retry would ingest the same episodes twice
            await using var transaction = await context.Database.BeginTransactionAsync();
            parent.LastUpdate = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();

            foreach (var (file, child) in added)
            {
                file.Status = JimakuFileStatus.Imported;
                file.HoldReason = null;
                file.ChildDeckId = child.DeckId;
            }

            await context.SaveChangesAsync();
            await transaction.CommitAsync();

            var parentDeckId = parent.DeckId;
            var childIds = added.Select(a => a.Child.DeckId).ToList();
            backgroundJobs.Enqueue<ParseNewSubdecksJob>(job => job.ParseNewSubdecks(parentDeckId, childIds));

            return (added.Count, downloadError);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    /// <summary>
    /// Titles follow the position in the deck, so a season whose files start at episode 49 still opens with
    /// "Episode 1". An episode that arrives out of order takes its place among the numbered subdecks, and only
    /// subdecks still carrying a positional title are renamed to match.
    /// </summary>
    internal static void ArrangeEpisodes(List<Deck> children, IReadOnlyDictionary<Deck, double?> episodeByChild, IReadOnlySet<Deck> added)
    {
        var numbered = children.Where(c => episodeByChild.GetValueOrDefault(c) != null).ToList();
        var latestExisting = numbered.Where(c => !added.Contains(c)).Select(c => episodeByChild[c]!.Value).DefaultIfEmpty(double.MinValue).Max();
        var outOfOrder = added.Any(c => episodeByChild.GetValueOrDefault(c) < latestExisting);

        if (outOfOrder)
        {
            var slots = numbered.Select(c => c.DeckOrder).OrderBy(o => o).ToList();
            var byEpisode = numbered.OrderBy(c => episodeByChild[c]!.Value).ThenBy(c => c.DeckOrder).ToList();
            for (var i = 0; i < byEpisode.Count; i++)
                byEpisode[i].DeckOrder = slots[i];
        }

        var ordered = children.OrderBy(c => c.DeckOrder).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var child = ordered[i];
            if (added.Contains(child) || (outOfOrder && PositionalTitle().IsMatch(child.OriginalTitle)))
                child.OriginalTitle = $"Episode {i + 1}";
        }
    }

    private async Task RefreshAiringAsync(List<JimakuSource> sources)
    {
        try
        {
            var anime = sources.Where(s => s.AnilistId != null).ToList();
            if (anime.Count > 0)
            {
                var airing = await airingLookup.AnilistAsync(anime.Select(s => s.AnilistId!.Value).ToList());
                foreach (var source in anime)
                {
                    if (airing.TryGetValue(source.AnilistId!.Value, out var info))
                        Apply(source, info);
                }
            }

            foreach (var source in sources.Where(s => s.AnilistId == null && s.TmdbId != null))
            {
                if (await airingLookup.TmdbAsync(source.TmdbId!, source.TmdbSeason) is { } info)
                    Apply(source, info);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "JimakuSync: episode totals could not be refreshed; the last known ones stay in use");
        }

        static void Apply(JimakuSource source, EpisodeAiring info)
        {
            source.ExpectedEpisodes = info.Total ?? source.ExpectedEpisodes;
            source.AiredEpisodes = info.Aired;
            source.FinishedAiring = info.Finished;
        }
    }

    private static void CompleteIfDone(JimakuSource source)
    {
        var imported = source.Files.Count(f => f.Status == JimakuFileStatus.Imported);
        var unresolved = source.Files.Count(f => f.Status is JimakuFileStatus.Held or JimakuFileStatus.Queued);
        if (!JimakuSchedule.IsComplete(source.FinishedAiring, source.EffectiveExpectedEpisodes, imported, unresolved))
            return;

        source.SyncEnabled = false;
        source.CompletedAt = DateTimeOffset.UtcNow;
    }

    private static JimakuTrackedFile NewLedgerRow(JimakuFileDecision decision, DateTimeOffset now) => new()
    {
        FileName = decision.File.Name,
        Url = decision.File.Url,
        Size = decision.File.Size,
        SourceModifiedAt = decision.File.ModifiedAt,
        EpisodeNumber = decision.Episode,
        Status = decision.Status,
        HoldReason = decision.HoldReason,
        FirstSeenAt = now
    };

    private async Task<string?> DownloadCoverAsync(string? imageUrl, string workingDirectory)
    {
        if (string.IsNullOrEmpty(imageUrl))
            return null;

        try
        {
            var bytes = await httpClientFactory.CreateClient().GetByteArrayAsync(imageUrl);
            var path = Path.Join(workingDirectory, "cover.jpg");
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "JimakuCreate: the cover at {Url} could not be downloaded", imageUrl);
            return null;
        }
    }

    private async Task RecordFailureAsync(int deckId, Exception ex)
    {
        logger.LogWarning(ex, "JimakuSync: deck {DeckId} failed", deckId);
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var source = await context.JimakuSources.FirstOrDefaultAsync(s => s.DeckId == deckId);
            if (source == null)
                return;

            source.ConsecutiveFailures++;
            source.LastError = Truncate(ex.Message, 1000);
            source.LastCheckedAt = DateTimeOffset.UtcNow;
            source.NextCheckAt = JimakuSchedule.NextCheckAfterFailure(source.ConsecutiveFailures);
            await context.SaveChangesAsync();
        }
        catch (Exception saveEx)
        {
            logger.LogError(saveEx, "JimakuSync: could not record the failure for deck {DeckId}", deckId);
        }
    }

    private string NewWorkingDirectory(int jimakuEntryId)
    {
        var path = Path.Join(config["StaticFilesPath"], "tmp", $"jimaku-{jimakuEntryId}-{Guid.NewGuid()}");
        Directory.CreateDirectory(path);
        return path;
    }

    private void DeleteWorkingDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "JimakuSync: could not clean up {Directory}", path);
        }
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
