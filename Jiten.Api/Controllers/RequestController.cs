using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jiten.Api.Dtos;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/requests")]
[Produces("application/json")]
[Authorize]
[EnableRateLimiting("fixed")]
public partial class RequestController(
    JitenDbContext context,
    UserDbContext userContext,
    ICurrentUserService currentUserService,
    RequestActivityService activityService,
    NotificationService notificationService,
    ICdnService cdnService,
    IMemoryCache memoryCache,
    ILogger<RequestController> logger) : ControllerBase
{
    private static readonly HashSet<string> AllowedExtensions =
        [".srt", ".ass", ".ssa", ".epub", ".zip", ".rar", ".7z", ".txt", ".mokuro"];

    private const long MaxUploadSize = 104_857_600; // 100MB
    private const long MaxUploadBytesPerDay = 500 * 1024 * 1024; // 500MB per 24h
    private const int RequestQuotaLimit = 20;
    private const int MaxCommentLength = 500;
    private const int CommentRateLimitPerFiveMin = 5;
    private const int CommentRateLimitPerHour = 25;

    private static readonly ConcurrentDictionary<string, object> UploadRateLimitLocks = new();
    private static readonly ConcurrentDictionary<string, object> CommentRateLimitLocks = new();
    private record CommentRateWindow(int Count, long WindowEndTicks);
    [HttpGet]
    public async Task<IResult> GetRequests(
        [FromQuery] MediaType? mediaType = null,
        [FromQuery] MediaRequestStatus? status = null,
        [FromQuery] string sort = "votes",
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 20,
        [FromQuery] bool mine = false,
        [FromQuery] bool contributed = false,
        [FromQuery] string? search = null,
        [FromQuery] string? attachments = null)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        limit = Math.Clamp(limit, 1, mine || contributed ? 200 : 50);

        var query = context.MediaRequests.AsNoTracking().AsQueryable();

        if (mine)
            query = query.Where(r => r.RequesterId == userId);
        else if (contributed)
        {
            var contributedRequestIds = context.MediaRequestComments
                .Where(c => c.UserId == userId)
                .Where(c => context.MediaRequestUploads.Any(u => u.MediaRequestCommentId == c.Id && !u.FileDeleted))
                .Select(c => c.MediaRequestId)
                .Distinct();
            query = query.Where(r => contributedRequestIds.Contains(r.Id) && r.RequesterId != userId);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var escaped = search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            query = query.Where(r => EF.Functions.ILike(r.Title, $"%{escaped}%", "\\"));
        }

        if (mediaType.HasValue)
            query = query.Where(r => r.MediaType == mediaType.Value);

        if (status.HasValue)
            query = query.Where(r => r.Status == status.Value);

        if (attachments == "yes")
            query = query.Where(r => context.MediaRequestUploads.Any(u => u.MediaRequestId == r.Id && !u.FileDeleted));
        else if (attachments == "no")
            query = query.Where(r => !context.MediaRequestUploads.Any(u => u.MediaRequestId == r.Id && !u.FileDeleted));

        var totalCount = await query.CountAsync();

        query = sort switch {
            "recent" => query.OrderByDescending(r => r.CreatedAt),
            "completed" => query.OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.CreatedAt),
            _ => query.OrderByDescending(r => r.UpvoteCount).ThenByDescending(r => r.CreatedAt),
        };

        var requests = await query.Skip(offset).Take(limit).ToListAsync();
        var requestIds = requests.Select(r => r.Id).ToList();

        var userUpvotes = await context.MediaRequestUpvotes
            .AsNoTracking()
            .Where(u => requestIds.Contains(u.MediaRequestId) && u.UserId == userId)
            .Select(u => u.MediaRequestId)
            .ToListAsync();

        var userSubscriptions = await context.MediaRequestSubscriptions
            .AsNoTracking()
            .Where(s => requestIds.Contains(s.MediaRequestId) && s.UserId == userId)
            .Select(s => s.MediaRequestId)
            .ToListAsync();

        var commentCounts = await context.MediaRequestComments
            .AsNoTracking()
            .Where(c => requestIds.Contains(c.MediaRequestId))
            .GroupBy(c => c.MediaRequestId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        var uploadCounts = await context.MediaRequestUploads
            .AsNoTracking()
            .Where(u => requestIds.Contains(u.MediaRequestId) && !u.FileDeleted)
            .GroupBy(u => u.MediaRequestId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        var fulfilledDeckIds = requests
            .Where(r => r.FulfilledDeckId.HasValue)
            .Select(r => r.FulfilledDeckId!.Value)
            .Distinct()
            .ToList();

        var deckTitles = fulfilledDeckIds.Count > 0
            ? await context.Decks.AsNoTracking()
                .Where(d => fulfilledDeckIds.Contains(d.DeckId))
                .ToDictionaryAsync(d => d.DeckId, d => d.OriginalTitle)
            : new Dictionary<int, string>();

        var isAdmin = User.IsInRole("Administrator");
        Dictionary<string, string?> requesterNames = new();
        if (isAdmin)
        {
            var requesterIds = requests.Select(r => r.RequesterId).Distinct().ToList();
            if (requesterIds.Count > 0)
            {
                requesterNames = await userContext.Users.AsNoTracking()
                    .Where(u => requesterIds.Contains(u.Id))
                    .Select(u => new { u.Id, u.UserName })
                    .ToDictionaryAsync(u => u.Id, u => u.UserName);
            }
        }

        var upvoteSet = new HashSet<int>(userUpvotes);
        var subSet = new HashSet<int>(userSubscriptions);

        var dtos = requests.Select(r => new MediaRequestDto
        {
            Id = r.Id,
            Title = r.Title,
            MediaType = r.MediaType,
            ExternalUrl = r.ExternalUrl,
            ExternalLinkType = r.ExternalLinkType,
            Description = r.Description,
            Status = r.Status,
            AdminNote = r.AdminNote,
            FulfilledDeckId = r.FulfilledDeckId,
            FulfilledDeckTitle = r.FulfilledDeckId.HasValue
                ? deckTitles.GetValueOrDefault(r.FulfilledDeckId.Value)
                : null,
            UpvoteCount = r.UpvoteCount,
            CommentCount = commentCounts.GetValueOrDefault(r.Id, 0),
            UploadCount = uploadCounts.GetValueOrDefault(r.Id, 0),
            HasUserUpvoted = upvoteSet.Contains(r.Id),
            IsSubscribed = subSet.Contains(r.Id),
            IsOwnRequest = r.RequesterId == userId,
            RequesterName = isAdmin ? requesterNames.GetValueOrDefault(r.RequesterId) : null,
            CreatedAt = r.CreatedAt,
            CompletedAt = r.CompletedAt
        }).ToList();

        return Results.Ok(new PaginatedResponse<List<MediaRequestDto>>(dtos, totalCount, limit, offset));
    }

    [HttpGet("{id:int}")]
    public async Task<IResult> GetRequest(int id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var request = await context.MediaRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id);

        if (request == null)
            return Results.NotFound("Request not found");

        var hasUpvoted = await context.MediaRequestUpvotes.AsNoTracking()
            .AnyAsync(u => u.MediaRequestId == id && u.UserId == userId);

        var isSubscribed = await context.MediaRequestSubscriptions.AsNoTracking()
            .AnyAsync(s => s.MediaRequestId == id && s.UserId == userId);

        var commentCount = await context.MediaRequestComments.AsNoTracking()
            .CountAsync(c => c.MediaRequestId == id);

        var uploadCount = await context.MediaRequestUploads.AsNoTracking()
            .CountAsync(u => u.MediaRequestId == id && !u.FileDeleted);

        string? deckTitle = null;
        if (request.FulfilledDeckId.HasValue)
        {
            deckTitle = await context.Decks.AsNoTracking()
                .Where(d => d.DeckId == request.FulfilledDeckId.Value)
                .Select(d => d.OriginalTitle)
                .FirstOrDefaultAsync();
        }

        var isAdmin = User.IsInRole("Administrator");
        string? requesterName = null;
        if (isAdmin)
        {
            requesterName = await userContext.Users.AsNoTracking()
                .Where(u => u.Id == request.RequesterId)
                .Select(u => u.UserName)
                .FirstOrDefaultAsync();
        }

        var dto = new MediaRequestDto
        {
            Id = request.Id,
            Title = request.Title,
            MediaType = request.MediaType,
            ExternalUrl = request.ExternalUrl,
            ExternalLinkType = request.ExternalLinkType,
            Description = request.Description,
            Status = request.Status,
            AdminNote = request.AdminNote,
            FulfilledDeckId = request.FulfilledDeckId,
            FulfilledDeckTitle = deckTitle,
            UpvoteCount = request.UpvoteCount,
            CommentCount = commentCount,
            UploadCount = uploadCount,
            HasUserUpvoted = hasUpvoted,
            IsSubscribed = isSubscribed,
            IsOwnRequest = request.RequesterId == userId,
            RequesterName = requesterName,
            CreatedAt = request.CreatedAt,
            CompletedAt = request.CompletedAt
        };

        return Results.Ok(dto);
    }

    [HttpPost]
    public async Task<IResult> CreateRequest([FromBody] CreateMediaRequestRequest model)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        if (!ModelState.IsValid)
            return Results.ValidationProblem(ModelState.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value!.Errors.Select(e => e.ErrorMessage).ToArray()));

        var trimmedTitle = model.Title.Trim();
        if (trimmedTitle.Length == 0)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["title"] = ["Title must not be empty or whitespace."]
            });

        if (!string.IsNullOrWhiteSpace(model.ExternalUrl) &&
            (!Uri.TryCreate(model.ExternalUrl.Trim(), UriKind.Absolute, out var parsedUrl) ||
             (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps)))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["externalUrl"] = ["ExternalUrl must be a valid http or https URL."]
            });

        var externalLinkType = !string.IsNullOrWhiteSpace(model.ExternalUrl)
            ? InferLinkType(model.ExternalUrl)
            : (LinkType?)null;

        var request = new MediaRequest
        {
            Title = trimmedTitle,
            MediaType = model.MediaType,
            ExternalUrl = model.ExternalUrl?.Trim(),
            ExternalLinkType = externalLinkType,
            Description = model.Description?.Trim(),
            RequesterId = userId,
            UpvoteCount = 1
        };

        try
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

            var activeCount = await context.MediaRequests
                .CountAsync(r => r.RequesterId == userId &&
                                 (r.Status == MediaRequestStatus.Open ||
                                  r.Status == MediaRequestStatus.InProgress));

            if (activeCount >= RequestQuotaLimit)
                return Results.Problem(
                    title: "Quota exceeded",
                    detail: $"You have reached the limit of {RequestQuotaLimit} active requests. Wait for existing requests to be fulfilled or rejected.",
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    extensions: new Dictionary<string, object?> { ["activeCount"] = activeCount, ["limit"] = RequestQuotaLimit });

            context.MediaRequests.Add(request);
            await context.SaveChangesAsync();

            // Auto-upvote
            context.MediaRequestUpvotes.Add(new MediaRequestUpvote
            {
                MediaRequestId = request.Id,
                UserId = userId
            });

            // Auto-subscribe
            context.MediaRequestSubscriptions.Add(new MediaRequestSubscription
            {
                MediaRequestId = request.Id,
                UserId = userId
            });

            activityService.LogWithoutSave(request.Id, userId, RequestAction.RequestCreated,
                JsonSerializer.Serialize(new { request.Title, mediaType = request.MediaType.ToString(), request.ExternalUrl }),
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

            await context.SaveChangesAsync();
            await transaction.CommitAsync();

            logger.LogInformation("Request created: Id={Id}, Title={Title}, UserId={UserId}",
                request.Id, request.Title, userId);

            return Results.Created($"/api/requests/{request.Id}", new { id = request.Id });
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "40001")
        {
            return Results.Problem(
                title: "Quota exceeded",
                detail: $"You have reached the limit of {RequestQuotaLimit} active requests.",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?> { ["activeCount"] = RequestQuotaLimit, ["limit"] = RequestQuotaLimit });
        }
    }

    [HttpGet("my-quota")]
    public async Task<IResult> GetMyQuota()
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var activeCount = await context.MediaRequests
            .CountAsync(r => r.RequesterId == userId &&
                             (r.Status == MediaRequestStatus.Open ||
                              r.Status == MediaRequestStatus.InProgress));

        return Results.Ok(new { activeCount, limit = RequestQuotaLimit });
    }

    [HttpDelete("{id:int}")]
    public async Task<IResult> DeleteRequest(int id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var request = await context.MediaRequests
            .Include(r => r.Comments)
            .Include(r => r.Upvotes)
            .Include(r => r.Subscriptions)
            .Include(r => r.Uploads)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (request == null)
            return Results.NotFound("Request not found");

        if (request.RequesterId != userId)
            return Results.Forbid();

        // Best-effort CDN cleanup for uploaded files
        foreach (var upload in request.Uploads.Where(u => !u.FileDeleted))
        {
            try
            {
                await cdnService.DeleteFile(upload.StoragePath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete CDN file {Path} for request {Id}", upload.StoragePath, id);
            }
        }

        // Log before deleting (activity logs are kept)
        activityService.LogWithoutSave(request.Id, userId, RequestAction.RequestDeleted,
            JsonSerializer.Serialize(new { request.Title }),
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

        context.MediaRequestUploads.RemoveRange(request.Uploads);
        context.MediaRequestComments.RemoveRange(request.Comments);
        context.MediaRequestUpvotes.RemoveRange(request.Upvotes);
        context.MediaRequestSubscriptions.RemoveRange(request.Subscriptions);
        context.MediaRequests.Remove(request);

        await context.SaveChangesAsync();

        logger.LogInformation("Request deleted: Id={Id}, UserId={UserId}", id, userId);

        return Results.Ok(new { success = true });
    }

    [HttpPost("{id:int}/upvote")]
    public async Task<IResult> ToggleUpvote(int id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var requestExists = await context.MediaRequests.AsNoTracking().AnyAsync(r => r.Id == id);
        if (!requestExists)
            return Results.NotFound("Request not found");

        var existing = await context.MediaRequestUpvotes
            .FirstOrDefaultAsync(u => u.MediaRequestId == id && u.UserId == userId);

        bool upvoted;
        await using var transaction = await context.Database.BeginTransactionAsync();

        if (existing != null)
        {
            context.MediaRequestUpvotes.Remove(existing);
            await context.SaveChangesAsync();

            await context.MediaRequests
                .Where(r => r.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.UpvoteCount, r => Math.Max(0, r.UpvoteCount - 1)));

            upvoted = false;
        }
        else
        {
            context.MediaRequestUpvotes.Add(new MediaRequestUpvote
            {
                MediaRequestId = id,
                UserId = userId
            });

            // Auto-subscribe on upvote (if not already subscribed)
            var isSubscribed = await context.MediaRequestSubscriptions
                .AnyAsync(s => s.MediaRequestId == id && s.UserId == userId);

            if (!isSubscribed)
            {
                context.MediaRequestSubscriptions.Add(new MediaRequestSubscription
                {
                    MediaRequestId = id,
                    UserId = userId
                });
            }

            await context.SaveChangesAsync();

            await context.MediaRequests
                .Where(r => r.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.UpvoteCount, r => r.UpvoteCount + 1));

            upvoted = true;
        }

        await transaction.CommitAsync();

        var upvoteCount = await context.MediaRequests.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => r.UpvoteCount)
            .FirstAsync();

        return Results.Ok(new { upvoted, upvoteCount = Math.Max(0, upvoteCount) });
    }

    [HttpGet("{id:int}/upvote")]
    public async Task<IResult> CheckUpvote(int id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var hasUpvoted = await context.MediaRequestUpvotes.AsNoTracking()
            .AnyAsync(u => u.MediaRequestId == id && u.UserId == userId);

        return Results.Ok(new { upvoted = hasUpvoted });
    }

    [HttpPost("{id:int}/subscribe")]
    public async Task<IResult> Subscribe(int id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var requestExists = await context.MediaRequests.AsNoTracking().AnyAsync(r => r.Id == id);
        if (!requestExists)
            return Results.NotFound("Request not found");

        var existing = await context.MediaRequestSubscriptions
            .AnyAsync(s => s.MediaRequestId == id && s.UserId == userId);

        if (existing)
            return Results.Ok(new { subscribed = true });

        context.MediaRequestSubscriptions.Add(new MediaRequestSubscription
        {
            MediaRequestId = id,
            UserId = userId
        });

        await context.SaveChangesAsync();
        return Results.Ok(new { subscribed = true });
    }

    [HttpDelete("{id:int}/subscribe")]
    public async Task<IResult> Unsubscribe(int id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var subscription = await context.MediaRequestSubscriptions
            .FirstOrDefaultAsync(s => s.MediaRequestId == id && s.UserId == userId);

        if (subscription == null)
            return Results.Ok(new { subscribed = false });

        context.MediaRequestSubscriptions.Remove(subscription);

        await context.SaveChangesAsync();
        return Results.Ok(new { subscribed = false });
    }

    [HttpGet("{id:int}/subscribe")]
    public async Task<IResult> CheckSubscription(int id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var isSubscribed = await context.MediaRequestSubscriptions.AsNoTracking()
            .AnyAsync(s => s.MediaRequestId == id && s.UserId == userId);

        return Results.Ok(new { subscribed = isSubscribed });
    }

    [HttpPost("{id:int}/comments")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadSize)]
    public async Task<IResult> AddComment(int id, [FromForm] string? text, [FromForm] IFormFile[]? files)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var hasText = !string.IsNullOrWhiteSpace(text);
        var hasFiles = files is { Length: > 0 };

        if (!hasText && !hasFiles)
            return Results.BadRequest("Comment text or files are required");

        if (hasText && text!.Trim().Length > MaxCommentLength)
            return Results.BadRequest($"Comment text must not exceed {MaxCommentLength} characters");

        var commentLock = CommentRateLimitLocks.GetOrAdd(userId, _ => new object());
        lock (commentLock)
        {
            var now = DateTime.UtcNow;
            if (!memoryCache.TryGetValue($"c5m:{userId}", out CommentRateWindow? w5) || w5 == null || now.Ticks >= w5.WindowEndTicks)
                w5 = new CommentRateWindow(0, now.AddMinutes(5).Ticks);
            if (!memoryCache.TryGetValue($"c1h:{userId}", out CommentRateWindow? w1h) || w1h == null || now.Ticks >= w1h.WindowEndTicks)
                w1h = new CommentRateWindow(0, now.AddHours(1).Ticks);

            if (w5.Count >= CommentRateLimitPerFiveMin)
                return Results.Problem("You are posting too frequently. Please wait before commenting again.", statusCode: 429);
            if (w1h.Count >= CommentRateLimitPerHour)
                return Results.Problem("You have reached the hourly comment limit. Please try again later.", statusCode: 429);

            memoryCache.Set($"c5m:{userId}", w5 with { Count = w5.Count + 1 }, TimeSpan.FromTicks(w5.WindowEndTicks - now.Ticks));
            memoryCache.Set($"c1h:{userId}", w1h with { Count = w1h.Count + 1 }, TimeSpan.FromTicks(w1h.WindowEndTicks - now.Ticks));
        }

        if (hasFiles)
        {
            foreach (var file in files!)
            {
                var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (!AllowedExtensions.Contains(ext))
                    return Results.BadRequest($"File type '{ext}' is not allowed. Accepted: {string.Join(", ", AllowedExtensions)}");
            }

            var totalSize = files.Sum(f => f.Length);
            if (totalSize > MaxUploadSize)
                return Results.BadRequest("Total file size exceeds 100MB");

            var cacheKey = $"upload_bytes:{userId}";
            var uploadLock = UploadRateLimitLocks.GetOrAdd(userId, _ => new object());
            lock (uploadLock)
            {
                var usedBytes = memoryCache.GetOrCreate(cacheKey, entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24);
                    return 0L;
                });

                if (usedBytes + totalSize > MaxUploadBytesPerDay)
                {
                    var remainingMb = Math.Max(0, (MaxUploadBytesPerDay - usedBytes) / (1024 * 1024));
                    return Results.Problem(
                        $"Upload limit of {MaxUploadBytesPerDay / (1024 * 1024)}MB per 24 hours exceeded. Remaining: {remainingMb}MB",
                        statusCode: 429);
                }

                memoryCache.Set(cacheKey, usedBytes + totalSize, TimeSpan.FromHours(24));
            }
        }

        var request = await context.MediaRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id);

        if (request == null)
            return Results.NotFound("Request not found");

        if (request.Status != MediaRequestStatus.Open && request.Status != MediaRequestStatus.InProgress)
            return Results.BadRequest("Comments are only allowed on open or in-progress requests");

        var comment = new MediaRequestComment
        {
            MediaRequestId = id,
            UserId = userId,
            Text = hasText ? text!.Trim() : null
        };

        string? uploadedStoragePath = null;

        await using var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            context.MediaRequestComments.Add(comment);
            await context.SaveChangesAsync();

            if (hasFiles)
            {
                int originalFileCount = files!.Length;
                var displayName = files.Length == 1
                    ? SanitiseFileName(files[0].FileName)
                    : string.Join(", ", files.Select(f => SanitiseFileName(f.FileName)));

                if (displayName.Length > 255)
                {
                    var suffix = originalFileCount > 1 ? $"... (+{originalFileCount} files)" : "...";
                    displayName = displayName[..(255 - suffix.Length)] + suffix;
                }

                using var ms = new MemoryStream();
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    foreach (var file in files)
                    {
                        var entry = archive.CreateEntry(SanitiseFileName(file.FileName), CompressionLevel.Fastest);
                        await using var entryStream = entry.Open();
                        await file.CopyToAsync(entryStream);
                    }
                }

                var fileBytes = ms.ToArray();
                var storagePath = $"requests/{id}/{Guid.NewGuid():N}.zip";
                await cdnService.UploadFile(fileBytes, storagePath);
                uploadedStoragePath = storagePath;

                var upload = new MediaRequestUpload
                {
                    MediaRequestCommentId = comment.Id,
                    MediaRequestId = id,
                    FileName = displayName,
                    StoragePath = storagePath,
                    FileSize = fileBytes.Length,
                    OriginalFileCount = originalFileCount
                };

                context.MediaRequestUploads.Add(upload);

                activityService.LogWithoutSave(id, userId, RequestAction.FileUploaded,
                    JsonSerializer.Serialize(new { commentId = comment.Id, fileName = displayName, fileSize = fileBytes.Length }),
                    ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

                await context.SaveChangesAsync();
            }
            else
            {
                var textPreview = comment.Text!.Length > 100 ? comment.Text[..100] + "..." : comment.Text;
                activityService.LogWithoutSave(id, userId, RequestAction.CommentAdded,
                    JsonSerializer.Serialize(new { commentId = comment.Id, textPreview }),
                    ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

                await context.SaveChangesAsync();
            }

            await transaction.CommitAsync();
        }
        catch (Exception) when (uploadedStoragePath != null)
        {
            logger.LogError("Failed to complete comment+upload transaction, cleaning up CDN file {Path}", uploadedStoragePath);
            try { await cdnService.DeleteFile(uploadedStoragePath); }
            catch { /* best effort */ }
            throw;
        }

        if (hasFiles)
        {
            try
            {
                if (userId != request.RequesterId)
                {
                    await notificationService.Notify(
                        request.RequesterId,
                        NotificationType.RequestFileUploaded,
                        "File uploaded to your request",
                        $"A file was uploaded to \"{request.Title}\".",
                        $"/requests/{id}");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send file upload notification for request {Id}", id);
            }
        }

        return Results.Ok(new { id = comment.Id });
    }

    [HttpPut("{id:int}/comments/{commentId:int}")]
    public async Task<IResult> EditComment(int id, int commentId, [FromBody] AddMediaRequestCommentRequest model)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var trimmedText = model.Text?.Trim();
        if (string.IsNullOrEmpty(trimmedText))
            return Results.BadRequest("Comment text must not be empty.");
        if (trimmedText.Length > MaxCommentLength)
            return Results.BadRequest($"Comment text must not exceed {MaxCommentLength} characters.");

        var request = await context.MediaRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        if (request == null)
            return Results.NotFound("Request not found");

        if (request.Status != MediaRequestStatus.Open && request.Status != MediaRequestStatus.InProgress)
            return Results.BadRequest("Comments cannot be edited on completed or rejected requests.");

        var comment = await context.MediaRequestComments.FirstOrDefaultAsync(c => c.Id == commentId && c.MediaRequestId == id);
        if (comment == null)
            return Results.NotFound("Comment not found");

        if (comment.UserId != userId)
            return Results.Forbid();

        comment.Text = trimmedText;
        comment.UpdatedAt = DateTime.UtcNow;

        var textPreview = trimmedText.Length > 100 ? trimmedText[..100] + "..." : trimmedText;
        activityService.LogWithoutSave(id, userId, RequestAction.CommentEdited,
            JsonSerializer.Serialize(new { commentId, textPreview }),
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

        await context.SaveChangesAsync();

        return Results.Ok(new { success = true });
    }

    [HttpPut("{id:int}/edit-description")]
    public async Task<IResult> EditDescription(int id, [FromBody] EditRequestDescriptionRequest model)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        if (!ModelState.IsValid)
            return Results.ValidationProblem(ModelState.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value!.Errors.Select(e => e.ErrorMessage).ToArray()));

        if (!string.IsNullOrWhiteSpace(model.ExternalUrl) &&
            (!Uri.TryCreate(model.ExternalUrl.Trim(), UriKind.Absolute, out var parsedUrl) ||
             (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps)))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["externalUrl"] = ["ExternalUrl must be a valid http or https URL."]
            });

        var request = await context.MediaRequests.FirstOrDefaultAsync(r => r.Id == id);
        if (request == null)
            return Results.NotFound("Request not found");

        if (request.RequesterId != userId)
            return Results.Forbid();

        if (request.Status != MediaRequestStatus.Open && request.Status != MediaRequestStatus.InProgress)
            return Results.BadRequest("Request cannot be edited in its current status.");

        var externalLinkType = !string.IsNullOrWhiteSpace(model.ExternalUrl)
            ? InferLinkType(model.ExternalUrl)
            : (LinkType?)null;

        request.Description = model.Description?.Trim();
        request.ExternalUrl = model.ExternalUrl?.Trim();
        request.ExternalLinkType = externalLinkType;
        request.UpdatedAt = DateTime.UtcNow;

        activityService.LogWithoutSave(id, userId, RequestAction.RequestEditedByRequester,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

        await context.SaveChangesAsync();

        return Results.Ok(new { success = true });
    }

    [HttpGet("{id:int}/comments")]
    public async Task<IResult> GetComments(int id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var isAdmin = User.IsInRole("Administrator");

        var request = await context.MediaRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id);

        if (request == null)
            return Results.NotFound("Request not found");

        var comments = await context.MediaRequestComments.AsNoTracking()
            .Include(c => c.Upload)
            .Where(c => c.MediaRequestId == id)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync();

        Dictionary<string, string?> userNames = new();
        if (isAdmin)
        {
            var allUserIds = comments.Select(c => c.UserId).Distinct().ToList();

            if (allUserIds.Count > 0)
            {
                var users = await userContext.Users
                    .AsNoTracking()
                    .Where(u => allUserIds.Contains(u.Id))
                    .Select(u => new { u.Id, u.UserName })
                    .ToListAsync();

                userNames = users.ToDictionary(u => u.Id, u => u.UserName);
            }
        }

        var dtos = comments.Select(c =>
        {
            var hasUpload = c.Upload != null;
            var role = c.UserId == request.RequesterId ? "Requester" : "Contributor";

            MediaRequestUploadDto? uploadDto = null;
            if (hasUpload)
            {
                if (isAdmin)
                {
                    uploadDto = new MediaRequestUploadAdminDto
                    {
                        Id = c.Upload!.Id,
                        FileName = c.Upload.FileName,
                        FileSize = c.Upload.FileSize,
                        OriginalFileCount = c.Upload.OriginalFileCount,
                        CreatedAt = c.Upload.CreatedAt,
                        UploaderName = userNames.GetValueOrDefault(c.UserId),
                        AdminReviewed = c.Upload.AdminReviewed,
                        AdminNote = c.Upload.AdminNote,
                        FileDeleted = c.Upload.FileDeleted
                    };
                }
                else
                {
                    uploadDto = new MediaRequestUploadDto
                    {
                        Id = c.Upload!.Id,
                        FileName = c.Upload.FileName,
                        FileSize = c.Upload.FileSize,
                        OriginalFileCount = c.Upload.OriginalFileCount,
                        CreatedAt = c.Upload.CreatedAt
                    };
                }
            }

            return new MediaRequestCommentDto
            {
                Id = c.Id,
                Text = c.Text,
                Role = role,
                IsOwnComment = c.UserId == userId,
                UserName = isAdmin ? userNames.GetValueOrDefault(c.UserId) : null,
                Upload = uploadDto,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt
            };
        }).ToList();

        return Results.Ok(dtos);
    }

    [HttpGet("duplicate-check")]
    public async Task<IResult> DuplicateCheck([FromQuery] string? title)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var normalisedTitle = title?.Trim() ?? string.Empty;
        if (normalisedTitle.Length < 2)
            return Results.Ok(new DuplicateCheckResultDto());

        var likePattern = $"%{EscapeLikePattern(normalisedTitle)}%";

        // Search existing decks by title (simple ILIKE)
        var existingDecks = await context.DeckTitles.AsNoTracking()
            .Where(dt => EF.Functions.ILike(dt.Title, likePattern, "\\"))
            .OrderBy(dt => dt.Title.Length)
            .Take(5)
            .Select(dt => new DuplicateCheckDeckDto
            {
                DeckId = dt.DeckId,
                Title = dt.Title,
                MediaType = dt.Deck!.MediaType
            })
            .ToListAsync();

        // Deduplicate by DeckId
        existingDecks = existingDecks
            .GroupBy(d => d.DeckId)
            .Select(g => g.First())
            .Take(5)
            .ToList();

        // Search open/in-progress requests
        var existingRequests = await context.MediaRequests.AsNoTracking()
            .Where(r => (r.Status == MediaRequestStatus.Open || r.Status == MediaRequestStatus.InProgress)
                        && EF.Functions.ILike(r.Title, likePattern, "\\"))
            .OrderBy(r => r.Title.Length)
            .Take(5)
            .Select(r => new DuplicateCheckRequestDto
            {
                Id = r.Id,
                Title = r.Title,
                MediaType = r.MediaType,
                Status = r.Status,
                UpvoteCount = r.UpvoteCount
            })
            .ToListAsync();

        return Results.Ok(new DuplicateCheckResultDto
        {
            ExistingDecks = existingDecks,
            ExistingRequests = existingRequests
        });
    }

    // Admin endpoints

    [HttpPut("{id:int}/status")]
    [Authorize("RequiresAdmin")]
    public async Task<IResult> UpdateStatus(int id, [FromBody] UpdateRequestStatusRequest model)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var request = await context.MediaRequests.FirstOrDefaultAsync(r => r.Id == id);
        if (request == null)
            return Results.NotFound("Request not found");

        // Same-status no-op
        if (request.Status == model.Status)
            return Results.Ok(new { success = true });

        // Validate transition
        var allowed = GetAllowedTransitions(request.Status);
        if (!allowed.Contains(model.Status))
            return Results.Conflict($"Cannot transition from {request.Status} to {model.Status}");

        // Validate payload rules
        if (model.Status == MediaRequestStatus.Completed)
        {
            if (!model.FulfilledDeckId.HasValue)
                return Results.BadRequest("fulfilledDeckId is required when completing a request");

            var deckExists = await context.Decks.AsNoTracking()
                .AnyAsync(d => d.DeckId == model.FulfilledDeckId.Value);
            if (!deckExists)
                return Results.BadRequest("Referenced deck does not exist");
        }

        if (model.Status == MediaRequestStatus.Rejected)
        {
            if (string.IsNullOrWhiteSpace(model.AdminNote))
                return Results.BadRequest("adminNote is required when rejecting a request");
        }

        if (model.Status != MediaRequestStatus.Completed && model.FulfilledDeckId.HasValue)
            return Results.BadRequest("fulfilledDeckId must be omitted for non-Completed status");

        request.Status = model.Status;
        request.AdminNote = model.AdminNote;
        request.FulfilledDeckId = model.FulfilledDeckId;
        request.UpdatedAt = DateTime.UtcNow;
        if (model.Status is MediaRequestStatus.Completed or MediaRequestStatus.Rejected)
            request.CompletedAt = DateTime.UtcNow;

        var action = model.Status switch
        {
            MediaRequestStatus.Open => RequestAction.StatusChangedToOpen,
            MediaRequestStatus.InProgress => RequestAction.StatusChangedToInProgress,
            MediaRequestStatus.Completed => RequestAction.StatusChangedToCompleted,
            MediaRequestStatus.Rejected => RequestAction.StatusChangedToRejected,
            _ => RequestAction.StatusChangedToInProgress
        };

        var detail = model.Status switch
        {
            MediaRequestStatus.Completed => JsonSerializer.Serialize(new { deckId = model.FulfilledDeckId, model.AdminNote }),
            MediaRequestStatus.Rejected => JsonSerializer.Serialize(new { model.AdminNote }),
            _ => null
        };

        activityService.LogWithoutSave(id, userId, action, detail,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

        await context.SaveChangesAsync();

        // Best-effort notification dispatch
        try
        {
            var notifTitle = model.Status switch
            {
                MediaRequestStatus.Open => "Request reopened",
                MediaRequestStatus.InProgress => "Request in progress",
                MediaRequestStatus.Completed => "Request completed",
                MediaRequestStatus.Rejected => "Request rejected",
                _ => "Request updated"
            };

            var notifMessage = model.Status switch
            {
                MediaRequestStatus.Open => $"\"{request.Title}\" has been reopened.",
                MediaRequestStatus.InProgress => $"\"{request.Title}\" is now being worked on.",
                MediaRequestStatus.Completed => string.IsNullOrWhiteSpace(model.AdminNote)
                    ? $"\"{request.Title}\" has been completed!"
                    : $"\"{request.Title}\" has been completed! Note: {model.AdminNote}",
                MediaRequestStatus.Rejected => $"\"{request.Title}\" has been rejected. Reason: {model.AdminNote}",
                _ => $"\"{request.Title}\" status has changed."
            };

            var notifType = model.Status == MediaRequestStatus.Completed
                ? NotificationType.RequestCompleted
                : NotificationType.RequestStatusChanged;

            var linkUrl = model.Status == MediaRequestStatus.Completed && model.FulfilledDeckId.HasValue
                ? $"/decks/media/{model.FulfilledDeckId.Value}/detail"
                : $"/requests/{id}";

            if (model.Status == MediaRequestStatus.Open || model.Status == MediaRequestStatus.InProgress)
            {
                if (request.RequesterId != userId)
                    await notificationService.Notify(request.RequesterId, notifType, notifTitle, notifMessage, linkUrl);
            }
            else if (model.Status == MediaRequestStatus.Rejected || model.Status == MediaRequestStatus.Completed)
            {
                var subscriberIds = await context.MediaRequestSubscriptions.AsNoTracking()
                    .Where(s => s.MediaRequestId == id && s.UserId != userId)
                    .Select(s => s.UserId)
                    .ToListAsync();

                if (!subscriberIds.Contains(request.RequesterId) && request.RequesterId != userId)
                    subscriberIds.Add(request.RequesterId);

                await notificationService.NotifyMany(subscriberIds, notifType, notifTitle, notifMessage, linkUrl);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send notifications for request {Id}", id);
        }

        logger.LogInformation("Request status updated: Id={Id}, Status={Status}, AdminUserId={UserId}",
            id, model.Status, userId);

        return Results.Ok(new { success = true });
    }

    [HttpGet("{id:int}/activity-log")]
    [Authorize("RequiresAdmin")]
    public async Task<IResult> GetActivityLog(int id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var logs = await context.RequestActivityLogs.AsNoTracking()
            .Where(l => l.MediaRequestId == id)
            .OrderByDescending(l => l.CreatedAt)
            .Take(100)
            .ToListAsync();

        var userIds = logs.Select(l => l.UserId).Distinct().ToList();
        var userNames = userIds.Count > 0
            ? await userContext.Users.AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.UserName)
            : new Dictionary<string, string?>();

        var dtos = logs.Select(l => new RequestActivityLogDto
        {
            Id = l.Id,
            MediaRequestId = l.MediaRequestId,
            UserId = l.UserId,
            UserName = userNames.GetValueOrDefault(l.UserId),
            TargetUserId = l.TargetUserId,
            Action = l.Action,
            Detail = l.Detail,
            CreatedAt = l.CreatedAt
        }).ToList();

        return Results.Ok(dtos);
    }

    [HttpGet("~/api/admin/request-activity")]
    [Authorize("RequiresAdmin")]
    public async Task<IResult> GetGlobalActivityLog(
        [FromQuery] string? userId = null,
        [FromQuery] RequestAction? action = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50)
    {
        var currentUserId = currentUserService.UserId;
        if (string.IsNullOrEmpty(currentUserId))
            return Results.Unauthorized();

        limit = Math.Clamp(limit, 1, 100);

        var query = context.RequestActivityLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(userId))
            query = query.Where(l => l.UserId == userId);

        if (action.HasValue)
            query = query.Where(l => l.Action == action.Value);

        var totalCount = await query.CountAsync();

        var logs = await query
            .OrderByDescending(l => l.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();

        // Fetch request titles for display
        var requestIds = logs.Where(l => l.MediaRequestId.HasValue)
            .Select(l => l.MediaRequestId!.Value)
            .Distinct()
            .ToList();

        var requestTitles = requestIds.Count > 0
            ? await context.MediaRequests.AsNoTracking()
                .Where(r => requestIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => r.Title)
            : new Dictionary<int, string>();

        var userIds = logs.Select(l => l.UserId).Distinct().ToList();
        var userNames = userIds.Count > 0
            ? await userContext.Users.AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.UserName)
            : new Dictionary<string, string?>();

        var dtos = logs.Select(l => new RequestActivityLogDto
        {
            Id = l.Id,
            MediaRequestId = l.MediaRequestId,
            RequestTitle = l.MediaRequestId.HasValue
                ? requestTitles.GetValueOrDefault(l.MediaRequestId.Value)
                : null,
            UserId = l.UserId,
            UserName = userNames.GetValueOrDefault(l.UserId),
            TargetUserId = l.TargetUserId,
            Action = l.Action,
            Detail = l.Detail,
            CreatedAt = l.CreatedAt
        }).ToList();

        return Results.Ok(new PaginatedResponse<List<RequestActivityLogDto>>(dtos, totalCount, limit, offset));
    }

    [HttpGet("~/api/admin/request-user-summary/{targetUserId}")]
    [Authorize("RequiresAdmin")]
    public async Task<IResult> GetUserSummary(string targetUserId)
    {
        var currentUserId = currentUserService.UserId;
        if (string.IsNullOrEmpty(currentUserId))
            return Results.Unauthorized();

        var summary = new RequestUserSummaryDto
        {
            RequestCount = await context.MediaRequests.AsNoTracking()
                .CountAsync(r => r.RequesterId == targetUserId),
            UpvoteCount = await context.MediaRequestUpvotes.AsNoTracking()
                .CountAsync(u => u.UserId == targetUserId),
            SubscriptionCount = await context.MediaRequestSubscriptions.AsNoTracking()
                .CountAsync(s => s.UserId == targetUserId),
            UploadCount = await context.MediaRequestUploads.AsNoTracking()
                .CountAsync(u => u.Comment != null && u.Comment.UserId == targetUserId),
            FulfilledCount = await context.MediaRequests.AsNoTracking()
                .CountAsync(r => r.RequesterId == targetUserId && r.Status == MediaRequestStatus.Completed)
        };

        return Results.Ok(summary);
    }

    [HttpDelete("{id:int}/uploads/{uploadId:int}")]
    [Authorize("RequiresAdmin")]
    public async Task<IResult> DeleteUpload(int id, int uploadId)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var upload = await context.MediaRequestUploads
            .Include(u => u.Comment)
            .FirstOrDefaultAsync(u => u.Id == uploadId && u.MediaRequestId == id);

        if (upload == null)
            return Results.NotFound("Upload not found");

        if (!upload.FileDeleted)
        {
            try
            {
                await cdnService.DeleteFile(upload.StoragePath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete CDN file {Path}", upload.StoragePath);
            }
        }

        activityService.LogWithoutSave(id, userId, RequestAction.FileDeletedByAdmin,
            JsonSerializer.Serialize(new { uploadId, upload.FileName }),
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

        upload.FileDeleted = true;

        await context.SaveChangesAsync();

        logger.LogInformation("Upload deleted by admin: UploadId={UploadId}, RequestId={RequestId}, AdminUserId={UserId}",
            uploadId, id, userId);

        return Results.Ok(new { success = true });
    }

    [HttpPut("{id:int}/uploads/{uploadId:int}/review")]
    [Authorize("RequiresAdmin")]
    public async Task<IResult> ReviewUpload(int id, int uploadId, [FromBody] AdminReviewUploadRequest model)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var upload = await context.MediaRequestUploads
            .Include(u => u.Comment)
            .FirstOrDefaultAsync(u => u.Id == uploadId && u.MediaRequestId == id);

        if (upload == null)
            return Results.NotFound("Upload not found");

        upload.AdminReviewed = model.AdminReviewed;
        upload.AdminNote = model.AdminNote;

        var uploaderUserId = upload.Comment?.UserId;
        if (uploaderUserId != null)
        {
            var action = model.AdminReviewed ? RequestAction.ContributionValidated : RequestAction.ContributionRevoked;
            activityService.LogWithoutSave(id, userId, action,
                JsonSerializer.Serialize(new { uploadId, upload.FileName }),
                targetUserId: uploaderUserId,
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());
        }

        await context.SaveChangesAsync();

        return Results.Ok(new { success = true });
    }

    [HttpGet("{id:int}/uploads/{uploadId:int}/download")]
    [Authorize("RequiresAdmin")]
    public async Task<IResult> DownloadUpload(int id, int uploadId)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var upload = await context.MediaRequestUploads.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == uploadId && u.MediaRequestId == id);

        if (upload == null)
            return Results.NotFound("Upload not found");

        if (upload.FileDeleted)
            return Results.Problem("File has been deleted", statusCode: 410);

        var cdnUrl = cdnService.GetCdnUrl(upload.StoragePath);
        return Results.Ok(new { url = cdnUrl });
    }

    [HttpPut("{id:int}/edit")]
    [Authorize("RequiresAdmin")]
    public async Task<IResult> EditRequest(int id, [FromBody] AdminEditMediaRequestRequest model)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        if (!ModelState.IsValid)
            return Results.ValidationProblem(ModelState.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value!.Errors.Select(e => e.ErrorMessage).ToArray()));

        var trimmedTitle = model.Title.Trim();
        if (trimmedTitle.Length == 0)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["title"] = ["Title must not be empty or whitespace."]
            });

        if (!string.IsNullOrWhiteSpace(model.ExternalUrl) &&
            (!Uri.TryCreate(model.ExternalUrl.Trim(), UriKind.Absolute, out var parsedUrl) ||
             (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps)))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["externalUrl"] = ["ExternalUrl must be a valid http or https URL."]
            });

        var request = await context.MediaRequests.FirstOrDefaultAsync(r => r.Id == id);
        if (request == null)
            return Results.NotFound("Request not found");

        var oldTitle = request.Title;
        var externalLinkType = !string.IsNullOrWhiteSpace(model.ExternalUrl)
            ? InferLinkType(model.ExternalUrl)
            : (LinkType?)null;

        request.Title = trimmedTitle;
        request.MediaType = model.MediaType;
        request.ExternalUrl = model.ExternalUrl?.Trim();
        request.ExternalLinkType = externalLinkType;
        request.Description = model.Description?.Trim();
        request.UpdatedAt = DateTime.UtcNow;

        activityService.LogWithoutSave(id, userId, RequestAction.RequestEditedByAdmin,
            JsonSerializer.Serialize(new { oldTitle, newTitle = request.Title }),
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

        await context.SaveChangesAsync();

        logger.LogInformation("Request edited by admin: Id={Id}, AdminUserId={UserId}", id, userId);

        return Results.Ok(new { success = true });
    }

    private static HashSet<MediaRequestStatus> GetAllowedTransitions(MediaRequestStatus current) => current switch
    {
        MediaRequestStatus.Open => [MediaRequestStatus.InProgress, MediaRequestStatus.Rejected, MediaRequestStatus.Completed],
        MediaRequestStatus.InProgress => [MediaRequestStatus.Rejected, MediaRequestStatus.Completed],
        MediaRequestStatus.Completed => [MediaRequestStatus.Open],
        MediaRequestStatus.Rejected => [MediaRequestStatus.Open],
        _ => []
    };

    private static string SanitiseFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return SanitiseFileNameRegex().Replace(name, "_");
    }

    private static string EscapeLikePattern(string input) =>
        input.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1F]")]
    private static partial Regex SanitiseFileNameRegex();

    private static LinkType InferLinkType(string url)
    {
        try
        {
            var uri = new Uri(url);
            var host = uri.Host.ToLowerInvariant();

            if (host.Contains("anilist.co")) return LinkType.Anilist;
            if (host.Contains("vndb.org")) return LinkType.Vndb;
            if (host.Contains("themoviedb.org")) return LinkType.Tmdb;
            if (host.Contains("myanimelist.net")) return LinkType.Mal;
            if (host.Contains("imdb.com")) return LinkType.Imdb;
            if (host.Contains("igdb.com")) return LinkType.Igdb;
            if (host.Contains("syosetu.com")) return LinkType.Syosetsu;
            if (host.Contains("google.") && uri.AbsolutePath.Contains("/books/")) return LinkType.GoogleBooks;

            return LinkType.Web;
        }
        catch
        {
            return LinkType.Web;
        }
    }
}
