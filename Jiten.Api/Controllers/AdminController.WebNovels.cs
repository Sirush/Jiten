using Hangfire;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Jobs;
using Jiten.Core.Data;
using Jiten.Core.Data.WebNovel;
using Jiten.Core.WebNovel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

public partial class AdminController
{
    /// <summary>
    /// Work metadata straight from the provider, so the admin can review it (and generate a cover from the
    /// title) before committing to an import.
    /// </summary>
    [HttpGet("webnovel-preview")]
    public async Task<IActionResult> PreviewWebNovel([FromQuery] string url,
                                                     [FromServices] IWebNovelSourceResolver sourceResolver)
    {
        var (work, error) = await ResolveWebNovelUrlAsync(url, sourceResolver);
        if (work == null)
            return error!;

        var (provider, sourceId) = (work.Provider, work.SourceId);
        var descriptor = WebNovelProviders.Get(provider);

        var existing = await dbContext.WebNovelSources
                                      .FirstOrDefaultAsync(s => s.Provider == provider && s.SourceId == sourceId);

        if (existing != null)
            return Conflict(new { Message = "This novel is already tracked.", DeckId = existing.DeckId });

        try
        {
            var info = await sourceResolver.Resolve(provider).GetInfoAsync(sourceId);

            // A same-title webnovel deck would make InsertDeck skip the insert, so the import would fail
            var titleConflict = await dbContext.Decks
                                               .AnyAsync(d => d.OriginalTitle == info.Title &&
                                                              d.MediaType == MediaType.WebNovel);

            return Ok(new
            {
                Provider = provider.ToString(),
                ProviderName = descriptor.DisplayName,
                info.SourceId,
                info.Url,
                info.Title,
                TitleConflict = titleConflict,
                info.Author,
                info.Synopsis,
                info.Genre,
                info.Keywords,
                info.EpisodeCount,
                info.TotalCharacters,
                info.IsCompleted,
                info.IsOnHiatus,
                info.IsOneShot,
                info.IsR15,
                info.IsAdultOnly,
                info.CoverUrl,
                FirstPublishedAt = info.FirstPublishedAt?.UtcDateTime,
                LastUpdatedAt = info.LastUpdatedAt?.UtcDateTime,
                EstimatedSubdecks = EstimateSubdecks(info.TotalCharacters),
                EstimatedFetchMinutes = Math.Max(1, (int)Math.Round(descriptor.Site.Politeness.EstimateSeconds(info.EpisodeCount + 1) / 60))
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Webnovel preview failed for {Url}", url);
            return StatusCode(StatusCodes.Status502BadGateway,
                              new { Message = "Could not read this novel from the source.", Details = ex.Message });
        }
    }

    /// <summary>
    /// Re-checks the InsertDeck dedupe key after the admin edits the title on the add page.
    /// </summary>
    [HttpGet("webnovel-title-conflict")]
    public async Task<IActionResult> CheckWebNovelTitleConflict([FromQuery] string? title)
    {
        var normalised = title?.Trim();
        if (string.IsNullOrEmpty(normalised))
            return Ok(new { Conflict = false });

        var conflict = await dbContext.Decks
                                      .AnyAsync(d => d.OriginalTitle == normalised && d.MediaType == MediaType.WebNovel);

        return Ok(new { Conflict = conflict });
    }

    [HttpPost("add-webnovel-deck")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> AddWebNovelDeck([FromForm] AddWebNovelDeckRequest model,
                                                     [FromServices] IWebNovelSourceResolver sourceResolver)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var (work, error) = await ResolveWebNovelUrlAsync(model.Url, sourceResolver);
        if (work == null)
            return error!;

        var (provider, sourceId) = (work.Provider, work.SourceId);

        if (await dbContext.WebNovelSources.AnyAsync(s => s.Provider == provider && s.SourceId == sourceId))
            return Conflict(new { Message = "This novel is already tracked." });

        string? coverPath = null;
        if (model.CoverImage is { Length: > 0 })
        {
            coverPath = NewTempCoverPath();
            await using var stream = new FileStream(coverPath, FileMode.Create);
            await model.CoverImage.CopyToAsync(stream);
        }
        else if (model.UseSourceCover)
        {
            coverPath = await DownloadSourceCoverAsync(sourceResolver.Resolve(provider), sourceId);
        }

        var titles = new WebNovelTitles
        {
            OriginalTitle = model.OriginalTitle,
            RomajiTitle = model.RomajiTitle,
            EnglishTitle = model.EnglishTitle
        };

        // Fetching a long novel is 20+ minutes of polite requests, so it can never run in-request
        var jobId = backgroundJobs.Enqueue<WebNovelImportJob>(
            job => job.Import(provider, sourceId, coverPath, model.ChunkCharBudget, titles));

        logger.LogInformation("Admin queued webnovel import for {Provider}/{SourceId} (job {JobId})",
                              provider, sourceId, jobId);

        return Accepted(new { Message = "Import queued.", JobId = jobId, Provider = provider.ToString(), SourceId = sourceId });
    }

    [HttpGet("webnovel/{deckId:int}")]
    public async Task<IActionResult> GetWebNovelSource(int deckId)
    {
        var tracked = await dbContext.WebNovelSources
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(s => s.DeckId == deckId);

        if (tracked == null)
            return NotFound();

        var subdecks = await dbContext.WebNovelChapters
                                      .AsNoTracking()
                                      .Where(c => c.DeckId == deckId)
                                      .GroupBy(c => c.ChildDeckId)
                                      .Select(g => new
                                      {
                                          ChildDeckId = g.Key,
                                          StartEpisode = g.Min(c => c.EpisodeNumber),
                                          EndEpisode = g.Max(c => c.EpisodeNumber),
                                          EpisodeCount = g.Count(),
                                          CharCount = g.Sum(c => c.CharCount),
                                          RevisedCount = g.Count(c => c.RevisedAtSource)
                                      })
                                      .OrderBy(g => g.StartEpisode)
                                      .ToListAsync();

        return Ok(new
        {
            tracked.DeckId,
            Provider = tracked.Provider.ToString(),
            ProviderName = WebNovelProviders.Get(tracked.Provider).DisplayName,
            tracked.SourceId,
            Url = WebNovelUrlParser.BuildWorkUrl(tracked.Provider, tracked.SourceId),
            tracked.LastEpisodeCount,
            tracked.LastSourceUpdate,
            tracked.LastSyncedAt,
            tracked.NextCheckAt,
            tracked.SyncEnabled,
            tracked.CompletedAtSource,
            tracked.OnHiatusAtSource,
            tracked.ConsecutiveFailures,
            tracked.LastError,
            tracked.ChunkCharBudget,
            tracked.PendingRevisionCount,
            Subdecks = subdecks
        });
    }

    [HttpPost("webnovel/{deckId:int}/sync")]
    public async Task<IActionResult> SyncWebNovelNow(int deckId)
    {
        if (await TrackedProviderAsync(deckId) is not { } provider)
            return NotFound();

        var jobId = backgroundJobs.Enqueue<WebNovelFetchJob>(job => job.Sync(provider, deckId));

        return Accepted(new { Message = "Sync queued.", JobId = jobId });
    }

    /// <summary>
    /// Re-fetches one subdeck's whole episode range, picking up revisions (改稿) to episodes already ingested.
    /// </summary>
    [HttpPost("webnovel/{deckId:int}/rebuild/{childDeckId:int}")]
    public async Task<IActionResult> RebuildWebNovelSubdeck(int deckId, int childDeckId)
    {
        var tracked = await dbContext.WebNovelSources
                                     .Where(s => s.DeckId == deckId && s.Chapters.Any(c => c.ChildDeckId == childDeckId))
                                     .Select(s => (WebNovelProvider?)s.Provider)
                                     .FirstOrDefaultAsync();
        if (tracked is not { } provider)
            return NotFound();

        var jobId = backgroundJobs.Enqueue<WebNovelFetchJob>(job => job.RebuildSubdeck(provider, deckId, childDeckId));

        return Accepted(new { Message = "Rebuild queued.", JobId = jobId });
    }

    /// <summary>
    /// Queues a rebuild for every subdeck holding at least one episode revised at the source.
    /// </summary>
    [HttpPost("webnovel/{deckId:int}/rebuild-revised")]
    public async Task<IActionResult> RebuildRevisedWebNovelSubdecks(int deckId)
    {
        if (await TrackedProviderAsync(deckId) is not { } provider)
            return NotFound();

        var childDeckIds = await dbContext.WebNovelChapters
                                          .Where(c => c.DeckId == deckId && c.RevisedAtSource)
                                          .Select(c => c.ChildDeckId)
                                          .Distinct()
                                          .OrderBy(id => id)
                                          .ToListAsync();

        foreach (var childDeckId in childDeckIds)
            backgroundJobs.Enqueue<WebNovelFetchJob>(job => job.RebuildSubdeck(provider, deckId, childDeckId));

        return Accepted(new { Message = $"{childDeckIds.Count} rebuild(s) queued.", SubdeckCount = childDeckIds.Count });
    }

    [HttpPost("webnovel/{deckId:int}/sync-enabled")]
    public async Task<IActionResult> SetWebNovelSyncEnabled(int deckId, [FromBody] SetWebNovelSyncEnabledRequest model)
    {
        var tracked = await dbContext.WebNovelSources.FirstOrDefaultAsync(s => s.DeckId == deckId);
        if (tracked == null)
            return NotFound();

        tracked.SyncEnabled = model.Enabled;

        // Coming off a freeze shouldn't wait out a backoff window
        if (model.Enabled)
        {
            tracked.NextCheckAt = DateTimeOffset.UtcNow;
            tracked.ConsecutiveFailures = 0;
            tracked.LastError = null;
        }

        await dbContext.SaveChangesAsync();

        return Ok(new { tracked.DeckId, tracked.SyncEnabled });
    }

    private async Task<WebNovelProvider?> TrackedProviderAsync(int deckId) =>
        await dbContext.WebNovelSources
                       .Where(s => s.DeckId == deckId)
                       .Select(s => (WebNovelProvider?)s.Provider)
                       .FirstOrDefaultAsync();

    /// <summary>
    /// Direct work URLs resolve offline; an indirect page (a Kakuyomu book page) is looked up at the source.
    /// </summary>
    private async Task<(WebNovelWorkRef? Work, IActionResult? Error)> ResolveWebNovelUrlAsync(
        string url, IWebNovelSourceResolver sourceResolver)
    {
        try
        {
            if (await sourceResolver.ResolveUrlAsync(url) is not { } work)
                return (null, BadRequest(new { Message = "Not a supported webnovel URL." }));

            if (!sourceResolver.IsSupported(work.Provider))
                return (null, BadRequest(new { Message = $"{WebNovelProviders.Get(work.Provider).DisplayName} is not enabled yet." }));

            return (work, null);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return (null, BadRequest(new { ex.Message }));
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Webnovel link lookup failed for {Url}", url);
            return (null, StatusCode(StatusCodes.Status502BadGateway,
                                     new { Message = "Could not read this page from the source.", Details = ex.Message }));
        }
    }

    private string NewTempCoverPath()
    {
        var directory = Path.Join(config["StaticFilesPath"], "tmp", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        return Path.Join(directory, "cover.jpg");
    }

    /// <summary>
    /// Only ever fetches the URL the source itself reports, never one from the request. Null when the work has no
    /// cover or the download fails; the import then goes ahead without one.
    /// </summary>
    private async Task<string?> DownloadSourceCoverAsync(IWebNovelSource source, string sourceId)
    {
        try
        {
            var info = await source.GetInfoAsync(sourceId);
            if (string.IsNullOrEmpty(info.CoverUrl))
                return null;

            var bytes = await httpClient.GetByteArrayAsync(info.CoverUrl);
            var coverPath = NewTempCoverPath();
            await System.IO.File.WriteAllBytesAsync(coverPath, bytes);
            return coverPath;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not download the source cover for {Provider}/{SourceId}", source.Provider, sourceId);
            return null;
        }
    }

    private static int EstimateSubdecks(long totalCharacters) =>
        totalCharacters <= 0
            ? 1
            : (int)Math.Max(1, Math.Ceiling(totalCharacters / (double)SubdeckChunker.DefaultCharBudget));
}

public class SetWebNovelSyncEnabledRequest
{
    public bool Enabled { get; set; }
}
