using Hangfire;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Core.Data.YouTube;
using Jiten.Core.YouTube;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Jiten.Api.Controllers;

public partial class AdminController
{
    /// <summary>
    /// Resolves a pasted channel or playlist URL to its canonical id (one cheap listing call) so the admin can
    /// confirm the target before the full enumeration runs as a job.
    /// </summary>
    [HttpGet("youtube-preview")]
    public async Task<IActionResult> PreviewYouTubeSource([FromQuery] string url, [FromServices] YtDlpClient client,
                                                          [FromServices] YouTubeSourceRegistrar registrar)
    {
        if (!YouTubeUrlParser.TryParse(url, out _, out _, out _))
            return BadRequest(new { Message = "Not a YouTube channel or playlist URL." });

        try
        {
            var info = await client.ResolveSourceAsync(url, maxVideos: 1);
            var conflict = await registrar.CheckConflictsAsync(info);

            // Google's avatar host refuses hotlinking from the dashboard, so the preview carries the bytes
            var coverBytes = await client.DownloadImageAsync(info.CoverUrl);
            var coverDataUrl = coverBytes is { Length: > 0 } ? $"data:image/jpeg;base64,{Convert.ToBase64String(coverBytes)}" : null;

            return Ok(new
            {
                Kind = info.Kind.ToString(),
                info.SourceId,
                info.Title,
                info.ChannelName,
                info.ChannelId,
                info.Description,
                info.CoverUrl,
                CoverDataUrl = coverDataUrl,
                Url = YouTubeUrlParser.SourceUrl(info.Kind, info.SourceId),
                Conflict = conflict
            });
        }
        catch (YtDlpBlockedException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                              new { Message = "YouTube is bot-checking the server's IP. Add the source from the home CLI instead.", Details = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "YouTube preview failed for {Url}", url);
            return StatusCode(StatusCodes.Status502BadGateway,
                              new { Message = "Could not read this source.", Details = ex.Message });
        }
    }

