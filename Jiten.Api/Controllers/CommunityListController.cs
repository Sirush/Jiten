using System.Globalization;
using System.Text;
using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Billing;
using Jiten.Core.Data.CommunityLists;
using Jiten.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/lists")]
[Produces("application/json")]
[Authorize]
[EnableRateLimiting("fixed")]
public class CommunityListController(
    JitenDbContext context,
    UserDbContext userContext,
    ICurrentUserService currentUserService,
    IJitenPlusService jitenPlus,
    IUserLimitsService userLimits,
    NotificationService notificationService,
    ICommunityListActivityBuffer activityBuffer,
    IDiscordReportNotifier discordReports) : ControllerBase
{
    public const int MaxGenres = 3;
    public const int MaxTags = 5;
    private const int DefaultItemPageSize = 50;
    private const int MaxItemPageSize = 100;
    private const int PreviewCoverCount = 6;
    private const int DefaultFavouritedPageSize = 48;
    private const int MaxFilterIds = 50;
    private const string CoverageSort = "coverage";

    /// <summary>Advisory lock namespace for per-owner list creation; distinct from every other two-key lock class.</summary>
    private const int OwnerLockClass = 0x434C53;

    /// <summary>Owners hear once about each milestone; a jump past several announces only the highest.</summary>
    private static readonly int[] FavouriteMilestones = [10, 25, 50, 100, 250, 500, 1000];

    private bool IsAdmin => User.IsInRole("Administrator");

    private bool CanManage(string ownerId) => currentUserService.UserId is { } viewerId && (ownerId == viewerId || IsAdmin);

    [HttpGet]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "Browse public community lists")]
    public async Task<IResult> Browse(
        [FromQuery] string sort = "top",
        [FromQuery] int? seed = null,
        [FromQuery] int days = 7,
        [FromQuery] string? q = null,
        [FromQuery] string? genres = null,
        [FromQuery] string? excludeGenres = null,
        [FromQuery] string? tags = null,
        [FromQuery] string? excludeTags = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 24)
    {
        limit = Math.Clamp(limit, 1, 50);
        offset = Math.Max(0, offset);

        var filter = new GenreTagFilter(genres, excludeGenres, tags, excludeTags);
        if (TooManyFilterIds(filter))
            return FilterLimitError();

        var query = PublicMatching(q, filter);
        var total = await query.CountAsync();

        int? usedSeed = null;
        if (sort == "random")
        {
            usedSeed = seed is > 0 ? seed.Value : Random.Shared.Next(1, int.MaxValue);
            query = OrderRandomly(query, usedSeed.Value);
        }
        else if (sort == "popular")
        {
            var since = DateTime.UtcNow.AddDays(-(days == 30 ? 30 : 7));
            query = query.OrderByDescending(l => context.CommunityListFavourites.Count(f => f.ListId == l.ListId && f.CreatedAt >= since))
                         .ThenByDescending(l => l.FavouriteCount)
                         .ThenByDescending(l => l.ListId);
        }
        else
        {
            query = sort switch
            {
                "newest" => query.OrderByDescending(l => l.PublishedAt).ThenByDescending(l => l.ListId),
                "updated" => query.OrderByDescending(l => l.UpdatedAt).ThenByDescending(l => l.ListId),
                _ => query.OrderByDescending(l => l.FavouriteCount).ThenByDescending(l => l.ListId)
            };
        }

        var lists = await query.Skip(offset).Take(limit).ToListAsync();
        var dtos = await BuildSummariesAsync<CommunityListSummaryDto>(lists, currentUserService.UserId);

        return Results.Ok(new CommunityListBrowseResponse(dtos, total, limit, offset, usedSeed));
    }

    [HttpGet("facets")]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "Genre and tag counts over the public lists matching a browse query")]
    public async Task<IResult> GetFacets(
        [FromQuery] string? q = null,
        [FromQuery] string? genres = null,
        [FromQuery] string? excludeGenres = null,
        [FromQuery] string? tags = null,
        [FromQuery] string? excludeTags = null)
    {
        var filter = new GenreTagFilter(genres, excludeGenres, tags, excludeTags);
        if (TooManyFilterIds(filter))
            return FilterLimitError();

        var query = PublicMatching(q, filter);

        var genreCounts = await query.SelectMany(l => l.Genres)
                                     .GroupBy(g => g.Genre)
                                     .Select(g => new { g.Key, Count = g.Count() })
                                     .ToDictionaryAsync(g => (int)g.Key, g => g.Count);
        var tagCounts = await query.SelectMany(l => l.Tags)
                                   .GroupBy(t => t.TagId)
                                   .Select(g => new { g.Key, Count = g.Count() })
                                   .ToDictionaryAsync(g => g.Key, g => g.Count);

        return Results.Ok(new { genres = genreCounts, tags = tagCounts });
    }

    [HttpGet("{id:int}")]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "Get a community list with its first page of items")]
    public async Task<IResult> Get(int id, [FromQuery] int limit = DefaultItemPageSize, [FromQuery] string? sort = null)
    {
        var list = await LoadVisibleAsync(id);
        if (list == null)
            return Results.NotFound();

        var viewerId = currentUserService.UserId;
        var dto = (await BuildSummariesAsync<CommunityListDetailDto>([list], viewerId))[0];
        dto.CanEdit = CanManage(list.OwnerId);

        var items = await LoadCuratorOrderAsync(id);
        var itemDeckIds = items.Select(i => i.DeckId).ToList();

        Dictionary<int, (DeckStatus Status, bool IsFavourite)>? statuses = null;
        UserCoverageChunkHelper.CoverageDictionaries? coverage = null;
        if (viewerId != null)
        {
            statuses = await MediaListStatusHelper.GetDisplayStatusesAsync(userContext, context, viewerId);
            dto.StatusCounts = itemDeckIds.Where(statuses.ContainsKey)
                                          .GroupBy(deckId => statuses[deckId].Status)
                                          .Select(g => new CommunityListStatusCountDto { Status = g.Key, Count = g.Count() })
                                          .ToList();

            coverage = await GetViewerCoverageAsync(viewerId, itemDeckIds);
            dto.Coverage = await BuildCoverageSummaryAsync(itemDeckIds, coverage.MatureCoverage);
        }

        dto.ItemCount = items.Count;
        dto.Items = await BuildItemPageAsync(Arrange(items, sort, coverage?.MatureCoverage), 0, Math.Clamp(limit, 1, MaxItemPageSize), viewerId,
                                             statuses, coverage);

        return Results.Ok(dto);
    }

    [HttpGet("{id:int}/items")]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "Get a page of a community list's items", Description = "Curator order by default; sort=coverage puts the viewer's best-covered titles first.")]
    public async Task<IResult> GetItems(int id, [FromQuery] int offset = 0, [FromQuery] int limit = DefaultItemPageSize, [FromQuery] string? sort = null)
    {
        var list = await LoadVisibleAsync(id);
        if (list == null)
            return Results.NotFound();

        var viewerId = currentUserService.UserId;
        var statuses = viewerId != null ? await MediaListStatusHelper.GetDisplayStatusesAsync(userContext, context, viewerId) : null;
        var items = await LoadCuratorOrderAsync(id);
        var coverage = viewerId != null && sort == CoverageSort
            ? await GetViewerCoverageAsync(viewerId, items.Select(i => i.DeckId).ToList())
            : null;

        return Results.Ok(await BuildItemPageAsync(Arrange(items, sort, coverage?.MatureCoverage), Math.Max(0, offset), Math.Clamp(limit, 1, MaxItemPageSize),
                                                   viewerId, statuses, coverage));
    }

    [HttpGet("{id:int}/edit")]
    [SwaggerOperation(Summary = "Get a community list in full for its editor")]
    public async Task<IResult> GetForEdit(int id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var list = await context.CommunityLists.AsNoTracking()
                                .Include(l => l.Genres)
                                .Include(l => l.Tags).ThenInclude(t => t.Tag)
                                .FirstOrDefaultAsync(l => l.ListId == id);
        if (list == null || !CanManage(list.OwnerId))
            return Results.NotFound();

        var items = await context.CommunityListItems.AsNoTracking()
                                 .Where(i => i.ListId == id)
                                 .OrderBy(i => i.Position)
                                 .Select(i => new CommunityListEditItemDto
                                 {
                                     DeckId = i.DeckId,
                                     Note = i.Note,
                                     OriginalTitle = i.Deck.OriginalTitle,
                                     RomajiTitle = i.Deck.RomajiTitle,
                                     EnglishTitle = i.Deck.EnglishTitle,
                                     MediaType = i.Deck.MediaType,
                                     CoverName = i.Deck.CoverName
                                 })
                                 .ToListAsync();

        var deckIds = items.Select(i => i.DeckId).ToList();
        var deckGenres = await context.DeckGenres.AsNoTracking()
                                      .Where(g => deckIds.Contains(g.DeckId))
                                      .Select(g => new { g.DeckId, g.Genre })
                                      .ToListAsync();
        var deckTags = await context.DeckTags.AsNoTracking()
                                    .Where(t => deckIds.Contains(t.DeckId))
                                    .Select(t => new { t.DeckId, t.TagId, t.Tag.Name, t.Percentage })
                                    .ToListAsync();
        var genresByDeck = deckGenres.ToLookup(g => g.DeckId, g => g.Genre);
        var tagsByDeck = deckTags.ToLookup(t => t.DeckId);
        foreach (var item in items)
        {
            item.Genres = genresByDeck[item.DeckId].ToList();
            item.Tags = tagsByDeck[item.DeckId]
                        .Select(t => new TagWithPercentageDto { TagId = t.TagId, Name = t.Name, Percentage = t.Percentage })
                        .ToList();
        }

        return Results.Ok(new CommunityListEditDto
        {
            ListId = list.ListId,
            Slug = list.Slug,
            Name = list.Name,
            Description = list.Description,
            Status = list.Status,
            FavouriteCount = list.FavouriteCount,
            Genres = list.Genres.Select(g => g.Genre).OrderBy(g => g).ToList(),
            Tags = list.Tags.Select(t => new TagDto { TagId = t.TagId, Name = t.Tag.Name }).OrderBy(t => t.Name).ToList(),
            Items = items,
            Quota = await GetQuotaAsync(list.OwnerId)
        });
    }

    [HttpGet("mine")]
    [SwaggerOperation(Summary = "Get the caller's community lists in every status, with their caps")]
    public async Task<IResult> GetMine()
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var lists = await context.CommunityLists.AsNoTracking()
                                 .Where(l => l.OwnerId == userId)
                                 .OrderByDescending(l => l.UpdatedAt)
                                 .ToListAsync();

        return Results.Ok(new CommunityListMineDto
        {
            Lists = await BuildSummariesAsync<CommunityListSummaryDto>(lists, userId),
            Quota = await GetQuotaAsync(userId)
        });
    }

    [HttpGet("quota")]
    [SwaggerOperation(Summary = "Get the caller's community list caps and usage")]
    public async Task<IResult> GetQuota()
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        return Results.Ok(await GetQuotaAsync(userId));
    }

    [HttpGet("favourited")]
    [SwaggerOperation(Summary = "Get a page of the public lists the caller favourited, most recent first")]
    public async Task<IResult> GetFavourited([FromQuery] int offset = 0, [FromQuery] int limit = DefaultFavouritedPageSize)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        limit = Math.Clamp(limit, 1, MaxItemPageSize);
        offset = Math.Max(0, offset);

        var query = context.CommunityListFavourites.AsNoTracking()
                           .Where(f => f.UserId == userId)
                           .Join(context.CommunityLists.Where(l => l.Status == CommunityListStatus.Public),
                                 f => f.ListId, l => l.ListId, (f, l) => new { f.CreatedAt, List = l });
        var total = await query.CountAsync();
        var lists = await query.OrderByDescending(x => x.CreatedAt)
                               .ThenByDescending(x => x.List.ListId)
                               .Skip(offset)
                               .Take(limit)
                               .Select(x => x.List)
                               .ToListAsync();

        var dtos = await BuildSummariesAsync<CommunityListSummaryDto>(lists, userId);
        return Results.Ok(new PaginatedResponse<List<CommunityListSummaryDto>>(dtos, total, limit, offset));
    }

    [HttpPost]
    [SwaggerOperation(Summary = "Create a draft community list")]
    public async Task<IResult> Create([FromBody] CreateCommunityListRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var displayName = await userContext.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.DisplayName).FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(displayName))
            return Error("display_name_required", "Choose a display name before creating a list.");

        var name = UserTextSanitizer.Clean(request.Name, multiline: false) ?? "";
        var description = UserTextSanitizer.Clean(request.Description, multiline: true);
        if (ValidateText(name, description) is { } invalid)
            return invalid;

        await using var transaction = await context.Database.BeginTransactionAsync();
        await LockOwnerAsync(userId);

        var quota = await GetQuotaAsync(userId);
        if (!quota.CanCreate)
            return Error("list_limit", $"You can have up to {quota.MaxLists} lists.");

        var now = DateTime.UtcNow;
        var list = new CommunityList
        {
            OwnerId = userId,
            Name = name,
            Description = description,
            Slug = MakeSlug(name),
            Status = CommunityListStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.CommunityLists.Add(list);
        await context.SaveChangesAsync();
        await transaction.CommitAsync();

        return Results.Ok(new { listId = list.ListId, slug = list.Slug });
    }

    [HttpPut("{id:int}")]
    [SwaggerOperation(Summary = "Update a community list's details, genres, tags and visibility")]
    public async Task<IResult> Update(int id, [FromBody] UpdateCommunityListRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var list = await context.CommunityLists
                                .Include(l => l.Genres)
                                .Include(l => l.Tags)
                                .FirstOrDefaultAsync(l => l.ListId == id);
        if (list == null || !CanManage(list.OwnerId))
            return Results.NotFound();

        var name = UserTextSanitizer.Clean(request.Name, multiline: false) ?? "";
        var description = UserTextSanitizer.Clean(request.Description, multiline: true);
        if (ValidateText(name, description) is { } invalid)
            return invalid;

        if (!Enum.IsDefined(request.Status))
            return Error("invalid_status", "Unknown visibility.");
        if (!IsAdmin && (request.Status == CommunityListStatus.Unlisted) != (list.Status == CommunityListStatus.Unlisted))
            return Error("unlisted_by_moderation", "This list was unlisted by moderation and can't change visibility.");

        var genres = request.Genres.Distinct().ToList();
        if (genres.Count > MaxGenres || genres.Any(g => !Enum.IsDefined(g)))
            return Error("genre_limit", $"Pick up to {MaxGenres} genres.");

        var tagIds = request.TagIds.Distinct().ToList();
        if (tagIds.Count > MaxTags)
            return Error("tag_limit", $"Pick up to {MaxTags} tags.");
        if (tagIds.Count > 0 && await context.Tags.CountAsync(t => tagIds.Contains(t.TagId)) != tagIds.Count)
            return Error("unknown_tag", "One of the tags doesn't exist.");

        list.Name = name;
        list.Slug = MakeSlug(name);
        list.Description = description;
        if (request.Status == CommunityListStatus.Public && list.PublishedAt == null)
            list.PublishedAt = DateTime.UtcNow;
        list.Status = request.Status;
        list.UpdatedAt = DateTime.UtcNow;

        list.Genres.RemoveAll(g => !genres.Contains(g.Genre));
        foreach (var genre in genres.Where(g => list.Genres.All(existing => existing.Genre != g)))
            list.Genres.Add(new CommunityListGenre { ListId = id, Genre = genre });

        list.Tags.RemoveAll(t => !tagIds.Contains(t.TagId));
        foreach (var tagId in tagIds.Where(t => list.Tags.All(existing => existing.TagId != t)))
            list.Tags.Add(new CommunityListTag { ListId = id, TagId = tagId });

        await context.SaveChangesAsync();

        return Results.Ok(new { slug = list.Slug, status = list.Status });
    }

    [HttpPut("{id:int}/items")]
    [SwaggerOperation(Summary = "Replace a community list's items, in order")]
    public async Task<IResult> ReplaceItems(int id, [FromBody] ReplaceCommunityListItemsRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var ownerId = await context.CommunityLists.Where(l => l.ListId == id).Select(l => l.OwnerId).FirstOrDefaultAsync();
        if (ownerId == null || !CanManage(ownerId))
            return Results.NotFound();

        var inputs = request.Items;
        var deckIds = inputs.Select(i => i.DeckId).ToList();
        if (deckIds.Distinct().Count() != deckIds.Count)
            return Error("duplicate_item", "A title can only appear once in a list.");

        var notes = inputs.Select(i => UserTextSanitizer.Clean(i.Note, multiline: false)).ToList();
        if (notes.Any(n => n is { Length: > CommunityListItem.MaxNoteLength }))
            return Error("note_too_long", $"Notes can be up to {CommunityListItem.MaxNoteLength} characters.");

        if (deckIds.Count > 0)
        {
            var validCount = await context.Decks.CountAsync(d => deckIds.Contains(d.DeckId) && d.ParentDeckId == null);
            if (validCount != deckIds.Count)
                return Error("invalid_item", "Only existing top-level media can be added to a list.");
        }

        await using var transaction = await context.Database.BeginTransactionAsync();
        await LockListAsync(id);

        var list = await context.CommunityLists.FirstOrDefaultAsync(l => l.ListId == id);
        if (list == null)
            return Results.NotFound();

        var maxItems = (await userLimits.GetLimitsAsync(list.OwnerId)).CommunityListItems;
        if (deckIds.Count > maxItems && deckIds.Count > await context.CommunityListItems.CountAsync(i => i.ListId == id))
            return Error("item_limit", $"Lists can hold up to {maxItems} titles on your plan.");

        await context.CommunityListItems.Where(i => i.ListId == id).ExecuteDeleteAsync();
        for (var i = 0; i < deckIds.Count; i++)
            context.CommunityListItems.Add(new CommunityListItem { ListId = id, DeckId = deckIds[i], Position = i, Note = notes[i] });

        list.ItemCount = deckIds.Count;
        list.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
        await transaction.CommitAsync();

        return Results.Ok(new { itemCount = list.ItemCount });
    }

    [HttpDelete("{id:int}")]
    [SwaggerOperation(Summary = "Delete a community list with its items, favourites and reports")]
    public async Task<IResult> Delete(int id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var ownerId = await context.CommunityLists.Where(l => l.ListId == id).Select(l => l.OwnerId).FirstOrDefaultAsync();
        if (ownerId == null || !CanManage(ownerId))
            return Results.NotFound();

        await context.CommunityLists.Where(l => l.ListId == id).ExecuteDeleteAsync();
        return Results.NoContent();
    }

    [HttpPost("{id:int}/favourite")]
    [SwaggerOperation(Summary = "Favourite a public community list")]
    public async Task<IResult> Favourite(int id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var list = await context.CommunityLists.AsNoTracking()
                                .Where(l => l.ListId == id)
                                .Select(l => new { l.OwnerId, l.Status, l.Name, l.Slug })
                                .FirstOrDefaultAsync();
        if (list == null || list.Status != CommunityListStatus.Public)
            return Results.NotFound();
        if (list.OwnerId == userId)
            return Error("own_list", "You can't favourite your own list.");

        if (await context.CommunityListFavourites.AnyAsync(f => f.ListId == id && f.UserId == userId))
            return Results.Ok(new { favourited = true, favouriteCount = await GetFavouriteCountAsync(id) });

        context.CommunityListFavourites.Add(new CommunityListFavourite { ListId = id, UserId = userId, CreatedAt = DateTime.UtcNow });
        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            return Results.Ok(new { favourited = true, favouriteCount = await GetFavouriteCountAsync(id) });
        }

        await context.CommunityLists.Where(l => l.ListId == id)
                     .ExecuteUpdateAsync(s => s.SetProperty(l => l.FavouriteCount, l => l.FavouriteCount + 1));
        var count = await GetFavouriteCountAsync(id);
        await NotifyMilestoneAsync(id, count, list.OwnerId, list.Name, list.Slug);

        return Results.Ok(new { favourited = true, favouriteCount = count });
    }

    [HttpDelete("{id:int}/favourite")]
    [SwaggerOperation(Summary = "Remove a community list from the caller's favourites")]
    public async Task<IResult> Unfavourite(int id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        if (!await context.CommunityLists.AnyAsync(l => l.ListId == id))
            return Results.NotFound();

        var removed = await context.CommunityListFavourites.Where(f => f.ListId == id && f.UserId == userId).ExecuteDeleteAsync();
        if (removed > 0)
            await context.CommunityLists.Where(l => l.ListId == id)
                         .ExecuteUpdateAsync(s => s.SetProperty(l => l.FavouriteCount, l => l.FavouriteCount > 0 ? l.FavouriteCount - 1 : 0));

        return Results.Ok(new { favourited = false, favouriteCount = await GetFavouriteCountAsync(id) });
    }

    [HttpPost("{id:int}/report")]
    [SwaggerOperation(Summary = "Report a public community list to moderation")]
    public async Task<IResult> Report(int id, [FromBody] ReportCommunityListRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var reason = UserTextSanitizer.Clean(request.Reason, multiline: true) ?? "";
        if (reason.Length == 0 || reason.Length > CommunityListReport.MaxReasonLength)
            return Error("invalid_reason", $"Describe the problem in up to {CommunityListReport.MaxReasonLength} characters.");

        var list = await context.CommunityLists.AsNoTracking()
                                .Where(l => l.ListId == id)
                                .Select(l => new { l.OwnerId, l.Status, l.Name, l.Slug })
                                .FirstOrDefaultAsync();
        if (list == null || list.Status != CommunityListStatus.Public)
            return Results.NotFound();
        if (list.OwnerId == userId)
            return Error("own_list", "You can't report your own list.");

        var alreadyReported = Results.Conflict(new { code = "already_reported", message = "You already reported this list." });
        if (await context.CommunityListReports.AnyAsync(r => r.ListId == id && r.ReporterId == userId && r.ResolvedAt == null))
            return alreadyReported;

        context.CommunityListReports.Add(new CommunityListReport { ListId = id, ReporterId = userId, Reason = reason, CreatedAt = DateTime.UtcNow });
        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return alreadyReported;
        }

        await discordReports.SendAsync(userId, "Community list report",
                                       $"[{DiscordMarkdown.Sanitize(list.Name)}](https://jiten.moe/lists/{id}/{list.Slug})\n\nReason:\n{DiscordMarkdown.Sanitize(reason)}\n\n[Review reports](https://jiten.moe/dashboard/lists)",
                                       "ListReporter");

        return Results.NoContent();
    }

    [HttpPost("{id:int}/view")]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "Record a view of a community list", Description = "Fired by the list page after a delay. Owner views don't count.")]
    public async Task<IResult> RecordView(int id)
    {
        if (id <= 0)
            return Results.NoContent();

        var userId = currentUserService.UserId;
        if (userId != null && await context.CommunityLists.AnyAsync(l => l.ListId == id && l.OwnerId == userId))
            return Results.NoContent();

        activityBuffer.RecordView(id, VisitorKeyHelper.For(HttpContext));
        return Results.NoContent();
    }

    private async Task NotifyMilestoneAsync(int listId, int favouriteCount, string ownerId, string name, string slug)
    {
        var milestone = FavouriteMilestones.LastOrDefault(m => m <= favouriteCount);
        if (milestone == 0)
            return;

        var claimed = await context.CommunityLists
                                   .Where(l => l.ListId == listId && l.FavouriteMilestoneNotified < milestone)
                                   .ExecuteUpdateAsync(s => s.SetProperty(l => l.FavouriteMilestoneNotified, milestone));
        if (claimed == 0)
            return;

        await notificationService.Notify(ownerId, NotificationType.CommunityListMilestone,
                                         $"Your list reached {milestone} favourites",
                                         $"\"{name}\" now has {milestone} favourites.",
                                         ListUrl(listId, slug));
    }

    private async Task<CommunityList?> LoadVisibleAsync(int id)
    {
        var list = await context.CommunityLists.AsNoTracking().FirstOrDefaultAsync(l => l.ListId == id);
        if (list == null)
            return null;
        return list.Status == CommunityListStatus.Public || CanManage(list.OwnerId) ? list : null;
    }

    /// <summary>Serialises one owner's creates so parallel requests can't each pass the list cap; call inside a transaction.</summary>
    private Task LockOwnerAsync(string ownerId) =>
        context.Database.IsNpgsql()
            ? context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0}, hashtext({1}))", OwnerLockClass, ownerId)
            : Task.CompletedTask;

    /// <summary>Serialises item replaces on one list so parallel saves can't merge into a list over the item cap; call inside a transaction.</summary>
    private Task LockListAsync(int listId) =>
        context.Database.IsNpgsql()
            ? context.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM jiten."CommunityLists" WHERE "ListId" = {listId} FOR UPDATE""")
            : Task.CompletedTask;

    private Task<int> GetFavouriteCountAsync(int listId) =>
        context.CommunityLists.AsNoTracking().Where(l => l.ListId == listId).Select(l => l.FavouriteCount).FirstAsync();

    private IQueryable<CommunityList> PublicMatching(string? q, GenreTagFilter filter)
    {
        var query = context.CommunityLists.AsNoTracking().Where(l => l.Status == CommunityListStatus.Public);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLower();
            query = needle.Length > CommunityList.MaxNameLength
                ? query.Where(_ => false)
                : query.Where(l => l.Name.ToLower().Contains(needle));
        }

        return ApplyGenreTagFilters(query, filter);
    }

    /// <summary>Each id adds a subquery to the browse SQL, so the total is capped well above what the filter UI produces.</summary>
    private static bool TooManyFilterIds(GenreTagFilter filter) =>
        filter.IncludedGenres.Count + filter.ExcludedGenres.Count + filter.IncludedTags.Count + filter.ExcludedTags.Count > MaxFilterIds;

    private static IResult FilterLimitError() => Error("filter_limit", $"Filter by up to {MaxFilterIds} genres and tags at a time.");

    private async Task<UserCoverageChunkHelper.CoverageDictionaries> GetViewerCoverageAsync(string viewerId, IReadOnlyCollection<int> deckIds)
    {
        var sentenceMetricsGranted = await jitenPlus.GetTierAsync(viewerId) >= JitenPlusTier.Trial;
        return await UserCoverageChunkHelper.GetCoverage(userContext, viewerId, deckIds, includeSentences: sentenceMetricsGranted);
    }

    private async Task<CommunityListQuotaDto> GetQuotaAsync(string ownerId)
    {
        var limits = await userLimits.GetLimitsAsync(ownerId);
        var listCount = await context.CommunityLists.CountAsync(l => l.OwnerId == ownerId);
        var maxLists = limits.CommunityLists;

        return new CommunityListQuotaDto
        {
            MaxLists = maxLists,
            MaxItems = limits.CommunityListItems,
            FullMaxLists = limits.Allowances.CommunityLists.Full,
            FullMaxItems = limits.Allowances.CommunityListItems.Full,
            MaxGenres = MaxGenres,
            MaxTags = MaxTags,
            ListCount = listCount,
            CanCreate = listCount < maxLists
        };
    }

    private async Task<List<T>> BuildSummariesAsync<T>(IReadOnlyList<CommunityList> lists, string? viewerId)
        where T : CommunityListSummaryDto, new()
    {
        if (lists.Count == 0)
            return [];

        var ids = lists.Select(l => l.ListId).ToList();

        var genres = await context.CommunityListGenres.AsNoTracking()
                                  .Where(g => ids.Contains(g.ListId))
                                  .Select(g => new { g.ListId, g.Genre })
                                  .ToListAsync();
        var tags = await context.CommunityListTags.AsNoTracking()
                                .Where(t => ids.Contains(t.ListId))
                                .Select(t => new { t.ListId, t.TagId, t.Tag.Name })
                                .ToListAsync();
        var covers = await context.CommunityListItems.AsNoTracking()
                                  .Where(i => ids.Contains(i.ListId) && i.Position < PreviewCoverCount)
                                  .OrderBy(i => i.Position)
                                  .Select(i => new { i.ListId, i.Deck.CoverName })
                                  .ToListAsync();

        var ownerIds = lists.Select(l => l.OwnerId).Distinct().ToList();
        var ownerNames = await userContext.Users.AsNoTracking()
                                          .Where(u => ownerIds.Contains(u.Id))
                                          .Select(u => new { u.Id, u.DisplayName })
                                          .ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        var favourited = viewerId == null
            ? new HashSet<int>()
            : (await context.CommunityListFavourites.AsNoTracking()
                            .Where(f => f.UserId == viewerId && ids.Contains(f.ListId))
                            .Select(f => f.ListId)
                            .ToListAsync()).ToHashSet();

        var genresByList = genres.ToLookup(g => g.ListId, g => g.Genre);
        var tagsByList = tags.ToLookup(t => t.ListId);
        var coversByList = covers.ToLookup(c => c.ListId, c => c.CoverName);
        var isAdmin = viewerId != null && IsAdmin;

        return lists.Select(l =>
        {
            var dto = new T();
            var isOwner = viewerId != null && l.OwnerId == viewerId;
            dto.ListId = l.ListId;
            dto.Slug = l.Slug;
            dto.Name = l.Name;
            dto.Description = l.Description;
            dto.OwnerDisplayName = ownerNames.GetValueOrDefault(l.OwnerId);
            dto.Genres = genresByList[l.ListId].OrderBy(g => g).ToList();
            dto.Tags = tagsByList[l.ListId].OrderBy(t => t.Name).Select(t => new TagDto { TagId = t.TagId, Name = t.Name }).ToList();
            dto.FavouriteCount = l.FavouriteCount;
            dto.ItemCount = l.ItemCount;
            dto.UpdatedAt = l.UpdatedAt;
            dto.PublishedAt = l.PublishedAt;
            dto.PreviewCoverNames = coversByList[l.ListId].ToList();
            dto.IsFavourited = favourited.Contains(l.ListId);
            dto.IsOwner = isOwner;
            dto.Status = isOwner || isAdmin ? l.Status : null;
            return dto;
        }).ToList();
    }

    private sealed record ListItemRow(int DeckId, string? Note);

    private sealed record RankedItem(int DeckId, int Rank, string? Note);

    private Task<List<ListItemRow>> LoadCuratorOrderAsync(int listId) =>
        context.CommunityListItems.AsNoTracking()
               .Where(i => i.ListId == listId)
               .OrderBy(i => i.Position)
               .ThenBy(i => i.DeckId)
               .Select(i => new ListItemRow(i.DeckId, i.Note))
               .ToListAsync();

    /// <summary>Ties in coverage keep curator order.</summary>
    private static List<RankedItem> Arrange(List<ListItemRow> curatorOrder, string? sort, Dictionary<int, float>? coverage)
    {
        var ranked = curatorOrder.Select((item, i) => new RankedItem(item.DeckId, i + 1, item.Note));
        if (sort == CoverageSort && coverage != null)
            ranked = ranked.OrderByDescending(r => coverage.GetValueOrDefault(r.DeckId)).ThenBy(r => r.Rank);
        return ranked.ToList();
    }

    private async Task<CommunityListCoverageSummaryDto> BuildCoverageSummaryAsync(List<int> deckIds, Dictionary<int, float> coverage)
    {
        var summary = new CommunityListCoverageSummaryDto
        {
            Values = deckIds.Select(d => coverage.GetValueOrDefault(d)).OrderByDescending(v => v).ToList()
        };
        if (summary.Values.Count == 0 || summary.Values[0] <= 0)
            return summary;

        var easiestId = deckIds.MaxBy(d => coverage.GetValueOrDefault(d));
        summary.Easiest = await context.Decks.AsNoTracking()
                                       .Where(d => d.DeckId == easiestId)
                                       .Select(d => new CommunityListEasiestDto
                                       {
                                           DeckId = d.DeckId,
                                           OriginalTitle = d.OriginalTitle,
                                           RomajiTitle = d.RomajiTitle,
                                           EnglishTitle = d.EnglishTitle
                                       })
                                       .FirstOrDefaultAsync();
        if (summary.Easiest != null)
            summary.Easiest.Coverage = summary.Values[0];

        return summary;
    }

    /// <param name="coverage">The viewer's coverage when already loaded for the whole list; fetched for the page otherwise.</param>
    private async Task<PaginatedResponse<List<CommunityListItemDto>>> BuildItemPageAsync(
        List<RankedItem> arranged, int offset, int limit, string? viewerId,
        Dictionary<int, (DeckStatus Status, bool IsFavourite)>? statuses, UserCoverageChunkHelper.CoverageDictionaries? coverage)
    {
        var page = arranged.Skip(offset).Take(limit).ToList();
        var deckIds = page.Select(r => r.DeckId).ToList();

        var decks = await context.Decks.AsNoTracking()
                                 .Include(d => d.DeckDifficulty)
                                 .Where(d => deckIds.Contains(d.DeckId))
                                 .ToDictionaryAsync(d => d.DeckId);

        var items = new List<CommunityListItemDto>(page.Count);
        foreach (var item in page)
        {
            if (!decks.TryGetValue(item.DeckId, out var deck)) continue;
            items.Add(new CommunityListItemDto { Position = item.Rank, Note = item.Note, Deck = new DeckDto(deck) });
        }

        var deckDtos = items.Select(i => i.Deck).ToList();

        if (viewerId != null && deckDtos.Count > 0)
        {
            coverage ??= await GetViewerCoverageAsync(viewerId, deckIds);
            coverage.ApplyTo(deckDtos);

            if (statuses != null)
            {
                foreach (var dto in deckDtos)
                {
                    if (!statuses.TryGetValue(dto.DeckId, out var tracked)) continue;
                    dto.Status = tracked.Status;
                    dto.IsFavourite = tracked.IsFavourite;
                }
            }
        }

        return new PaginatedResponse<List<CommunityListItemDto>>(items, arranged.Count, limit, offset);
    }

    /// <summary>Same rules as the media browser.</summary>
    private static IQueryable<CommunityList> ApplyGenreTagFilters(IQueryable<CommunityList> query, GenreTagFilter filter)
    {
        foreach (var genre in filter.IncludedGenres)
            query = query.Where(l => l.Genres.Any(g => g.Genre == genre));

        var excludedGenres = filter.ExcludedGenres;
        if (excludedGenres.Count > 0)
            query = query.Where(l => !l.Genres.Any(g => excludedGenres.Contains(g.Genre)));

        foreach (var tagId in filter.IncludedTags)
            query = query.Where(l => l.Tags.Any(t => t.TagId == tagId));

        var excludedTags = filter.ExcludedTags;
        if (excludedTags.Count > 0)
            query = query.Where(l => !l.Tags.Any(t => excludedTags.Contains(t.TagId)));

        return query;
    }

    /// <summary>Seeded shuffle that stays stable across pages; seeds pick the hash's multipliers, since shifting a fixed hash only rotates one order.</summary>
    private static IQueryable<CommunityList> OrderRandomly(IQueryable<CommunityList> query, int seed)
    {
        const long prime = 2147483647L;
        var rng = new Random(seed);
        long linear = rng.Next(1, int.MaxValue);
        long quadratic = rng.Next(1, int.MaxValue);
        return query.OrderBy(l => ((long)l.ListId * linear % prime + (long)l.ListId * l.ListId % prime * quadratic % prime) % prime)
                    .ThenBy(l => l.ListId);
    }

    private static IResult? ValidateText(string name, string? description)
    {
        if (name.Length == 0 || name.Length > CommunityList.MaxNameLength)
            return Error("invalid_name", $"Names need 1 to {CommunityList.MaxNameLength} characters.");
        if (description is { Length: > CommunityList.MaxDescriptionLength })
            return Error("description_too_long", $"Descriptions can be up to {CommunityList.MaxDescriptionLength} characters.");
        return null;
    }

    private static IResult Error(string code, string message) => Results.BadRequest(new { code, message });

    public static string ListUrl(int listId, string slug) => string.IsNullOrEmpty(slug) ? $"/lists/{listId}" : $"/lists/{listId}/{slug}";

    /// <summary>Lowercase ASCII words joined by hyphens; accents fold to their base letter and other scripts drop out, so a Japanese-only name gives an empty slug.</summary>
    public static string MakeSlug(string name)
    {
        var builder = new StringBuilder();
        var lastWasDash = true;
        foreach (var ch in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            var c = char.ToLowerInvariant(ch);
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length > CommunityList.MaxSlugLength ? slug[..CommunityList.MaxSlugLength].TrimEnd('-') : slug;
    }
}
