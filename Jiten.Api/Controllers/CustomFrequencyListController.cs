using System.IO.Compression;
using System.Text;
using Jiten.Api.Authorization;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.Billing;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

/// <summary>
/// User-built custom frequency lists (Jiten+). Generation + own-download is Trial+; persisting, auto-update
/// and public sharing are Full. This is a NEW controller — the legacy <c>FrequencyListController</c>
/// (api/frequency-list) serves the static site-wide Yomitan lists and must not be touched.
/// </summary>
[ApiController]
[Route("api/frequency-lists")]
[Produces("application/json")]
[Authorize]
public class CustomFrequencyListController(
    IDbContextFactory<JitenDbContext> jitenFactory,
    UserDbContext userContext,
    ICurrentUserService currentUserService,
    IJitenPlusService jitenPlusService,
    IBackgroundJobClient backgroundJobs,
    ICdnService cdn,
    ILogger<CustomFrequencyListController> logger) : ControllerBase
{
    public const int MAX_SAVED_LISTS = 25;
    public const int MAX_TRANSIENT_LISTS = 50;
    public const int MAX_AUTO_UPDATE_LISTS = 3;
    private const int MIN_DECKS = 2;
    private const int MAX_NAME_LENGTH = 100;

    public record DefinitionDto(
        List<int>? MediaTypes,
        int? YearFrom, int? YearTo,
        List<int>? GenresInclude, List<int>? GenresExclude,
        List<int>? TagsInclude, List<int>? TagsExclude,
        double? DifficultyMin, double? DifficultyMax,
        List<int>? DeckIds);

    public record CreateRequest(string? Name, string? Mode, bool Save, bool AutoUpdate, DefinitionDto? Definition);

    public record UpdateRequest(string? Name, bool? AutoUpdate, string? Mode, DefinitionDto? Definition);

    public record PickedDeckDto(int DeckId, string OriginalTitle, string CoverName, int MediaType);

    // ---- Preview ------------------------------------------------------------

    [HttpGet("preview")]
    [JitenPlus]
    public async Task<IResult> Preview(
        [FromQuery] string? mode = "filters",
        [FromQuery] string? mediaTypes = null,
        [FromQuery] int? yearFrom = null, [FromQuery] int? yearTo = null,
        [FromQuery] string? genresInclude = null, [FromQuery] string? genresExclude = null,
        [FromQuery] string? tagsInclude = null, [FromQuery] string? tagsExclude = null,
        [FromQuery] double? difficultyMin = null, [FromQuery] double? difficultyMax = null,
        [FromQuery] string? deckIds = null)
    {
        var listMode = ParseMode(mode);
        var definition = new FrequencyListDefinition
        {
            MediaTypes = ParseCsvInts(mediaTypes),
            YearFrom = yearFrom,
            YearTo = yearTo,
            GenresInclude = ParseCsvInts(genresInclude),
            GenresExclude = ParseCsvInts(genresExclude),
            TagsInclude = ParseCsvInts(tagsInclude),
            TagsExclude = ParseCsvInts(tagsExclude),
            DifficultyMin = difficultyMin,
            DifficultyMax = difficultyMax,
            DeckIds = ParseCsvInts(deckIds)
        };

        await using var jiten = await jitenFactory.CreateDbContextAsync();
        var (count, sample) = await DeckFilterHelper.PreviewAsync(jiten, definition, listMode);
        var (genreCounts, tagCounts) = await DeckFilterHelper.FacetCountsAsync(jiten, definition, listMode);

        return Results.Ok(new
        {
            deckCount = count,
            sampleTitles = sample,
            minDecks = MIN_DECKS,
            genreCounts,
            tagCounts
        });
    }

    // ---- Create -------------------------------------------------------------

    [HttpPost]
    [JitenPlus]
    [EnableRateLimiting("freq-list-create")]
    public async Task<IResult> Create([FromBody] CreateRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var name = SanitizeName(request.Name);
        if (string.IsNullOrEmpty(name))
            return Results.BadRequest(new { error = "Please give the list a name." });

        var mode = ParseMode(request.Mode);
        var definition = ToDefinition(request.Definition);

        // Full is required to persist a list, opt into auto-update, or share it.
        if (request.Save)
        {
            var forbid = await RequireFullAsync(userId, "freq-list-save");
            if (forbid != null) return forbid;
        }

        await using var jiten = await jitenFactory.CreateDbContextAsync();
        var deckIds = await DeckFilterHelper.ResolveDeckIdsAsync(jiten, definition, mode);

        if (deckIds.Count < MIN_DECKS)
        {
            return Results.BadRequest(new
            {
                error = $"A frequency list needs at least {MIN_DECKS} matching decks. Your current selection matches {deckIds.Count}.",
                deckCount = deckIds.Count
            });
        }

        // Concurrent-list caps: saved lists and transient results are counted separately.
        if (request.Save)
        {
            var savedCount = await userContext.UserFrequencyLists.CountAsync(f => f.UserId == userId && f.IsSaved);
            if (savedCount >= MAX_SAVED_LISTS)
                return Results.BadRequest(new { error = $"You can keep at most {MAX_SAVED_LISTS} saved lists. Delete one first." });
        }
        else
        {
            // Expired lists keep their definition and still count here, so the guidance is to delete one.
            var transientCount = await userContext.UserFrequencyLists.CountAsync(f => f.UserId == userId && !f.IsSaved);
            if (transientCount >= MAX_TRANSIENT_LISTS)
                return Results.BadRequest(new { error = $"You can have at most {MAX_TRANSIENT_LISTS} generated lists at once. Delete one to make room." });
        }

        if (request is { Save: true, AutoUpdate: true })
        {
            var autoUpdateCount = await userContext.UserFrequencyLists.CountAsync(f => f.UserId == userId && f.AutoUpdate);
            if (autoUpdateCount >= MAX_AUTO_UPDATE_LISTS)
                return Results.BadRequest(new { error = $"You can auto-update at most {MAX_AUTO_UPDATE_LISTS} lists. Turn auto-update off on another list first." });
        }

        var list = new UserFrequencyList
        {
            UserId = userId,
            Name = name,
            Mode = mode,
            Definition = definition,
            IsSaved = request.Save,
            AutoUpdate = request.Save && request.AutoUpdate,
            // Saved (permanent) lists get their slug immediately; it is the anonymous credential for the
            // share link and for Yomitan's update index/download URLs. Share only retrieves it.
            PublicSlug = request.Save ? FrequencyListLinks.GenerateSlug() : null,
            Status = FrequencyListStatus.Pending,
            DeckCount = deckIds.Count,
            CreatedAt = DateTime.UtcNow
        };

        userContext.UserFrequencyLists.Add(list);
        await userContext.SaveChangesAsync();

        backgroundJobs.Enqueue<FrequencyListJob>(j => j.Generate(list.Id));

        logger.LogInformation("CustomFrequencyList: user {UserId} created list {ListId} ({Mode}, saved={Saved}, {Decks} decks)",
                              userId, list.Id, mode, request.Save, deckIds.Count);

        return Results.Ok(ToDto(list));
    }

    // ---- List ---------------------------------------------------------------

    [HttpGet]
    [JitenPlus]
    public async Task<IResult> GetLists()
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var lists = await userContext.UserFrequencyLists
                                     .AsNoTracking()
                                     .Where(f => f.UserId == userId)
                                     .OrderByDescending(f => f.CreatedAt)
                                     .ToListAsync();

        var pickedIdsPerList = lists.Where(f => f.Mode == FrequencyListMode.HandPicked)
                                    .ToDictionary(f => f.Id, f => f.Definition.DeckIds);

        var decksById = new Dictionary<int, PickedDeckDto>();
        var allPickedIds = pickedIdsPerList.Values.SelectMany(ids => ids).Distinct().ToList();
        if (allPickedIds.Count > 0)
        {
            await using var jiten = await jitenFactory.CreateDbContextAsync();
            decksById = await jiten.Decks.AsNoTracking()
                                  .Where(d => allPickedIds.Contains(d.DeckId))
                                  .Select(d => new PickedDeckDto(d.DeckId, d.OriginalTitle, d.CoverName, (int)d.MediaType))
                                  .ToDictionaryAsync(d => d.DeckId);
        }

        return Results.Ok(lists.Select(f => ToDto(f, pickedIdsPerList.TryGetValue(f.Id, out var ids)
                                                        ? ids.Where(decksById.ContainsKey).Select(id => decksById[id]).ToList()
                                                        : null)));
    }

    // ---- Download -----------------------------------------------------------

    [HttpGet("{id:long}/download")]
    [JitenPlus]
    [EnableRateLimiting("download")]
    public async Task<IResult> Download(long id, [FromQuery] string format = "zip")
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var list = await userContext.UserFrequencyLists.AsNoTracking()
                                    .FirstOrDefaultAsync(f => f.Id == id && f.UserId == userId);
        if (list is null)
            return Results.NotFound();

        if (list.Status == FrequencyListStatus.Expired)
            return Results.Json(new { error = "This list's files have expired. Regenerate it to rebuild them." },
                                statusCode: StatusCodes.Status410Gone);

        if (list.Status != FrequencyListStatus.Ready)
            return Results.BadRequest(new { error = "This list isn't ready yet." });

        return await ServeListFile(list, format);
    }

    // ---- Regenerate ---------------------------------------------------------

    [HttpPost("{id:long}/regenerate")]
    [JitenPlus]
    [EnableRateLimiting("compute")]
    public async Task<IResult> Regenerate(long id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var list = await userContext.UserFrequencyLists.FirstOrDefaultAsync(f => f.Id == id && f.UserId == userId);
        if (list is null)
            return Results.NotFound();

        list.Status = FrequencyListStatus.Pending;
        await userContext.SaveChangesAsync();

        backgroundJobs.Enqueue<FrequencyListJob>(j => j.Generate(list.Id));
        return Results.Ok(ToDto(list));
    }

    // ---- Save (transient -> saved) ------------------------------------------

    [HttpPost("{id:long}/save")]
    [JitenPlus(JitenPlusTier.Full, Feature = "freq-list-save")]
    public async Task<IResult> Save(long id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var list = await userContext.UserFrequencyLists.FirstOrDefaultAsync(f => f.Id == id && f.UserId == userId);
        if (list is null)
            return Results.NotFound();

        if (list.IsSaved)
            return Results.Ok(ToDto(list));

        var savedCount = await userContext.UserFrequencyLists.CountAsync(f => f.UserId == userId && f.IsSaved);
        if (savedCount >= MAX_SAVED_LISTS)
            return Results.BadRequest(new { error = $"You can keep at most {MAX_SAVED_LISTS} saved lists. Delete one first." });

        list.IsSaved = true;
        var mintedSlug = string.IsNullOrEmpty(list.PublicSlug);
        list.PublicSlug ??= FrequencyListLinks.GenerateSlug();
        await userContext.SaveChangesAsync();

        // The zip was generated while the list was transient, so its index.json lacks the Yomitan update
        // URLs. Patch it before returning so no one can download a non-updatable zip from a saved list.
        if (mintedSlug && list.Status == FrequencyListStatus.Ready)
            await TryEmbedUpdateUrls(list);

        return Results.Ok(ToDto(list));
    }

    /// <summary>
    /// Rewrites index.json inside the already-generated zip so it carries the Yomitan update URLs. Runs
    /// synchronously when a transient list is saved (its zip predates the slug) so a download right after
    /// saving can't hand out a non-updatable zip. Failures are logged, not surfaced — the save itself
    /// succeeded and the zip is merely non-updatable, exactly as it was before the save.
    /// </summary>
    private async Task TryEmbedUpdateUrls(UserFrequencyList list)
    {
        try
        {
            var zipBytes = await cdn.DownloadFile(FrequencyListJob.ZipStoragePath(list.UserId, list.Id));
            if (zipBytes is null)
                return;

            var configuration = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var indexJson = YomitanHelper.GetCustomFrequencyIndexJson(list.Name, list.GeneratedAt ?? list.CreatedAt,
                                                                      FrequencyListLinks.IndexUrl(configuration, list.PublicSlug!),
                                                                      FrequencyListLinks.DownloadUrl(configuration, list.PublicSlug!));

            using var stream = new MemoryStream();
            stream.Write(zipBytes, 0, zipBytes.Length);
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
            {
                archive.GetEntry("index.json")?.Delete();
                var entry = archive.CreateEntry("index.json", CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(Encoding.UTF8.GetBytes(indexJson));
            }

            await cdn.UploadFile(stream.ToArray(), FrequencyListJob.ZipStoragePath(list.UserId, list.Id));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to embed update URLs into zip for list {ListId}", list.Id);
        }
    }

    // ---- Rename / auto-update toggle / edit filters --------------------------

    [HttpPatch("{id:long}")]
    [JitenPlus]
    [EnableRateLimiting("compute")]
    public async Task<IResult> Update(long id, [FromBody] UpdateRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var list = await userContext.UserFrequencyLists.FirstOrDefaultAsync(f => f.Id == id && f.UserId == userId);
        if (list is null)
            return Results.NotFound();

        if (request.Name != null)
        {
            var name = SanitizeName(request.Name);
            if (string.IsNullOrEmpty(name))
                return Results.BadRequest(new { error = "Please give the list a name." });
            list.Name = name;
        }

        if (request.AutoUpdate.HasValue && request.AutoUpdate.Value != list.AutoUpdate)
        {
            // Auto-update is a Full-only, saved-list capability.
            var forbid = await RequireFullAsync(userId, "freq-list-save");
            if (forbid != null) return forbid;

            if (request.AutoUpdate.Value && !list.IsSaved)
                return Results.BadRequest(new { error = "Save the list before enabling auto-update." });

            if (request.AutoUpdate.Value)
            {
                var autoUpdateCount = await userContext.UserFrequencyLists.CountAsync(f => f.UserId == userId && f.AutoUpdate);
                if (autoUpdateCount >= MAX_AUTO_UPDATE_LISTS)
                    return Results.BadRequest(new { error = $"You can auto-update at most {MAX_AUTO_UPDATE_LISTS} lists. Turn auto-update off on another list first." });
            }

            list.AutoUpdate = request.AutoUpdate.Value;
        }

        var definitionChanged = false;
        if (request.Definition != null)
        {
            var mode = request.Mode != null ? ParseMode(request.Mode) : list.Mode;
            var definition = ToDefinition(request.Definition);

            await using var jiten = await jitenFactory.CreateDbContextAsync();
            var deckIds = await DeckFilterHelper.ResolveDeckIdsAsync(jiten, definition, mode);

            if (deckIds.Count < MIN_DECKS)
            {
                return Results.BadRequest(new
                {
                    error = $"A frequency list needs at least {MIN_DECKS} matching decks. Your current selection matches {deckIds.Count}.",
                    deckCount = deckIds.Count
                });
            }

            list.Mode = mode;
            list.Definition = definition;
            list.DeckCount = deckIds.Count;
            list.Status = FrequencyListStatus.Pending;
            definitionChanged = true;
        }

        await userContext.SaveChangesAsync();

        if (definitionChanged)
            backgroundJobs.Enqueue<FrequencyListJob>(j => j.Generate(list.Id));

        return Results.Ok(ToDto(list));
    }

    // ---- Share --------------------------------------------------------------

    [HttpPost("{id:long}/share")]
    [JitenPlus(JitenPlusTier.Full, Feature = "freq-list-save")]
    public async Task<IResult> Share(long id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var list = await userContext.UserFrequencyLists.FirstOrDefaultAsync(f => f.Id == id && f.UserId == userId);
        if (list is null)
            return Results.NotFound();

        // The slug is minted when the list becomes permanent; sharing just retrieves it. The mint here is a
        // fallback for saved rows that predate slug-at-save (retrying on the off-chance of a collision).
        if (string.IsNullOrEmpty(list.PublicSlug))
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                list.PublicSlug = FrequencyListLinks.GenerateSlug();
                try
                {
                    await userContext.SaveChangesAsync();
                    break;
                }
                catch (DbUpdateException) when (attempt < 4)
                {
                    userContext.Entry(list).Reload();
                }
            }
        }

        return Results.Ok(new { slug = list.PublicSlug });
    }

    // ---- Anonymous shared download ------------------------------------------

    [HttpGet("shared/{slug}")]
    [AllowAnonymous]
    [EnableRateLimiting("download")]
    public async Task<IResult> SharedDownload(string slug, [FromQuery] string format = "zip")
    {
        var list = await userContext.UserFrequencyLists.AsNoTracking()
                                    .FirstOrDefaultAsync(f => f.PublicSlug == slug);
        if (list is null || list.Status != FrequencyListStatus.Ready)
            return Results.NotFound();

        return await ServeListFile(list, format);
    }

    /// <summary>
    /// Fresh Yomitan index for a shared list, served straight from the DB. Yomitan polls this URL (embedded
    /// in the generated zip's index.json) and offers an update when the revision is newer than the installed
    /// one, downloading from the shared download URL above.
    /// </summary>
    [HttpGet("shared/{slug}/index")]
    [AllowAnonymous]
    public async Task<IResult> SharedIndex(string slug)
    {
        var list = await userContext.UserFrequencyLists.AsNoTracking()
                                    .FirstOrDefaultAsync(f => f.PublicSlug == slug);
        if (list is null || list.Status != FrequencyListStatus.Ready)
            return Results.NotFound();

        var configuration = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var indexJson = YomitanHelper.GetCustomFrequencyIndexJson(list.Name, list.GeneratedAt ?? list.CreatedAt,
                                                                  FrequencyListLinks.IndexUrl(configuration, slug),
                                                                  FrequencyListLinks.DownloadUrl(configuration, slug));
        return Results.Text(indexJson, "application/json");
    }

    /// <summary>
    /// Streams a list's file from the storage zone (authoritative bytes — the pull-zone cache is not purged
    /// on regeneration) with a Content-Disposition filename based on the list's name, so downloads aren't
    /// saved as "{id}.zip".
    /// </summary>
    private async Task<IResult> ServeListFile(UserFrequencyList list, string format)
    {
        var isCsv = format == "csv";
        var storedUrl = isCsv ? list.CsvUrl : list.ZipUrl;
        if (string.IsNullOrEmpty(storedUrl))
            return Results.NotFound();

        var storagePath = isCsv
            ? FrequencyListJob.CsvStoragePath(list.UserId, list.Id)
            : FrequencyListJob.ZipStoragePath(list.UserId, list.Id);

        var bytes = await cdn.DownloadFile(storagePath);
        if (bytes is null)
            return Results.NotFound();

        var fileName = $"{SafeFileName(list.Name)}.{(isCsv ? "csv" : "zip")}";
        return Results.File(bytes, isCsv ? "text/csv" : "application/zip", fileName);
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(cleaned) ? "frequency-list" : cleaned;
    }

    // ---- Delete -------------------------------------------------------------

    [HttpDelete("{id:long}")]
    [JitenPlus]
    public async Task<IResult> Delete(long id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var list = await userContext.UserFrequencyLists.FirstOrDefaultAsync(f => f.Id == id && f.UserId == userId);
        if (list is null)
            return Results.NotFound();

        try
        {
            if (!string.IsNullOrEmpty(list.ZipUrl))
                await cdn.DeleteFile(FrequencyListJob.ZipStoragePath(list.UserId, list.Id));
            if (!string.IsNullOrEmpty(list.CsvUrl))
                await cdn.DeleteFile(FrequencyListJob.CsvStoragePath(list.UserId, list.Id));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CustomFrequencyList: failed to delete CDN files for list {ListId}", list.Id);
        }

        userContext.UserFrequencyLists.Remove(list);
        await userContext.SaveChangesAsync();
        return Results.Ok();
    }

    // ---- Helpers ------------------------------------------------------------

    private async Task<IResult?> RequireFullAsync(string userId, string feature)
    {
        var tier = await jitenPlusService.GetTierAsync(userId);
        if (tier >= JitenPlusTier.Full)
            return null;

        return Results.Json(new
        {
            jitenPlus = true,
            feature,
            requiredTier = "full",
            currentTier = tier.ToString().ToLowerInvariant(),
            message = "This feature stores your data permanently and isn't part of the trial. It unlocks with any paid plan."
        }, statusCode: StatusCodes.Status403Forbidden);
    }

    private static FrequencyListMode ParseMode(string? mode) =>
        string.Equals(mode, "handpicked", StringComparison.OrdinalIgnoreCase)
            ? FrequencyListMode.HandPicked
            : FrequencyListMode.Filters;

    private static List<int> ParseCsvInts(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? new List<int>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(s => int.TryParse(s, out var v) ? (int?)v : null)
                 .Where(v => v.HasValue)
                 .Select(v => v!.Value)
                 .ToList();

    private static FrequencyListDefinition ToDefinition(DefinitionDto? dto) => new()
    {
        MediaTypes = dto?.MediaTypes ?? new(),
        YearFrom = dto?.YearFrom,
        YearTo = dto?.YearTo,
        GenresInclude = dto?.GenresInclude ?? new(),
        GenresExclude = dto?.GenresExclude ?? new(),
        TagsInclude = dto?.TagsInclude ?? new(),
        TagsExclude = dto?.TagsExclude ?? new(),
        DifficultyMin = dto?.DifficultyMin,
        DifficultyMax = dto?.DifficultyMax,
        DeckIds = dto?.DeckIds ?? new()
    };

    private static string? SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var cleaned = new string(name.Trim().Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0)
            return null;

        return cleaned.Length > MAX_NAME_LENGTH ? cleaned[..MAX_NAME_LENGTH] : cleaned;
    }

    private static object ToDto(UserFrequencyList list, List<PickedDeckDto>? pickedDecks = null) => new
    {
        id = list.Id,
        name = list.Name,
        mode = list.Mode.ToString().ToLowerInvariant(),
        definition = list.Definition,
        isSaved = list.IsSaved,
        autoUpdate = list.AutoUpdate,
        publicSlug = list.PublicSlug,
        status = list.Status.ToString().ToLowerInvariant(),
        wordCount = list.WordCount,
        deckCount = list.DeckCount,
        createdAt = list.CreatedAt,
        generatedAt = list.GeneratedAt,
        pickedDecks
    };
}
