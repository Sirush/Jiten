using Hangfire;
using Jiten.Api.Jobs;
using Jiten.Core.Data;
using Jiten.Core.Data.Jimaku;
using Jiten.Core.Data.Providers.Jimaku;
using Jiten.Core.Jimaku;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Controllers;

public partial class AdminController
{
    private static readonly TimeSpan JimakuPreviewCacheDuration = TimeSpan.FromMinutes(10);
    private const int JimakuRecentDays = 10;

    [HttpGet("jimaku-trackers/search")]
    public async Task<IActionResult> SearchJimakuEntries([FromQuery] string query, [FromQuery] bool anime,
                                                         [FromServices] JimakuClient client)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
            return BadRequest(new { Message = "Enter at least two characters." });

        try
        {
            var entries = await client.SearchAsync(query.Trim(), anime);
            return Ok(entries.Where(e => !e.Flags.Movie)
                             .Take(30)
                             .Select(e => new { e.Id, e.Name, e.JapaneseName, e.EnglishName, e.LastModified }));
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { Message = "Jimaku could not be reached.", Details = ex.Message });
        }
    }

    /// <summary>Entries with a recent upload that are not tracked yet, newest first.</summary>
    [HttpGet("jimaku-trackers/recent")]
    public async Task<IActionResult> GetRecentJimakuEntries([FromQuery] bool anime, [FromServices] JimakuClient client,
                                                            [FromServices] IMemoryCache cache)
    {
        List<JimakuEntry> entries;
        try
        {
            entries = (await cache.GetOrCreateAsync($"jimaku-recent:{anime}", async cacheEntry =>
            {
                cacheEntry.AbsoluteExpirationRelativeToNow = JimakuPreviewCacheDuration;
                return await client.RecentAsync(anime, DateTimeOffset.UtcNow.AddDays(-JimakuRecentDays));
            }))!;
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { Message = "Jimaku could not be reached.", Details = ex.Message });
        }

        var tracked = (await dbContext.JimakuSources.Select(s => s.JimakuEntryId).ToListAsync()).ToHashSet();

        return Ok(entries.Where(e => !e.Flags.Movie && !tracked.Contains(e.Id))
                         .OrderByDescending(e => e.LastModified)
                         .Take(300)
                         .Select(e => new { e.Id, e.Name, e.JapaneseName, e.EnglishName, e.LastModified }));
    }

    /// <summary>
    /// What a pattern would do to an entry today, without writing anything. The entry is cached so typing a
    /// pattern costs Jimaku one listing, not one per keystroke.
    /// </summary>
    [HttpGet("jimaku-trackers/preview")]
    public async Task<IActionResult> PreviewJimakuTracker([FromQuery] int jimakuId, [FromQuery] string? pattern, [FromQuery] int? deckId,
                                                          [FromQuery] int? tmdbSeason, [FromQuery] int? expectedEpisodes,
                                                          [FromServices] JimakuClient client,
                                                          [FromServices] EpisodeAiringLookup airingLookup,
                                                          [FromServices] IMemoryCache cache)
    {
        JimakuEntrySnapshot? snapshot;
        try
        {
            snapshot = await LoadJimakuEntryAsync(jimakuId, client, cache);
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { Message = "Jimaku could not be reached.", Details = ex.Message });
        }

        if (snapshot == null)
            return NotFound(new { Message = $"Jimaku has no entry {jimakuId}." });

        var entry = snapshot.Entry;
        var mediaType = entry.Flags.Anime ? MediaType.Anime : MediaType.Drama;

        var airing = await cache.GetOrCreateAsync($"jimaku-airing:{jimakuId}:{tmdbSeason}", async cacheEntry =>
        {
            cacheEntry.AbsoluteExpirationRelativeToNow = JimakuPreviewCacheDuration;
            if (entry.Flags.Anime && entry.AnilistId is { } anilistId)
                return (await airingLookup.AnilistAsync([anilistId])).GetValueOrDefault(anilistId);
            return entry.TmdbId != null ? await airingLookup.TmdbAsync(entry.TmdbId, tmdbSeason) : null;
        });

        var trackedDeckId = await dbContext.JimakuSources.Where(s => s.JimakuEntryId == jimakuId)
                                           .Select(s => (int?)s.DeckId)
                                           .FirstOrDefaultAsync();

        var providerUrl = entry.Flags.Anime && entry.AnilistId != null
            ? $"https://anilist.co/anime/{entry.AnilistId}"
            : entry.TmdbId != null ? $"https://www.themoviedb.org/tv/{entry.TmdbId.Replace("tv:", "")}" : null;
        var existingDeck = await dbContext.Decks.AsNoTracking()
                                          .Where(d => d.ParentDeckId == null && d.MediaType == mediaType &&
                                                      ((providerUrl != null && d.Links.Any(l => l.Url == providerUrl)) ||
                                                       (entry.JapaneseName != null && d.OriginalTitle == entry.JapaneseName)))
                                          .Select(d => new { d.DeckId, d.OriginalTitle })
                                          .FirstOrDefaultAsync();

        var targetDeck = deckId is { } id
            ? await dbContext.Decks.AsNoTracking()
                             .Where(d => d.DeckId == id)
                             .Select(d => new
                             {
                                 d.DeckId,
                                 d.OriginalTitle,
                                 d.MediaType,
                                 d.ParentDeckId,
                                 d.CharacterCount,
                                 Children = d.Children.Select(c => c.OriginalFileName).ToList()
                             })
                             .FirstOrDefaultAsync()
            : null;
        if (deckId != null && targetDeck == null)
            return NotFound(new { Message = $"There is no deck {deckId}." });

        var inDeck = targetDeck?.Children.OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
        var expected = expectedEpisodes ?? airing?.Total;

        var patternValid = JimakuFilePattern.IsValid(pattern);
        var alreadyIngested = patternValid && targetDeck != null
            ? JimakuSyncPlanner.MatchExisting(pattern!, snapshot.Files, inDeck, targetDeck.Children.Count)
            : [];
        var ingestedNames = alreadyIngested.Select(x => x.File.Name).ToHashSet(StringComparer.Ordinal);
        var ledger = alreadyIngested.Select(x => new JimakuLedgerEntry(x.File.Name, JimakuFileStatus.Imported, x.Episode)).ToList();
        var planned = patternValid ? JimakuSyncPlanner.Plan(pattern!, snapshot.Files, ledger, expected) : [];
        var decisions = planned.ToDictionary(d => d.File.Name, StringComparer.Ordinal);
        var regex = patternValid ? JimakuFilePattern.Compile(pattern!) : null;

        var position = targetDeck?.Children.Count ?? 0;
        var positionByName = planned.Where(d => d.Status == JimakuFileStatus.Queued)
                                    .ToDictionary(d => d.File.Name, _ => ++position, StringComparer.Ordinal);

        var files = snapshot.Files
                            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                            .Select(f =>
                            {
                                var decision = decisions.GetValueOrDefault(f.Name);
                                return new
                                {
                                    f.Name,
                                    f.Size,
                                    f.ModifiedAt,
                                    Matched = regex?.IsMatch(f.Name) == true,
                                    InDeck = ingestedNames.Contains(f.Name),
                                    Episode = decision?.Episode,
                                    Position = positionByName.TryGetValue(f.Name, out var p) ? p : (int?)null,
                                    Status = decision?.Status.ToString(),
                                    decision?.HoldReason
                                };
                            })
                            .ToList();

        return Ok(new
        {
            Entry = new
            {
                entry.Id,
                entry.Name,
                entry.JapaneseName,
                entry.EnglishName,
                MediaType = mediaType.ToString(),
                IsMovie = entry.Flags.Movie,
                entry.AnilistId,
                entry.TmdbId
            },
            Airing = airing,
            TrackedDeckId = trackedDeckId,
            ExistingDeck = existingDeck,
            TargetDeck = targetDeck == null
                ? null
                : new
                {
                    targetDeck.DeckId,
                    targetDeck.OriginalTitle,
                    MediaType = targetDeck.MediaType.ToString(),
                    IsSubdeck = targetDeck.ParentDeckId != null,
                    EpisodeCount = targetDeck.Children.Count,
                    HoldsTextDirectly = targetDeck.Children.Count == 0 && targetDeck.CharacterCount > 0,
                    NamedEpisodeCount = inDeck.Count
                },
            PatternValid = patternValid,
            Files = files
        });
    }

    [HttpPost("jimaku-trackers")]
    public async Task<IActionResult> AddJimakuTracker([FromBody] AddJimakuTrackerRequest model,
                                                      [FromServices] JimakuClient client,
                                                      [FromServices] IMemoryCache cache)
    {
        var pattern = model.Pattern?.Trim();
        if (!JimakuFilePattern.IsValid(pattern))
            return BadRequest(new { Message = "The pattern needs some text besides the wildcards." });
        if (pattern!.Length > 500)
            return BadRequest(new { Message = "The pattern is longer than 500 characters." });
        if (model.TmdbSeason is < 1 || model.ExpectedEpisodes is < 1)
            return BadRequest(new { Message = "The season and the episode count must be at least 1." });

        if (await dbContext.JimakuSources.AnyAsync(s => s.JimakuEntryId == model.JimakuId))
            return Conflict(new { Message = "This Jimaku entry is already tracked." });

        if (model.DeckId is not { } deckId)
        {
            var createJobId = backgroundJobs.Enqueue<JimakuSyncJob>(
                job => job.Create(model.JimakuId, pattern, model.TmdbSeason, model.ExpectedEpisodes));
            logger.LogInformation("Admin queued Jimaku tracker creation for entry {EntryId} (job {JobId})", model.JimakuId, createJobId);
            return Accepted(new { Message = "Deck creation queued.", JobId = createJobId });
        }

        var deck = await dbContext.Decks.Include(d => d.Children).FirstOrDefaultAsync(d => d.DeckId == deckId);
        if (deck == null)
            return NotFound(new { Message = $"There is no deck {deckId}." });
        if (deck.ParentDeckId != null || deck.MediaType is not (MediaType.Anime or MediaType.Drama))
            return BadRequest(new { Message = "Only a top-level anime or drama deck can be tracked." });
        if (deck.Children.Count == 0 && deck.CharacterCount > 0)
            return BadRequest(new { Message = "This deck holds its text directly. Move the text into a subdeck in the media editor first." });
        if (await dbContext.JimakuSources.AnyAsync(s => s.DeckId == deckId))
            return Conflict(new { Message = "This deck already tracks a Jimaku entry." });

        JimakuEntrySnapshot? snapshot;
        try
        {
            snapshot = await LoadJimakuEntryAsync(model.JimakuId, client, cache);
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { Message = "Jimaku could not be reached.", Details = ex.Message });
        }

        if (snapshot == null)
            return NotFound(new { Message = $"Jimaku has no entry {model.JimakuId}." });
        if (snapshot.Entry.Flags.Movie)
            return BadRequest(new { Message = "A film has no further episodes to track." });

        var childIdByFileName = deck.Children.Where(c => c.OriginalFileName != null)
                                    .GroupBy(c => c.OriginalFileName!, StringComparer.Ordinal)
                                    .ToDictionary(g => g.Key, g => g.First().DeckId, StringComparer.Ordinal);
        var alreadyIngested = JimakuSyncPlanner.MatchExisting(pattern, snapshot.Files,
                                                              childIdByFileName.Keys.ToHashSet(StringComparer.Ordinal),
                                                              deck.Children.Count);

        var now = DateTimeOffset.UtcNow;
        var source = new JimakuSource
        {
            DeckId = deckId,
            JimakuEntryId = snapshot.Entry.Id,
            FilePattern = pattern,
            AnilistId = snapshot.Entry.Flags.Anime ? snapshot.Entry.AnilistId : null,
            TmdbId = snapshot.Entry.TmdbId,
            TmdbSeason = model.TmdbSeason,
            ExpectedEpisodesOverride = model.ExpectedEpisodes,
            NextCheckAt = now,
            Files = alreadyIngested.Select(x => new JimakuTrackedFile
            {
                FileName = x.File.Name,
                Url = x.File.Url,
                Size = x.File.Size,
                SourceModifiedAt = x.File.ModifiedAt,
                EpisodeNumber = x.Episode,
                Status = JimakuFileStatus.Imported,
                ChildDeckId = childIdByFileName.TryGetValue(x.File.Name, out var childId) ? childId : null,
                FirstSeenAt = now
            }).ToList()
        };

        dbContext.JimakuSources.Add(source);
        await dbContext.SaveChangesAsync();

        var jobId = backgroundJobs.Enqueue<JimakuSyncJob>(job => job.SyncOne(deckId));
        logger.LogInformation("Admin attached Jimaku entry {EntryId} to deck {DeckId} (job {JobId})", source.JimakuEntryId, deckId, jobId);

        return Ok(new { source.DeckId, AlreadyIngested = alreadyIngested.Count, JobId = jobId });
    }

    [HttpGet("jimaku-trackers")]
    public async Task<IActionResult> GetJimakuTrackers()
    {
        var trackers = await dbContext.JimakuSources.AsNoTracking()
                                      .OrderByDescending(s => s.SyncEnabled)
                                      .ThenBy(s => s.Deck.OriginalTitle)
                                      .Select(s => new
                                      {
                                          s.DeckId,
                                          s.Deck.OriginalTitle,
                                          s.Deck.RomajiTitle,
                                          s.Deck.EnglishTitle,
                                          s.Deck.CoverName,
                                          MediaType = s.Deck.MediaType.ToString(),
                                          s.JimakuEntryId,
                                          s.FilePattern,
                                          s.AnilistId,
                                          s.TmdbId,
                                          s.TmdbSeason,
                                          s.ExpectedEpisodes,
                                          s.ExpectedEpisodesOverride,
                                          s.AiredEpisodes,
                                          s.FinishedAiring,
                                          Imported = s.Files.Count(f => f.Status == JimakuFileStatus.Imported),
                                          Held = s.Files.Count(f => f.Status == JimakuFileStatus.Held),
                                          Queued = s.Files.Count(f => f.Status == JimakuFileStatus.Queued),
                                          s.SyncEnabled,
                                          s.CompletedAt,
                                          s.LastCheckedAt,
                                          s.LastSyncedAt,
                                          s.NextCheckAt,
                                          s.ConsecutiveFailures,
                                          s.LastError
                                      })
                                      .ToListAsync();

        return Ok(trackers);
    }

    [HttpGet("jimaku-trackers/{deckId:int}/files")]
    public async Task<IActionResult> GetJimakuTrackerFiles(int deckId)
    {
        if (!await dbContext.JimakuSources.AnyAsync(s => s.DeckId == deckId))
            return NotFound();

        var files = await dbContext.JimakuTrackedFiles.AsNoTracking()
                                   .Where(f => f.SourceDeckId == deckId)
                                   .OrderBy(f => f.EpisodeNumber == null)
                                   .ThenBy(f => f.EpisodeNumber)
                                   .ThenBy(f => f.FileName)
                                   .Select(f => new
                                   {
                                       f.FileName,
                                       f.Size,
                                       f.SourceModifiedAt,
                                       f.EpisodeNumber,
                                       Status = f.Status.ToString(),
                                       f.HoldReason,
                                       f.ChildDeckId,
                                       f.FirstSeenAt
                                   })
                                   .ToListAsync();

        return Ok(files);
    }

    [HttpPost("jimaku-trackers/{deckId:int}/sync")]
    public async Task<IActionResult> SyncJimakuTrackerNow(int deckId)
    {
        if (!await dbContext.JimakuSources.AnyAsync(s => s.DeckId == deckId))
            return NotFound();

        var jobId = backgroundJobs.Enqueue<JimakuSyncJob>(job => job.SyncOne(deckId));
        return Accepted(new { Message = "Check queued.", JobId = jobId });
    }

    [HttpPost("jimaku-trackers/{deckId:int}/sync-enabled")]
    public async Task<IActionResult> SetJimakuTrackerSyncEnabled(int deckId, [FromBody] SetWebNovelSyncEnabledRequest model)
    {
        var source = await dbContext.JimakuSources.FirstOrDefaultAsync(s => s.DeckId == deckId);
        if (source == null)
            return NotFound();

        source.SyncEnabled = model.Enabled;
        if (model.Enabled)
        {
            source.CompletedAt = null;
            source.NextCheckAt = DateTimeOffset.UtcNow;
            source.ConsecutiveFailures = 0;
            source.LastError = null;
        }

        await dbContext.SaveChangesAsync();
        return Ok(new { source.DeckId, source.SyncEnabled });
    }

    [HttpPost("jimaku-trackers/{deckId:int}/settings")]
    public async Task<IActionResult> SetJimakuTrackerSettings(int deckId, [FromBody] SetJimakuTrackerSettingsRequest model)
    {
        var pattern = model.Pattern?.Trim();
        if (!JimakuFilePattern.IsValid(pattern) || pattern!.Length > 500)
            return BadRequest(new { Message = "The pattern needs some text besides the wildcards, in at most 500 characters." });
        if (model.TmdbSeason is < 1 || model.ExpectedEpisodes is < 1)
            return BadRequest(new { Message = "The season and the episode count must be at least 1." });

        var source = await dbContext.JimakuSources.Include(s => s.Files).FirstOrDefaultAsync(s => s.DeckId == deckId);
        if (source == null)
            return NotFound();

        if (!string.Equals(source.FilePattern, pattern, StringComparison.Ordinal))
        {
            var regex = JimakuFilePattern.Compile(pattern);
            var stale = source.Files.Where(f => f.Status != JimakuFileStatus.Imported && !regex.IsMatch(f.FileName)).ToList();
            dbContext.JimakuTrackedFiles.RemoveRange(stale);
            source.FilePattern = pattern;
        }

        source.TmdbSeason = model.TmdbSeason;
        source.ExpectedEpisodesOverride = model.ExpectedEpisodes;
        await dbContext.SaveChangesAsync();

        return Ok(new { source.DeckId, source.FilePattern, source.TmdbSeason, source.ExpectedEpisodesOverride });
    }

    /// <summary>Queued approves a held file for the next check; Ignored drops it for good. Imported rows are left alone.</summary>
    [HttpPost("jimaku-trackers/{deckId:int}/files/status")]
    public async Task<IActionResult> SetJimakuTrackedFileStatus(int deckId, [FromBody] SetJimakuTrackedFileStatusRequest model)
    {
        if (!Enum.TryParse<JimakuFileStatus>(model.Status, true, out var status) ||
            status is not (JimakuFileStatus.Queued or JimakuFileStatus.Ignored or JimakuFileStatus.Held))
            return BadRequest(new { Message = "A file can only be approved, ignored or put back on hold." });

        var file = await dbContext.JimakuTrackedFiles.FirstOrDefaultAsync(f => f.SourceDeckId == deckId && f.FileName == model.FileName);
        if (file == null)
            return NotFound();
        if (file.Status == JimakuFileStatus.Imported)
            return Conflict(new { Message = "This file is already in the deck. Delete its subdeck in the media editor instead." });
        if (status == JimakuFileStatus.Queued && JimakuFilePattern.IsArchive(file.FileName))
            return BadRequest(new { Message = "An archive cannot be ingested from here. Add its episodes in the media editor." });

        file.Status = status;
        if (status == JimakuFileStatus.Queued)
            file.HoldReason = null;
        await dbContext.SaveChangesAsync();

        return Ok(new { file.FileName, Status = file.Status.ToString() });
    }

    /// <summary>Stops tracking; the deck and its episodes stay.</summary>
    [HttpDelete("jimaku-trackers/{deckId:int}")]
    public async Task<IActionResult> DeleteJimakuTracker(int deckId)
    {
        var source = await dbContext.JimakuSources.FirstOrDefaultAsync(s => s.DeckId == deckId);
        if (source == null)
            return NotFound();

        dbContext.JimakuSources.Remove(source);
        await dbContext.SaveChangesAsync();
        logger.LogInformation("Admin stopped tracking Jimaku entry {EntryId} on deck {DeckId}", source.JimakuEntryId, deckId);
        return NoContent();
    }

    private static async Task<JimakuEntrySnapshot?> LoadJimakuEntryAsync(int jimakuId, JimakuClient client, IMemoryCache cache)
    {
        var key = $"jimaku-entry:{jimakuId}";
        if (cache.TryGetValue(key, out JimakuEntrySnapshot? cached))
            return cached;

        var entry = await client.GetEntryAsync(jimakuId);
        if (entry == null)
            return null;

        var files = await client.GetFilesAsync(jimakuId) ?? [];
        var snapshot = new JimakuEntrySnapshot(entry, files.Select(JimakuRemoteFile.From).ToList());
        cache.Set(key, snapshot, JimakuPreviewCacheDuration);
        return snapshot;
    }

    private sealed record JimakuEntrySnapshot(JimakuEntry Entry, List<JimakuRemoteFile> Files);
}

public class AddJimakuTrackerRequest
{
    public required int JimakuId { get; set; }
    public required string Pattern { get; set; }

    /// <summary>Existing deck to keep in sync; empty creates the deck from the matching files</summary>
    public int? DeckId { get; set; }

    public int? TmdbSeason { get; set; }
    public int? ExpectedEpisodes { get; set; }
}

public class SetJimakuTrackerSettingsRequest
{
    public required string Pattern { get; set; }
    public int? TmdbSeason { get; set; }
    public int? ExpectedEpisodes { get; set; }
}

public class SetJimakuTrackedFileStatusRequest
{
    public required string FileName { get; set; }
    public required string Status { get; set; }
}
