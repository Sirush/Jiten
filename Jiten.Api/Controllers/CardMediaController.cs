using Jiten.Api.Authorization;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.Billing;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.User;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Jiten.Api.Controllers;

/// <summary>
/// User-uploaded per-card images and audio (Jiten+). Media is keyed on (UserId, WordId, ReadingIndex,
/// Kind); the type is sniffed from the file's magic bytes, never from Content-Type. Uploading needs an
/// active tier and is bounded by that tier's allowance; reads and deletes gate on ownership only, so a
/// lapsed subscriber keeps and can clear what they already uploaded.
/// </summary>
[ApiController]
[Route("api/srs/card-media")]
[Produces("application/json")]
[Authorize]
public class CardMediaController(
    UserDbContext userContext,
    IDbContextFactory<JitenDbContext> jitenFactory,
    ICurrentUserService currentUserService,
    ICardMediaQuotaService quotaService,
    ICardMediaWriteService writeService,
    ICdnService cdn,
    ILogger<CardMediaController> logger) : ControllerBase
{
    private const long MaxFileBytes = CardMediaWriteService.MaxFileBytes;
    private const int MaxBatchItems = 500;
    private const int MaxDeleteBatchItems = 200;
    private const int MaxImportBatchItems = 20;
    private const long MaxImportBatchBytes = 55_000_000;
    private static readonly TimeSpan SignedUrlTtl = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public record BatchItem(int WordId, int ReadingIndex);

    public record BatchRequest(List<BatchItem>? Items);

    // ---- Upload -------------------------------------------------------------

    [HttpPost("{wordId:int}/{readingIndex:int}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxFileBytes + 4096)]
    [EnableRateLimiting("card-media-upload")]
    [JitenPlus(JitenPlusTier.Trial, Feature = "card-media")]
    public async Task<IResult> Upload(int wordId, int readingIndex, IFormFile? file)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        if (readingIndex is < 0 or > byte.MaxValue)
            return Results.BadRequest(new { error = "Invalid reading index." });
        var ri = (byte)readingIndex;

        if (file is null || file.Length == 0)
            return Results.BadRequest(new { error = "No file was uploaded." });

        if (file.Length > MaxFileBytes)
            return Results.BadRequest(new { error = $"File is too large. The maximum is {MaxFileBytes / (1024 * 1024)} MB." });

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms);
            bytes = ms.ToArray();
        }

        var quota = await quotaService.GetQuotaAsync(userId);
        var usedBytes = await userContext.UserCardMedia.Where(m => m.UserId == userId).SumAsync(m => m.FileSizeBytes);

        var result = await writeService.WriteAsync(userId, wordId, ri, bytes, overwrite: true, quota, usedBytes);

        switch (result.Status)
        {
            case CardMediaWriteStatus.Invalid:
                return Results.BadRequest(new
                {
                    error = "Unsupported file. Upload an image (JPEG, PNG, WebP, GIF, HEIC, AVIF) or audio (MP3, M4A, OGG, Opus, WebM, WAV, FLAC)."
                });
            case CardMediaWriteStatus.TooLarge:
                return Results.BadRequest(new { error = $"File is too large. The maximum is {MaxFileBytes / (1024 * 1024)} MB." });
            case CardMediaWriteStatus.QuotaExceeded:
                return Results.BadRequest(new
                {
                    error = quota.Tier == JitenPlusTier.Trial
                        ? "This upload would exceed your trial storage. Delete some card media, or subscribe for the full allowance."
                        : "This upload would exceed your storage quota. Delete some card media and try again.",
                    usedBytes = result.UsedBytes,
                    maxBytes = quota.MaxBytes
                });
        }

        return Results.Ok(new
        {
            media = ToDto(result.Row!, inherited: false, sourceReadingIndex: ri),
            quota = new { usedBytes = result.UsedBytes, maxBytes = quota.MaxBytes }
        });
    }

    // ---- Import batch -------------------------------------------------------

    public record ImportBatchItem(int Index, int WordId, int ReadingIndex, bool Overwrite);

    /// <summary>
    /// Writes up to twenty files in one request, for bulk imports that would otherwise spend an hour in the
    /// single-file endpoint's rate limit
    /// </summary>
    [HttpPost("import-batch")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxImportBatchBytes)]
    [EnableRateLimiting("card-media-import")]
    [JitenPlus(JitenPlusTier.Trial, Feature = "card-media")]
    public async Task<IResult> ImportBatch([FromForm] string? manifest)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        List<ImportBatchItem>? items;
        try
        {
            items = string.IsNullOrWhiteSpace(manifest)
                ? null
                : JsonSerializer.Deserialize<List<ImportBatchItem>>(manifest, ManifestJsonOptions);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Malformed manifest." });
        }

        if (items is not { Count: > 0 })
            return Results.BadRequest(new { error = "No items in the manifest." });

        if (items.Count > MaxImportBatchItems)
            return Results.BadRequest(new { error = $"Maximum of {MaxImportBatchItems} files per request." });

        var files = Request.Form.Files;
        if (files.Count != items.Count)
            return Results.BadRequest(new { error = "Manifest and file counts do not match." });

        if (items.Any(i => i.ReadingIndex is < 0 or > byte.MaxValue))
            return Results.BadRequest(new { error = "Invalid reading index." });

        // Items address their file by index, so a repeat would silently write the same file twice.
        if (items.Select(i => i.Index).Distinct().Count() != items.Count)
            return Results.BadRequest(new { error = "Manifest indexes must be unique." });

        // The real fence: media can only land on forms the caller studies, which is exactly what the
        // import flow produces and nothing a general-purpose uploader could use.
        var wordIds = items.Select(i => i.WordId).Distinct().ToList();
        var tracked = (await userContext.FsrsCards
                                        .AsNoTracking()
                                        .Where(c => c.UserId == userId && wordIds.Contains(c.WordId))
                                        .Select(c => new { c.WordId, c.ReadingIndex })
                                        .ToListAsync())
            .Select(c => (c.WordId, c.ReadingIndex))
            .ToHashSet();

        var quota = await quotaService.GetQuotaAsync(userId);
        var usedBytes = await userContext.UserCardMedia.Where(m => m.UserId == userId).SumAsync(m => m.FileSizeBytes);

        var results = new List<object>(items.Count);
        var quotaExhausted = false;

        // Sequential on purpose: the running byte tally is what keeps quota accounting exact within a
        // request, and each item may run an ImageMagick normalization.
        foreach (var item in items)
        {
            var ri = (byte)item.ReadingIndex;

            if (quotaExhausted)
            {
                results.Add(new { index = item.Index, status = "quota_exceeded" });
                continue;
            }

            if (!tracked.Contains((item.WordId, ri)))
            {
                results.Add(new { index = item.Index, status = "not_tracked" });
                continue;
            }

            var file = files.GetFile($"file{item.Index}");
            if (file is null || file.Length == 0)
            {
                results.Add(new { index = item.Index, status = "invalid" });
                continue;
            }

            if (file.Length > CardMediaWriteService.MaxFileBytes)
            {
                results.Add(new { index = item.Index, status = "too_large" });
                continue;
            }

            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms);
                bytes = ms.ToArray();
            }

            var result = await writeService.WriteAsync(userId, item.WordId, ri, bytes, item.Overwrite, quota, usedBytes);
            usedBytes = result.UsedBytes;

            switch (result.Status)
            {
                case CardMediaWriteStatus.Ok:
                    results.Add(new
                    {
                        index = item.Index,
                        status = "ok",
                        kind = result.Kind?.ToString().ToLowerInvariant(),
                        storedBytes = result.StoredBytes
                    });
                    break;
                case CardMediaWriteStatus.QuotaExceeded:
                    quotaExhausted = true;
                    results.Add(new { index = item.Index, status = "quota_exceeded" });
                    break;
                case CardMediaWriteStatus.Conflict:
                    results.Add(new { index = item.Index, status = "conflict", kind = result.Kind?.ToString().ToLowerInvariant() });
                    break;
                case CardMediaWriteStatus.TooLarge:
                    results.Add(new { index = item.Index, status = "too_large" });
                    break;
                default:
                    results.Add(new { index = item.Index, status = "invalid" });
                    break;
            }
        }

        return Results.Ok(new { results, usedBytes, maxBytes = quota.MaxBytes });
    }

    // ---- Delete -------------------------------------------------------------

    [HttpDelete("{wordId:int}/{readingIndex:int}/{kind}")]
    public async Task<IResult> Delete(int wordId, int readingIndex, string kind)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        if (readingIndex is < 0 or > byte.MaxValue)
            return Results.BadRequest(new { error = "Invalid reading index." });
        var ri = (byte)readingIndex;

        if (!TryParseKind(kind, out var mediaKind))
            return Results.BadRequest(new { error = "Kind must be 'image' or 'audio'." });

        var row = await userContext.UserCardMedia
                                   .FirstOrDefaultAsync(m => m.UserId == userId && m.WordId == wordId
                                                             && m.ReadingIndex == ri && m.Kind == mediaKind);
        if (row is null)
            return Results.NotFound();

        var paths = OwnedPaths(row).ToList();
        userContext.UserCardMedia.Remove(row);
        await userContext.SaveChangesAsync();

        await DeleteFilesAsync(paths);

        return Results.Ok(new { quota = await QuotaPayloadAsync(userId) });
    }

    [HttpDelete]
    public async Task<IResult> DeleteAll()
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var rows = await userContext.UserCardMedia.Where(m => m.UserId == userId).ToListAsync();
        var paths = rows.SelectMany(OwnedPaths).ToList();

        userContext.UserCardMedia.RemoveRange(rows);
        await userContext.SaveChangesAsync();

        await DeleteFilesAsync(paths);

        var quota = await quotaService.GetQuotaAsync(userId);
        return Results.Ok(new { quota = new { usedBytes = 0L, maxBytes = quota.MaxBytes } });
    }

    // ---- Batch delete -------------------------------------------------------

    public record DeleteBatchItem(int WordId, int ReadingIndex, string Kind);

    public record DeleteBatchRequest(List<DeleteBatchItem>? Items);

    [HttpPost("delete-batch")]
    public async Task<IResult> DeleteBatch([FromBody] DeleteBatchRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var items = request.Items ?? [];
        if (items.Count == 0)
            return Results.BadRequest(new { error = "No items to delete." });
        if (items.Count > MaxDeleteBatchItems)
            return Results.BadRequest(new { error = $"At most {MaxDeleteBatchItems} items per request." });

        var targets = new HashSet<(int WordId, byte Ri, CardMediaKind Kind)>(items.Count);
        foreach (var i in items)
        {
            if (i.ReadingIndex is < 0 or > byte.MaxValue)
                return Results.BadRequest(new { error = "Invalid reading index." });
            if (!TryParseKind(i.Kind, out var mediaKind))
                return Results.BadRequest(new { error = "Kind must be 'image' or 'audio'." });
            targets.Add((i.WordId, (byte)i.ReadingIndex, mediaKind));
        }

        var wordIds = targets.Select(t => t.WordId).Distinct().ToList();
        var candidates = await userContext.UserCardMedia
                                          .Where(m => m.UserId == userId && wordIds.Contains(m.WordId))
                                          .ToListAsync();

        var toDelete = candidates.Where(c => targets.Contains((c.WordId, c.ReadingIndex, c.Kind))).ToList();
        var paths = toDelete.SelectMany(OwnedPaths).ToList();

        userContext.UserCardMedia.RemoveRange(toDelete);
        await userContext.SaveChangesAsync();

        await DeleteFilesAsync(paths);

        return Results.Ok(new { deleted = toDelete.Count, quota = await QuotaPayloadAsync(userId) });
    }

    private static IEnumerable<string> OwnedPaths(UserCardMedia row) =>
        row.PreviousStoragePath is null ? [row.StoragePath] : [row.StoragePath, row.PreviousStoragePath];

    private async Task DeleteFilesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try { await cdn.DeleteFile(path, secure: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to delete card-media CDN file {Path}", path); }
        }
    }

    private async Task<object> QuotaPayloadAsync(string userId)
    {
        var quota = await quotaService.GetQuotaAsync(userId);
        var usedBytes = await userContext.UserCardMedia.Where(m => m.UserId == userId).SumAsync(m => m.FileSizeBytes);
        return new { usedBytes, maxBytes = quota.MaxBytes };
    }

    // ---- Summary ------------------------------------------------------------

    public record CardMediaSummary(int TotalForms, int ImageCount, long ImageBytes, int AudioCount, long AudioBytes, long UsedBytes, long MaxBytes);

    private async Task<CardMediaSummary> ComputeSummaryAsync(string userId)
    {
        var quota = await quotaService.GetQuotaAsync(userId);

        var byKind = await userContext.UserCardMedia
                                      .AsNoTracking()
                                      .Where(m => m.UserId == userId)
                                      .GroupBy(m => m.Kind)
                                      .Select(g => new { Kind = g.Key, Count = g.Count(), Bytes = g.Sum(x => x.FileSizeBytes) })
                                      .ToListAsync();

        var image = byKind.FirstOrDefault(k => k.Kind == CardMediaKind.Image);
        var audio = byKind.FirstOrDefault(k => k.Kind == CardMediaKind.Audio);
        var imageBytes = image?.Bytes ?? 0;
        var audioBytes = audio?.Bytes ?? 0;

        var totalForms = await userContext.UserCardMedia
                                          .AsNoTracking()
                                          .Where(m => m.UserId == userId)
                                          .Select(m => new { m.WordId, m.ReadingIndex })
                                          .Distinct()
                                          .CountAsync();

        return new CardMediaSummary(totalForms, image?.Count ?? 0, imageBytes, audio?.Count ?? 0, audioBytes, imageBytes + audioBytes, quota.MaxBytes);
    }

    private static object SummaryPayload(CardMediaSummary s) => new
    {
        totalForms = s.TotalForms,
        imageCount = s.ImageCount,
        imageBytes = s.ImageBytes,
        audioCount = s.AudioCount,
        audioBytes = s.AudioBytes,
        usedBytes = s.UsedBytes,
        maxBytes = s.MaxBytes
    };

    [HttpGet("summary")]
    public async Task<IResult> Summary()
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        return Results.Ok(SummaryPayload(await ComputeSummaryAsync(userId)));
    }

    // ---- Manage -------------------------------------------------------------

    [HttpGet("manage")]
    public async Task<IResult> Manage(int page = 1, string sort = "size", string kind = "all", string? search = null)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        const int pageSize = 50;
        if (page < 1) page = 1;

        var kindFilter = ParseKindFilter(kind);
        var summary = SummaryPayload(await ComputeSummaryAsync(userId));

        var baseQuery = userContext.UserCardMedia.AsNoTracking().Where(m => m.UserId == userId);
        if (kindFilter is { } k)
            baseQuery = baseQuery.Where(m => m.Kind == k);

        var trimmedSearch = search?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedSearch))
        {
            List<int> matchedWordIds;
            await using (var jiten = await jitenFactory.CreateDbContextAsync())
            {
                matchedWordIds = await jiten.Lookups
                                            .AsNoTracking()
                                            .Where(l => l.LookupKey == trimmedSearch)
                                            .Select(l => l.WordId)
                                            .Distinct()
                                            .ToListAsync();
            }

            if (matchedWordIds.Count == 0)
                return Results.Ok(new { items = Array.Empty<object>(), page, pageSize, totalForms = 0, summary });

            baseQuery = baseQuery.Where(m => matchedWordIds.Contains(m.WordId));
        }

        var grouped = baseQuery
            .GroupBy(m => new { m.WordId, m.ReadingIndex })
            .Select(g => new
            {
                g.Key.WordId,
                g.Key.ReadingIndex,
                TotalBytes = g.Sum(x => x.FileSizeBytes),
                MostRecent = g.Max(x => x.CreatedAt)
            });

        var totalForms = await grouped.CountAsync();

        grouped = sort?.ToLowerInvariant() switch
        {
            "date_desc" => grouped.OrderByDescending(f => f.MostRecent).ThenBy(f => f.WordId).ThenBy(f => f.ReadingIndex),
            "date_asc" => grouped.OrderBy(f => f.MostRecent).ThenBy(f => f.WordId).ThenBy(f => f.ReadingIndex),
            _ => grouped.OrderByDescending(f => f.TotalBytes).ThenBy(f => f.WordId).ThenBy(f => f.ReadingIndex)
        };

        var pageKeys = await grouped.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        var pageWordIds = pageKeys.Select(p => p.WordId).Distinct().ToList();
        var pairSet = pageKeys.Select(p => (p.WordId, p.ReadingIndex)).ToHashSet();

        List<UserCardMedia> pageRows = [];
        if (pageWordIds.Count > 0)
        {
            var rowQuery = userContext.UserCardMedia.AsNoTracking()
                                      .Where(m => m.UserId == userId && pageWordIds.Contains(m.WordId));
            if (kindFilter is { } kf)
                rowQuery = rowQuery.Where(m => m.Kind == kf);

            var fetched = await rowQuery.ToListAsync();
            pageRows = fetched.Where(r => pairSet.Contains((r.WordId, r.ReadingIndex))).ToList();
        }

        var wordText = new Dictionary<(int, byte), string>();
        if (pageWordIds.Count > 0)
        {
            await using var jiten = await jitenFactory.CreateDbContextAsync();
            var formTexts = await jiten.WordForms
                                       .AsNoTracking()
                                       .Where(f => pageWordIds.Contains(f.WordId))
                                       .Select(f => new { f.WordId, f.ReadingIndex, f.Text })
                                       .ToListAsync();
            foreach (var f in formTexts)
            {
                if (f.ReadingIndex is < 0 or > byte.MaxValue) continue;
                wordText[(f.WordId, (byte)f.ReadingIndex)] = f.Text;
            }
        }

        var rowsByKey = pageRows.GroupBy(r => (r.WordId, r.ReadingIndex))
                                .ToDictionary(g => g.Key, g => g.ToList());

        var items = pageKeys.Select(p =>
        {
            var rows = rowsByKey.GetValueOrDefault((p.WordId, p.ReadingIndex)) ?? [];
            var image = rows.FirstOrDefault(r => r.Kind == CardMediaKind.Image);
            var audio = rows.FirstOrDefault(r => r.Kind == CardMediaKind.Audio);
            return new
            {
                wordId = p.WordId,
                readingIndex = (int)p.ReadingIndex,
                wordText = wordText.GetValueOrDefault((p.WordId, p.ReadingIndex), ""),
                totalBytes = p.TotalBytes,
                image = image is null ? null : ManageFileDto(image),
                audio = audio is null ? null : ManageFileDto(audio)
            };
        }).ToList();

        return Results.Ok(new { items, page, pageSize, totalForms, summary });
    }

    private object ManageFileDto(UserCardMedia media) => new
    {
        url = cdn.GetSignedUrl(media.StoragePath, SignedUrlTtl),
        fileSizeBytes = media.FileSizeBytes,
        createdAt = media.CreatedAt,
        contentType = media.ContentType
    };

    private static CardMediaKind? ParseKindFilter(string? kind) => kind?.ToLowerInvariant() switch
    {
        "image" => CardMediaKind.Image,
        "audio" => CardMediaKind.Audio,
        _ => null
    };

    // ---- Batch fetch --------------------------------------------------------

    [HttpPost("batch")]
    public async Task<IResult> Batch([FromBody] BatchRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var items = request.Items ?? [];
        if (items.Count > MaxBatchItems)
            return Results.BadRequest(new { error = $"At most {MaxBatchItems} items per request." });

        if (items.Count == 0)
            return Results.Ok(new { items = Array.Empty<object>() });

        var wordIds = items.Select(i => i.WordId).Distinct().ToList();

        var media = await userContext.UserCardMedia
                                     .AsNoTracking()
                                     .Where(m => m.UserId == userId && wordIds.Contains(m.WordId))
                                     .ToListAsync();

        // Per-word kana-reading count drives the audio kana-guard: a word with exactly one kana reading has a
        // single pronunciation shared by every form, so audio may inherit; otherwise it must not.
        Dictionary<int, int> kanaCounts;
        await using (var jiten = await jitenFactory.CreateDbContextAsync())
        {
            kanaCounts = await jiten.WordForms
                                    .AsNoTracking()
                                    .Where(f => wordIds.Contains(f.WordId) && f.FormType == JmDictFormType.KanaForm)
                                    .GroupBy(f => f.WordId)
                                    .Select(g => new { g.Key, Count = g.Count() })
                                    .ToDictionaryAsync(g => g.Key, g => g.Count);
        }

        var mediaByWord = media.GroupBy(m => m.WordId).ToDictionary(g => g.Key, g => (IReadOnlyList<UserCardMedia>)g.ToList());

        var results = new List<object>(items.Count);
        foreach (var item in items)
        {
            var ri = (byte)Math.Clamp(item.ReadingIndex, 0, byte.MaxValue);
            var wordMedia = mediaByWord.TryGetValue(item.WordId, out var list) ? list : [];
            var kanaCount = kanaCounts.GetValueOrDefault(item.WordId, 0);

            var (image, audio) = CardMediaResolver.Resolve(ri, wordMedia, kanaCount);

            results.Add(new
            {
                wordId = item.WordId,
                readingIndex = item.ReadingIndex,
                image = image is null ? null : ToDto(image.Media, image.Inherited, image.SourceReadingIndex),
                audio = audio is null ? null : ToDto(audio.Media, audio.Inherited, audio.SourceReadingIndex)
            });
        }

        return Results.Ok(new { items = results });
    }

    // ---- Helpers ------------------------------------------------------------

    private object ToDto(UserCardMedia media, bool inherited, byte sourceReadingIndex) => new
    {
        kind = media.Kind.ToString().ToLowerInvariant(),
        url = cdn.GetSignedUrl(media.StoragePath, SignedUrlTtl),
        contentType = media.ContentType,
        fileSizeBytes = media.FileSizeBytes,
        createdAt = media.CreatedAt,
        inherited,
        sourceReadingIndex
    };

    private static bool TryParseKind(string kind, out CardMediaKind mediaKind)
    {
        switch (kind?.ToLowerInvariant())
        {
            case "image":
                mediaKind = CardMediaKind.Image;
                return true;
            case "audio":
                mediaKind = CardMediaKind.Audio;
                return true;
            default:
                mediaKind = default;
                return false;
        }
    }
}
