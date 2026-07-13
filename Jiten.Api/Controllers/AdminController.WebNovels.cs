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
        if (!WebNovelUrlParser.TryParse(url, out var provider, out var sourceId))
            return BadRequest(new { Message = "Not a supported webnovel URL." });

        if (!sourceResolver.IsSupported(provider))
            return BadRequest(new { Message = $"{provider} is not enabled yet." });

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
                FirstPublishedAt = info.FirstPublishedAt?.UtcDateTime,
                LastUpdatedAt = info.LastUpdatedAt?.UtcDateTime,
                EstimatedSubdecks = EstimateSubdecks(info.TotalCharacters)
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Webnovel preview failed for {Url}", url);
            return StatusCode(StatusCodes.Status502BadGateway,
                              new { Message = "Could not read this novel from the source.", Details = ex.Message });
        }
    }

    [HttpPost("add-webnovel-deck")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> AddWebNovelDeck([FromForm] AddWebNovelDeckRequest model,
                                                     [FromServices] IWebNovelSourceResolver sourceResolver)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (!WebNovelUrlParser.TryParse(model.Url, out var provider, out var sourceId))
            return BadRequest(new { Message = "Not a supported webnovel URL." });

        if (!sourceResolver.IsSupported(provider))
            return BadRequest(new { Message = $"{provider} is not enabled yet." });

        if (await dbContext.WebNovelSources.AnyAsync(s => s.Provider == provider && s.SourceId == sourceId))
            return Conflict(new { Message = "This novel is already tracked." });

        string? coverPath = null;
        if (model.CoverImage is { Length: > 0 })
        {
            var directory = Path.Join(config["StaticFilesPath"], "tmp", Guid.NewGuid().ToString());
            Directory.CreateDirectory(directory);

            coverPath = Path.Join(directory, "cover.jpg");
            await using var stream = new FileStream(coverPath, FileMode.Create);
            await model.CoverImage.CopyToAsync(stream);
        }

        // Fetching a long novel is 20+ minutes of polite requests, so it can never run in-request
        var jobId = backgroundJobs.Enqueue<WebNovelImportJob>(
            job => job.Import(provider, sourceId, coverPath, model.ChunkCharBudget));

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
                                          CharCount = g.Sum(c => c.CharCount)
                                      })
                                      .OrderBy(g => g.StartEpisode)
                                      .ToListAsync();

        return Ok(new
        {
            tracked.DeckId,
            Provider = tracked.Provider.ToString(),
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
        if (!await dbContext.WebNovelSources.AnyAsync(s => s.DeckId == deckId))
            return NotFound();

        var jobId = backgroundJobs.Enqueue<WebNovelFetchJob>(job => job.Sync(deckId));

        return Accepted(new { Message = "Sync queued.", JobId = jobId });
    }

    /// <summary>
    /// Re-fetches one subdeck's whole episode range, picking up revisions (改稿) to episodes already ingested.
    /// </summary>
    [HttpPost("webnovel/{deckId:int}/rebuild/{childDeckId:int}")]
    public async Task<IActionResult> RebuildWebNovelSubdeck(int deckId, int childDeckId)
    {
        if (!await dbContext.WebNovelChapters.AnyAsync(c => c.DeckId == deckId && c.ChildDeckId == childDeckId))
            return NotFound();

        var jobId = backgroundJobs.Enqueue<WebNovelFetchJob>(job => job.RebuildSubdeck(deckId, childDeckId));

        return Accepted(new { Message = "Rebuild queued.", JobId = jobId });
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

    private static int EstimateSubdecks(long totalCharacters) =>
        totalCharacters <= 0
            ? 1
            : (int)Math.Max(1, Math.Ceiling(totalCharacters / (double)SubdeckChunker.DefaultCharBudget));
}

public class SetWebNovelSyncEnabledRequest
{
    public bool Enabled { get; set; }
}