    [HttpPost("add-youtube-source")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> AddYouTubeSource([FromForm] AddYouTubeSourceRequest model)
    {
        if (!YouTubeUrlParser.TryParse(model.Url, out _, out _, out _))
            return BadRequest(new { Message = "Not a YouTube channel or playlist URL." });

        if (!TryValidateRegex(model.TitleInclude, out var error) || !TryValidateRegex(model.TitleExclude, out error))
            return BadRequest(new { Message = error });

        DateOnly? releaseDate = null;
        if (!string.IsNullOrWhiteSpace(model.ReleaseDate))
        {
            if (!DateOnly.TryParse(model.ReleaseDate, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return BadRequest(new { Message = "Release date must be yyyy-MM-dd." });
            releaseDate = parsed;
        }

        string? coverPath = null;
        if (model.CoverImage is { Length: > 0 })
        {
            var directory = Path.Join(config["StaticFilesPath"], "tmp", Guid.NewGuid().ToString());
            Directory.CreateDirectory(directory);
            coverPath = Path.Join(directory, "cover.jpg");
            await using var stream = new FileStream(coverPath, FileMode.Create);
            await model.CoverImage.CopyToAsync(stream);
        }

        var filters = new YouTubeSourceFilters(Normalise(model.TitleInclude), Normalise(model.TitleExclude),
                                               model.MinRuntimeSeconds, model.MaxRuntimeSeconds);
        var titles = new YouTubeSourceTitles
        {
            OriginalTitle = Normalise(model.OriginalTitle),
            RomajiTitle = Normalise(model.RomajiTitle),
            EnglishTitle = Normalise(model.EnglishTitle),
            ReleaseDate = releaseDate
        };
        var url = model.Url.Trim();

        // The server cannot list a channel from a bot-checked IP: park the request and let the home CLI
        // resolve it. Everything the admin typed here is applied when the CLI completes it.
        if (model.ViaCli)
        {
            var registration = new YouTubeRegistration
            {
                Url = url,
                OriginalTitle = titles.OriginalTitle,
                RomajiTitle = titles.RomajiTitle,
                EnglishTitle = titles.EnglishTitle,
                ReleaseDate = releaseDate,
                CoverPath = coverPath,
                TitleFilterInclude = filters.TitleInclude,
                TitleFilterExclude = filters.TitleExclude,
                MinRuntimeSeconds = filters.MinRuntimeSeconds,
                MaxRuntimeSeconds = filters.MaxRuntimeSeconds,
                CreatedAt = DateTimeOffset.UtcNow
            };
            dbContext.YouTubeRegistrations.Add(registration);
            await dbContext.SaveChangesAsync();

            return Accepted(new { Message = "Waiting for the CLI.", RegistrationId = registration.Id, Command = RegisterCommand(registration.Id) });
        }

        var jobId = backgroundJobs.Enqueue<YouTubeImportJob>(job => job.AddSource(url, filters, titles, coverPath));

        logger.LogInformation("Admin queued YouTube source registration for {Url} (job {JobId})", model.Url, jobId);

        return Accepted(new { Message = "Registration queued.", JobId = jobId });
    }

    /// <summary>Open and recently completed CLI registrations, with the command to paste for each open one.</summary>
    [HttpGet("youtube-registrations")]
    public async Task<IActionResult> GetYouTubeRegistrations([FromServices] IOptions<YouTubeOptions> youTubeOptions)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        var registrations = await dbContext.YouTubeRegistrations
                                           .AsNoTracking()
                                           .Where(r => r.CompletedAt == null || r.CompletedAt > cutoff)
                                           .OrderByDescending(r => r.CreatedAt)
                                           .Take(50)
                                           .Select(r => new
                                           {
                                               r.Id,
                                               r.Url,
                                               r.OriginalTitle,
                                               r.CreatedAt,
                                               r.CompletedAt,
                                               r.DeckId,
                                               r.LastError,
                                               Command = RegisterCommand(r.Id)
                                           })
                                           .ToListAsync();

        return Ok(new
        {
            ServerFetch = youTubeOptions.Value.ServerFetch,
            IngestConfigured = !string.IsNullOrEmpty(config["YouTube:IngestKey"]),
            Registrations = registrations
        });
    }

    [HttpDelete("youtube-registrations/{id:int}")]
    public async Task<IActionResult> CancelYouTubeRegistration(int id)
    {
        var registration = await dbContext.YouTubeRegistrations.FirstOrDefaultAsync(r => r.Id == id);
        if (registration == null)
            return NotFound();
        if (registration.CompletedAt != null)
            return Conflict(new { Message = "Already completed." });

        dbContext.YouTubeRegistrations.Remove(registration);
        await dbContext.SaveChangesAsync();
        return NoContent();
    }

    private static string RegisterCommand(int id) => $"dotnet run --project Jiten.Cli -- --yt-register {id}";

    [HttpGet("youtube/{deckId:int}")]
    public async Task<IActionResult> GetYouTubeSource(int deckId, [FromServices] IOptions<YouTubeOptions> youTubeOptions,
                                                      [FromQuery] string? status, [FromQuery] int limit = 500)
    {
        var tracked = await dbContext.YouTubeSources.AsNoTracking().FirstOrDefaultAsync(s => s.DeckId == deckId);
        if (tracked == null)
            return NotFound();

        var ledger = dbContext.YouTubeVideos.AsNoTracking().Where(v => v.SourceDeckId == deckId);

        var statusCounts = await ledger.GroupBy(v => v.Status)
                                       .Select(g => new { Status = g.Key, Count = g.Count() })
                                       .ToListAsync();

        var reasons = await ledger.Where(v => v.SkipReason != null)
                                  .Select(v => v.SkipReason!)
                                  .ToListAsync();
        var reasonCounts = reasons.GroupBy(Prefix)
                                  .Select(g => new { Prefix = g.Key, Count = g.Count() })
                                  .OrderByDescending(g => g.Count)
                                  .ToList();

        var filtered = ledger;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<YouTubeVideoStatus>(status, true, out var parsedStatus))
            filtered = filtered.Where(v => v.Status == parsedStatus);

        var videos = await filtered.OrderByDescending(v => v.UploadedAt)
                                   .ThenBy(v => v.VideoId)
                                   .Take(Math.Clamp(limit, 1, 2000))
                                   .Select(v => new
                                   {
                                       v.VideoId,
                                       v.ChildDeckId,
                                       Status = v.Status.ToString(),
                                       v.Title,
                                       v.UploadedAt,
                                       v.RuntimeSeconds,
                                       v.PlayableInEmbed,
                                       v.SkipReason,
                                       v.LastCheckedAt
                                   })
                                   .ToListAsync();

        return Ok(new
        {
            tracked.DeckId,
            SourceKind = tracked.SourceKind.ToString(),
            tracked.SourceId,
            tracked.ChannelName,
            tracked.ChannelId,
            Url = YouTubeUrlParser.SourceUrl(tracked.SourceKind, tracked.SourceId),
            tracked.TitleFilterInclude,
            tracked.TitleFilterExclude,
            tracked.MinRuntimeSeconds,
            tracked.MaxRuntimeSeconds,
            tracked.LastSourceUpdate,
            tracked.LastSyncedAt,
            tracked.NextCheckAt,
            tracked.SyncEnabled,
            tracked.CheckIntervalDays,
            tracked.ConsecutiveFailures,
            tracked.LastError,
            ServerFetch = youTubeOptions.Value.ServerFetch,
            StatusCounts = statusCounts.ToDictionary(s => s.Status.ToString(), s => s.Count),
            ReasonCounts = reasonCounts,
            Videos = videos
        });
    }

    /// <summary>Feed check now; drains too when server fetching is on.</summary>
    [HttpPost("youtube/{deckId:int}/sync")]
    public async Task<IActionResult> SyncYouTubeNow(int deckId)
    {
        if (!await dbContext.YouTubeSources.AnyAsync(s => s.DeckId == deckId))
            return NotFound();

        var jobId = backgroundJobs.Enqueue<YouTubeSyncSweepJob>(job => job.SyncOne(deckId));
        return Accepted(new { Message = "Sync queued.", JobId = jobId });
    }

    /// <summary>Server-side drain of pending rows regardless of the ServerFetch setting, for proxy trials.</summary>
    [HttpPost("youtube/{deckId:int}/drain")]
    public async Task<IActionResult> DrainYouTubeNow(int deckId)
    {
        if (!await dbContext.YouTubeSources.AnyAsync(s => s.DeckId == deckId))
            return NotFound();

        var jobId = backgroundJobs.Enqueue<YouTubeFetchJob>(job => job.Drain(deckId));
        return Accepted(new { Message = "Drain queued.", JobId = jobId });
    }

    /// <summary>Full re-enumeration, for sources whose ledger predates the feed or was seeded partially.</summary>
    [HttpPost("youtube/{deckId:int}/bootstrap")]
    public async Task<IActionResult> BootstrapYouTubeSource(int deckId)
    {
        if (!await dbContext.YouTubeSources.AnyAsync(s => s.DeckId == deckId))
            return NotFound();

        var jobId = backgroundJobs.Enqueue<YouTubeImportJob>(job => job.Bootstrap(deckId));
        return Accepted(new { Message = "Bootstrap queued.", JobId = jobId });
    }

    /// <summary>Parses fetched subdecks now instead of waiting for the hourly pass.</summary>
    [HttpPost("youtube/{deckId:int}/import")]
    public async Task<IActionResult> ImportYouTubeFetched(int deckId)
    {
        if (!await dbContext.YouTubeSources.AnyAsync(s => s.DeckId == deckId))
            return NotFound();

        var jobId = backgroundJobs.Enqueue<YouTubeImportJob>(job => job.ImportFetched(deckId));
        return Accepted(new { Message = "Import queued.", JobId = jobId });
    }

    /// <summary>Renumbers the children by upload date; for sources imported before the import job did this itself.</summary>
    [HttpPost("youtube/{deckId:int}/reorder")]
    public async Task<IActionResult> ReorderYouTubeChildren(int deckId)
    {
        if (!await dbContext.YouTubeSources.AnyAsync(s => s.DeckId == deckId))
            return NotFound();

        var children = await dbContext.Decks.Where(d => d.ParentDeckId == deckId).ToListAsync();
        YouTubeImportJob.ReorderByUploadDate(children);
        await dbContext.SaveChangesAsync();
        return Ok(new { Reordered = children.Count });
    }

    [HttpPost("youtube/{deckId:int}/sync-enabled")]
    public async Task<IActionResult> SetYouTubeSyncEnabled(int deckId, [FromBody] SetWebNovelSyncEnabledRequest model)
    {
        var tracked = await dbContext.YouTubeSources.FirstOrDefaultAsync(s => s.DeckId == deckId);
        if (tracked == null)
            return NotFound();

        tracked.SyncEnabled = model.Enabled;
        if (model.Enabled)
        {
            tracked.NextCheckAt = DateTimeOffset.UtcNow;
            tracked.ConsecutiveFailures = 0;
            tracked.LastError = null;
        }

        await dbContext.SaveChangesAsync();
        return Ok(new { tracked.DeckId, tracked.SyncEnabled });
    }

    /// <summary>Null restores the automatic rule (weekly, monthly once quiet).</summary>
    [HttpPost("youtube/{deckId:int}/check-interval")]
    public async Task<IActionResult> SetYouTubeCheckInterval(int deckId, [FromBody] SetYouTubeCheckIntervalRequest model)
    {
        if (model.Days is < 1 or > 365)
            return BadRequest(new { Message = "The interval must be between 1 and 365 days, or empty for automatic." });

        var tracked = await dbContext.YouTubeSources.FirstOrDefaultAsync(s => s.DeckId == deckId);
        if (tracked == null)
            return NotFound();

        tracked.CheckIntervalDays = model.Days;
        var next = YouTubeSchedule.NextCheck(tracked);
        if (next < tracked.NextCheckAt)
            tracked.NextCheckAt = next;

        await dbContext.SaveChangesAsync();
        return Ok(new { tracked.DeckId, tracked.CheckIntervalDays, tracked.NextCheckAt });
    }

    [HttpPost("youtube/{deckId:int}/filters")]
    public async Task<IActionResult> SetYouTubeTitleFilters(int deckId, [FromBody] SetYouTubeFiltersRequest model)
    {
        var tracked = await dbContext.YouTubeSources.FirstOrDefaultAsync(s => s.DeckId == deckId);
        if (tracked == null)
            return NotFound();

        if (!TryValidateRegex(model.TitleInclude, out var error) || !TryValidateRegex(model.TitleExclude, out error))
            return BadRequest(new { Message = error });

        if (model.MinRuntimeSeconds is < 0 || model.MaxRuntimeSeconds is < 0 ||
            (model.MinRuntimeSeconds is > 0 && model.MaxRuntimeSeconds is > 0 && model.MinRuntimeSeconds > model.MaxRuntimeSeconds))
            return BadRequest(new { Message = "The runtime bounds must be positive and the minimum below the maximum." });

        tracked.TitleFilterInclude = Normalise(model.TitleInclude);
        tracked.TitleFilterExclude = Normalise(model.TitleExclude);
        tracked.MinRuntimeSeconds = model.MinRuntimeSeconds is > 0 ? model.MinRuntimeSeconds : null;
        tracked.MaxRuntimeSeconds = model.MaxRuntimeSeconds is > 0 ? model.MaxRuntimeSeconds : null;
        await dbContext.SaveChangesAsync();

        return Ok(new { tracked.DeckId, tracked.TitleFilterInclude, tracked.TitleFilterExclude, tracked.MinRuntimeSeconds, tracked.MaxRuntimeSeconds });
    }

    /// <summary>Pending re-checks a video on the next drain; Excluded blacklists it. Imported rows are left alone.</summary>
    [HttpPost("youtube/{deckId:int}/videos/{videoId}/status")]
    public async Task<IActionResult> SetYouTubeVideoStatus(int deckId, string videoId, [FromBody] SetYouTubeVideoStatusRequest model)
    {
        if (!Enum.TryParse<YouTubeVideoStatus>(model.Status, true, out var status) ||
            status is not (YouTubeVideoStatus.Pending or YouTubeVideoStatus.Excluded))
            return BadRequest(new { Message = "Only Pending and Excluded can be set by hand." });

        var video = await dbContext.YouTubeVideos.FirstOrDefaultAsync(v => v.SourceDeckId == deckId && v.VideoId == videoId);
        if (video == null)
            return NotFound();

        if (video.Status == YouTubeVideoStatus.Imported || video.ChildDeckId != null)
            return Conflict(new { Message = "This video is already imported. Delete its subdeck first." });

        video.Status = status;
        video.SkipReason = status == YouTubeVideoStatus.Excluded ? "excluded: by admin" : null;
        await dbContext.SaveChangesAsync();

        return Ok(new { video.VideoId, Status = video.Status.ToString() });
    }

    /// <summary>Bulk re-check by skip-reason prefix, e.g. every asr-only video after a policy change.</summary>
    [HttpPost("youtube/{deckId:int}/recheck")]
    public async Task<IActionResult> RecheckYouTubeVideos(int deckId, [FromBody] RecheckYouTubeVideosRequest model)
    {
        var prefix = model.Prefix?.Trim();
        if (string.IsNullOrEmpty(prefix))
            return BadRequest(new { Message = "A skip-reason prefix is required." });

        var videos = await dbContext.YouTubeVideos
                                    .Where(v => v.SourceDeckId == deckId && v.ChildDeckId == null &&
                                                v.Status != YouTubeVideoStatus.Excluded && v.Status != YouTubeVideoStatus.Pending &&
                                                v.SkipReason != null && v.SkipReason.StartsWith(prefix))
                                    .ToListAsync();

        foreach (var video in videos)
            video.Status = YouTubeVideoStatus.Pending;

        await dbContext.SaveChangesAsync();
        return Ok(new { Requeued = videos.Count });
    }

    private static string Prefix(string reason)
    {
        var colon = reason.IndexOf(':');
        return colon > 0 ? reason[..colon] : reason;
    }

    private static string? Normalise(string? pattern) => string.IsNullOrWhiteSpace(pattern) ? null : pattern.Trim();

    private static bool TryValidateRegex(string? pattern, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(pattern))
            return true;

        try
        {
            _ = new System.Text.RegularExpressions.Regex(pattern);
            return true;
        }
        catch (ArgumentException ex)
        {
            error = $"Invalid regex '{pattern}': {ex.Message}";
            return false;
        }
    }
}

public class AddYouTubeSourceRequest
{
    public required string Url { get; set; }
    public string? OriginalTitle { get; set; }
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }

    /// <summary>yyyy-MM-dd; empty = the oldest listed upload</summary>
    public string? ReleaseDate { get; set; }

    public IFormFile? CoverImage { get; set; }

    /// <summary>Park the request for the home CLI instead of listing the channel on the server</summary>
    public bool ViaCli { get; set; }

    public string? TitleInclude { get; set; }
    public string? TitleExclude { get; set; }
    public int? MinRuntimeSeconds { get; set; }
    public int? MaxRuntimeSeconds { get; set; }
}

public class SetYouTubeCheckIntervalRequest
{
    public int? Days { get; set; }
}

public class SetYouTubeFiltersRequest
{
    public string? TitleInclude { get; set; }
    public string? TitleExclude { get; set; }
    public int? MinRuntimeSeconds { get; set; }
    public int? MaxRuntimeSeconds { get; set; }
}

public class SetYouTubeVideoStatusRequest
{
    public string? Status { get; set; }
}

public class RecheckYouTubeVideosRequest
{
    public string? Prefix { get; set; }
}
