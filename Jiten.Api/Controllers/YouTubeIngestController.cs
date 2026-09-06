using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Jiten.Api.Jobs;
using Jiten.Core;
using Jiten.Core.Data.YouTube;
using Jiten.Core.YouTube;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

/// <summary>
/// Gate for the home CLI: a shared secret from config (YouTube:IngestKey) in the X-Ingest-Key header. Nothing
/// here is reachable when the key is unset.
/// </summary>
public class IngestKeyFilter(IConfiguration config) : IAuthorizationFilter
{
    public const string HeaderName = "X-Ingest-Key";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var expected = config["YouTube:IngestKey"];
        var provided = context.HttpContext.Request.Headers[HeaderName].FirstOrDefault();

        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(provided)))
        {
            context.Result = new UnauthorizedResult();
        }
    }
}

/// <summary>
/// The database side of a drain run from a machine that can reach YouTube but not Postgres. The CLI lists
/// pending videos, fetches them with its own yt-dlp, and posts either a skip verdict or the cleaned track.
///
/// The key is additive only: it can register a new source and move Pending rows forward. It cannot delete,
/// rename or touch anything already Fetched or Imported, and it never reaches the admin surface.
/// </summary>
[ApiController]
[Route("api/ingest/youtube")]
[ApiExplorerSettings(IgnoreApi = true)]
[AllowAnonymous]
[EnableRateLimiting("ingest")]
[ServiceFilter(typeof(IngestKeyFilter))]
public class YouTubeIngestController(
    IDbContextFactory<JitenDbContext> contextFactory,
    YtDlpClient client,
    YouTubeSourceRegistrar registrar,
    YouTubeDrainService drainService,
    IBackgroundJobClient backgroundJobs,
    IConfiguration config,
    ILogger<YouTubeIngestController> logger) : ControllerBase
{
    /// <summary>Pending rows plus each source's filters, so the CLI can skip by title and length before fetching.</summary>
    [HttpGet("pending")]
    public async Task<IActionResult> Pending([FromQuery] int? deckId, [FromQuery] int max = 500)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var sources = await context.YouTubeSources
                                   .AsNoTracking()
                                   .Where(s => deckId == null || s.DeckId == deckId)
                                   .Where(s => context.YouTubeVideos.Any(v => v.SourceDeckId == s.DeckId && v.Status == YouTubeVideoStatus.Pending))
                                   .ToListAsync();

        var result = new List<object>();
        foreach (var source in sources)
        {
            var videos = await context.YouTubeVideos
                                      .AsNoTracking()
                                      .Where(v => v.SourceDeckId == source.DeckId && v.Status == YouTubeVideoStatus.Pending)
                                      .OrderBy(v => v.LastCheckedAt)
                                      .Take(Math.Clamp(max, 1, 5000))
                                      .Select(v => new { v.VideoId, v.Title, v.RuntimeSeconds })
                                      .ToListAsync();

            result.Add(new
            {
                source.DeckId,
                source.ChannelName,
                Filters = YouTubeSourceFilters.From(source),
                Videos = videos
            });
        }

        return Ok(result);
    }

    /// <summary>Registers a source the CLI resolved and listed with its own yt-dlp.</summary>
    [HttpPost("sources")]
    public async Task<IActionResult> RegisterSource([FromBody] RegisterYouTubeSourceRequest model)
    {
        var source = model.Source;
        var validation = ValidateSource(source);
        if (validation != null)
            return BadRequest(new { Message = validation });

        if (model.Titles is { OriginalTitle.Length: > 500 } or { RomajiTitle.Length: > 500 } or { EnglishTitle.Length: > 500 })
            return BadRequest(new { Message = "Titles are at most 500 characters." });

        var conflict = await registrar.CheckConflictsAsync(source, model.Titles?.OriginalTitle);
        if (conflict != null)
            return Conflict(new { Message = conflict });

        var cover = await client.DownloadImageAsync(model.Source.CoverUrl) ?? [];
        var deckId = await registrar.RegisterAsync(model.Source, model.Filters ?? YouTubeSourceFilters.None, cover, model.Titles);

        logger.LogInformation("YouTubeIngest: registered {Kind} {SourceId} as deck {DeckId} with {Count} videos",
                              model.Source.Kind, model.Source.SourceId, deckId, model.Source.Videos.Count);

        return Ok(new { DeckId = deckId, Pending = model.Source.Videos.Count });
    }

    /// <summary>What the CLI needs to resolve a dashboard registration: the URL. Settings stay server-side.</summary>
    [HttpGet("registrations/{id:int}")]
    public async Task<IActionResult> GetRegistration(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var registration = await context.YouTubeRegistrations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        if (registration == null)
            return NotFound();
        if (registration.CompletedAt != null)
            return Conflict(new { Message = "Already completed.", registration.DeckId });

        return Ok(new { registration.Id, registration.Url, HasReleaseDate = registration.ReleaseDate != null });
    }

    /// <summary>
    /// Completes a dashboard registration with the listing the CLI resolved. Titles, date, cover and filters
    /// come from what the admin typed, never from the payload, so the key cannot rename or restyle a deck.
    /// </summary>
    [HttpPost("registrations/{id:int}/complete")]
    public async Task<IActionResult> CompleteRegistration(int id, [FromBody] RegisterYouTubeSourceRequest model)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var registration = await context.YouTubeRegistrations.FirstOrDefaultAsync(r => r.Id == id);
        if (registration == null)
            return NotFound();
        if (registration.CompletedAt != null)
            return Conflict(new { Message = "Already completed.", registration.DeckId });

        var validation = ValidateSource(model.Source);
        if (validation != null)
            return BadRequest(new { Message = validation });

        var titles = new YouTubeSourceTitles
        {
            OriginalTitle = registration.OriginalTitle,
            RomajiTitle = registration.RomajiTitle,
            EnglishTitle = registration.EnglishTitle,
            ReleaseDate = registration.ReleaseDate
        };
        var filters = new YouTubeSourceFilters(registration.TitleFilterInclude, registration.TitleFilterExclude,
                                               registration.MinRuntimeSeconds, registration.MaxRuntimeSeconds);

        var conflict = await registrar.CheckConflictsAsync(model.Source, titles.OriginalTitle);
        if (conflict != null)
        {
            registration.LastError = conflict;
            await context.SaveChangesAsync();
            return Conflict(new { Message = conflict });
        }

        var cover = !string.IsNullOrEmpty(registration.CoverPath) && System.IO.File.Exists(registration.CoverPath)
            ? await System.IO.File.ReadAllBytesAsync(registration.CoverPath)
            : await client.DownloadImageAsync(model.Source.CoverUrl) ?? [];

        var deckId = await registrar.RegisterAsync(model.Source, filters, cover, titles);

        registration.CompletedAt = DateTimeOffset.UtcNow;
        registration.DeckId = deckId;
        registration.LastError = null;
        await context.SaveChangesAsync();
        YouTubeSourceRegistrar.DeleteStagedCover(registration.CoverPath);

        logger.LogInformation("YouTubeIngest: registration {Id} completed as deck {DeckId} with {Count} videos",
                              id, deckId, model.Source.Videos.Count);

        return Ok(new { DeckId = deckId, Pending = model.Source.Videos.Count });
    }

    [HttpPost("videos/{deckId:int}/{videoId}/skip")]
    public async Task<IActionResult> Skip(int deckId, string videoId, [FromBody] SkipYouTubeVideoRequest model)
    {
        if (!YouTubeUrlParser.IsVideoId(videoId))
            return BadRequest(new { Message = "Not a video id." });

        // Excluded is an admin decision; a drain can only report what YouTube said
        if (!Enum.TryParse<YouTubeVideoStatus>(model.Status, true, out var status) ||
            status is not (YouTubeVideoStatus.NoManualSubs or YouTubeVideoStatus.FilteredOut or YouTubeVideoStatus.Dead))
            return BadRequest(new { Message = "Not a fetch verdict." });

        if (model.SkipReason is { Length: > 500 })
            return BadRequest(new { Message = "Skip reason too long." });
        SanitiseInfo(model.Info, videoId);

        await using var context = await contextFactory.CreateDbContextAsync();
        var (source, video) = await LoadPendingAsync(context, deckId, videoId);
        if (video == null)
            return NotFound();

        var outcome = new YouTubeFetchOutcome { Status = status, SkipReason = model.SkipReason, Info = model.Info };
        await drainService.ApplyOutcomeAsync(context, source!, video, outcome);

        return Ok(new { video.VideoId, Status = video.Status.ToString() });
    }

    /// <summary>Cleaned srt plus the yt-dlp metadata; the server extracts text, computes speech stats and fetches the thumbnail.</summary>
    [HttpPost("videos/{deckId:int}/{videoId}/fetched")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> Fetched(int deckId, string videoId, [FromForm] FetchedYouTubeVideoRequest model)
    {
        if (!YouTubeUrlParser.IsVideoId(videoId))
            return BadRequest(new { Message = "Not a video id." });

        if (model.Subtitles is not { Length: > 0 })
            return BadRequest(new { Message = "The cleaned subtitle file is required." });

        var info = System.Text.Json.JsonSerializer.Deserialize<YouTubeVideoInfo>(model.Info,
                                                                                  new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (info == null)
            return BadRequest(new { Message = "The video info is required." });

        SanitiseInfo(info, videoId);

        await using var context = await contextFactory.CreateDbContextAsync();
        var (source, video) = await LoadPendingAsync(context, deckId, videoId);
        if (video == null)
            return NotFound();

        var directory = Path.Join(config["StaticFilesPath"], "tmp", "youtube-ingest", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var srtPath = Path.Join(directory, $"{videoId}.clean.srt");
            await using (var stream = System.IO.File.Create(srtPath))
                await model.Subtitles.CopyToAsync(stream);

            var cleaned = YouTubeSubtitleCleaner.Clean(await System.IO.File.ReadAllTextAsync(srtPath));
            var densityReason = YouTubeContentPolicy.CheckDensity(cleaned, info.DurationSeconds);

            var outcome = densityReason != null
                ? new YouTubeFetchOutcome { Status = YouTubeVideoStatus.FilteredOut, SkipReason = densityReason, Info = info, Cleaned = cleaned }
                : new YouTubeFetchOutcome { Status = YouTubeVideoStatus.Fetched, Info = info, CleanedSrtPath = srtPath, Cleaned = cleaned };

            var childDeckId = await drainService.ApplyOutcomeAsync(context, source!, video, outcome);

            return Ok(new { video.VideoId, Status = video.Status.ToString(), ChildDeckId = childDeckId });
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* staging leftovers are harmless */ }
        }
    }

    /// <summary>Parses the source's Fetched rows now; the CLI calls it once at the end of a drain instead of per video.</summary>
    [HttpPost("sources/{deckId:int}/import")]
    public async Task<IActionResult> ImportFetched(int deckId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        if (!await context.YouTubeSources.AnyAsync(s => s.DeckId == deckId))
            return NotFound();

        var jobId = backgroundJobs.Enqueue<YouTubeImportJob>(job => job.ImportFetched(deckId));
        return Accepted(new { JobId = jobId });
    }

    private static string? ValidateSource(YouTubeSourceInfo source)
    {
        var idValid = source.Kind switch
        {
            YouTubeSourceKind.Channel => YouTubeUrlParser.TryParse(source.SourceId, out var k, out _, out var id) && k == YouTubeSourceKind.Channel && id == source.SourceId,
            YouTubeSourceKind.Playlist => YouTubeUrlParser.TryParse(source.SourceId, out var k, out _, out var id) && k == YouTubeSourceKind.Playlist && id == source.SourceId,
            _ => false
        };
        if (!idValid)
            return "Not a canonical channel or playlist id.";

        if (string.IsNullOrWhiteSpace(source.Title) || source.Title.Length > 500 || source.ChannelName.Length > 500)
            return "Title is required and at most 500 characters.";

        if (source.Videos.Count > 20000)
            return "The listing holds too many videos.";

        // The ledger, links and file names all key on the bare 11-character id, never the URL form a listing may carry
        var canonical = new List<YouTubeVideoListing>(source.Videos.Count);
        foreach (var video in source.Videos)
        {
            if (!YouTubeUrlParser.TryParseVideoId(video.VideoId, out var videoId))
                return "The listing holds an invalid video id.";
            canonical.Add(video with { VideoId = videoId });
        }
        source.Videos = canonical;

        if (source.CoverUrl != null && !YtDlpClient.IsYouTubeImageUrl(source.CoverUrl))
            source.CoverUrl = null;

        return null;
    }

    /// <summary>The route decides which video is written; the payload only describes it.</summary>
    private static void SanitiseInfo(YouTubeVideoInfo? info, string videoId)
    {
        if (info == null)
            return;

        info.VideoId = videoId;
        info.Title = YouTubeDrainService.Truncate(info.Title ?? videoId, 500);
        info.Description = null;
        if (!YtDlpClient.IsYouTubeImageUrl(info.ThumbnailUrl))
            info.ThumbnailUrl = null;
    }

    private static async Task<(YouTubeSource? Source, YouTubeVideo? Video)> LoadPendingAsync(JitenDbContext context, int deckId, string videoId)
    {
        var source = await context.YouTubeSources.FirstOrDefaultAsync(s => s.DeckId == deckId);
        if (source == null)
            return (null, null);

        var video = await context.YouTubeVideos.FirstOrDefaultAsync(v => v.SourceDeckId == deckId && v.VideoId == videoId &&
                                                                         v.Status == YouTubeVideoStatus.Pending);
        return (source, video);
    }

}

public class RegisterYouTubeSourceRequest
{
    public required YouTubeSourceInfo Source { get; set; }
    public YouTubeSourceFilters? Filters { get; set; }
    public YouTubeSourceTitles? Titles { get; set; }
}

public class SkipYouTubeVideoRequest
{
    public required string Status { get; set; }
    public string? SkipReason { get; set; }
    public YouTubeVideoInfo? Info { get; set; }
}

public class FetchedYouTubeVideoRequest
{
    public required string Info { get; set; }
    public IFormFile? Subtitles { get; set; }
}
