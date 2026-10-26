using Jiten.Api.Dtos;
using Jiten.Api.Services;
using Jiten.Core.Data.CommunityLists;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

public partial class AdminController
{
    [HttpGet("list-reports")]
    public async Task<IResult> GetCommunityListReports([FromQuery] bool resolved = false, [FromQuery] int offset = 0, [FromQuery] int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);

        var query = dbContext.CommunityListReports.AsNoTracking().Where(r => resolved ? r.ResolvedAt != null : r.ResolvedAt == null);
        var total = await query.CountAsync();

        var rows = await query.OrderByDescending(r => r.CreatedAt)
                              .Skip(offset)
                              .Take(limit)
                              .Join(dbContext.CommunityLists, r => r.ListId, l => l.ListId,
                                    (r, l) => new { Report = r, l.Name, l.Slug, l.Status })
                              .ToListAsync();

        var reporterIds = rows.Select(r => r.Report.ReporterId).Distinct().ToList();
        var reporterNames = await userContext.Users.AsNoTracking()
                                             .Where(u => reporterIds.Contains(u.Id))
                                             .Select(u => new { u.Id, u.UserName })
                                             .ToDictionaryAsync(u => u.Id, u => u.UserName);

        var dtos = rows.Select(r => new AdminCommunityListReportDto
        {
            ReportId = r.Report.ReportId,
            ListId = r.Report.ListId,
            ListName = r.Name,
            ListSlug = r.Slug,
            ListStatus = r.Status,
            ReporterUserName = reporterNames.GetValueOrDefault(r.Report.ReporterId),
            Reason = r.Report.Reason,
            CreatedAt = r.Report.CreatedAt,
            ResolvedAt = r.Report.ResolvedAt
        }).ToList();

        return Results.Ok(new PaginatedResponse<List<AdminCommunityListReportDto>>(dtos, total, limit, offset));
    }

    [HttpPost("list-reports/{reportId:int}/dismiss")]
    public async Task<IResult> DismissCommunityListReport(int reportId, [FromServices] ICurrentUserService currentUser)
    {
        var updated = await ResolveOpenReportsAsync(dbContext.CommunityListReports.Where(r => r.ReportId == reportId), currentUser.UserId);
        return updated == 0 ? Results.NotFound() : Results.NoContent();
    }

    [HttpGet("community-lists")]
    public async Task<IResult> GetCommunityListsForAdmin([FromQuery] string? q = null, [FromQuery] CommunityListStatus? status = null,
                                                      [FromQuery] int offset = 0, [FromQuery] int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);

        var query = dbContext.CommunityLists.AsNoTracking();
        if (status.HasValue)
            query = query.Where(l => l.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLower();
            query = query.Where(l => l.Name.ToLower().Contains(needle));
        }

        var total = await query.CountAsync();
        var lists = await query.OrderByDescending(l => l.UpdatedAt).Skip(offset).Take(limit).ToListAsync();
        var ids = lists.Select(l => l.ListId).ToList();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var weekStart = today.AddDays(-6);
        var monthStart = today.AddDays(-29);
        var views = await dbContext.CommunityListActivityDailies.AsNoTracking()
                                   .Where(a => ids.Contains(a.ListId))
                                   .GroupBy(a => a.ListId)
                                   .Select(g => new
                                   {
                                       ListId = g.Key,
                                       Week = g.Where(a => a.Date >= weekStart).Sum(a => a.Views),
                                       Month = g.Where(a => a.Date >= monthStart).Sum(a => a.Views),
                                       Total = g.Sum(a => a.Views)
                                   })
                                   .ToDictionaryAsync(v => v.ListId);

        var openReports = await dbContext.CommunityListReports.AsNoTracking()
                                         .Where(r => ids.Contains(r.ListId) && r.ResolvedAt == null)
                                         .GroupBy(r => r.ListId)
                                         .Select(g => new { ListId = g.Key, Count = g.Count() })
                                         .ToDictionaryAsync(r => r.ListId, r => r.Count);

        var ownerIds = lists.Select(l => l.OwnerId).Distinct().ToList();
        var owners = await userContext.Users.AsNoTracking()
                                      .Where(u => ownerIds.Contains(u.Id))
                                      .Select(u => new { u.Id, u.UserName, u.DisplayName })
                                      .ToDictionaryAsync(u => u.Id);

        var dtos = lists.Select(l => new AdminCommunityListDto
        {
            ListId = l.ListId,
            Name = l.Name,
            Slug = l.Slug,
            OwnerId = l.OwnerId,
            OwnerUserName = owners.GetValueOrDefault(l.OwnerId)?.UserName,
            OwnerDisplayName = owners.GetValueOrDefault(l.OwnerId)?.DisplayName,
            Status = l.Status,
            ItemCount = l.ItemCount,
            FavouriteCount = l.FavouriteCount,
            Views7Days = views.GetValueOrDefault(l.ListId)?.Week ?? 0,
            Views30Days = views.GetValueOrDefault(l.ListId)?.Month ?? 0,
            ViewsTotal = views.GetValueOrDefault(l.ListId)?.Total ?? 0,
            OpenReports = openReports.GetValueOrDefault(l.ListId),
            CreatedAt = l.CreatedAt,
            UpdatedAt = l.UpdatedAt
        }).ToList();

        return Results.Ok(new PaginatedResponse<List<AdminCommunityListDto>>(dtos, total, limit, offset));
    }

    /// <summary>Hides the list from everyone but its owner and admins, and resolves its open reports.</summary>
    [HttpPost("community-lists/{id:int}/unlist")]
    public async Task<IResult> UnlistCommunityList(int id, [FromServices] ICurrentUserService currentUser)
    {
        var updated = await dbContext.CommunityLists.Where(l => l.ListId == id)
                                     .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, CommunityListStatus.Unlisted));
        if (updated == 0)
            return Results.NotFound();

        await ResolveOpenReportsAsync(dbContext.CommunityListReports.Where(r => r.ListId == id), currentUser.UserId);
        logger.LogInformation("Admin unlisted community list {ListId}", id);
        return Results.NoContent();
    }

    [HttpPost("community-lists/{id:int}/restore")]
    public async Task<IResult> RestoreCommunityList(int id)
    {
        var list = await dbContext.CommunityLists.FirstOrDefaultAsync(l => l.ListId == id);
        if (list == null)
            return Results.NotFound();
        if (list.Status != CommunityListStatus.Unlisted)
            return Results.BadRequest(new { code = "not_unlisted", message = "Only unlisted lists can be restored." });

        list.Status = CommunityListStatus.Public;
        list.PublishedAt ??= DateTime.UtcNow;
        await dbContext.SaveChangesAsync();
        return Results.NoContent();
    }

    /// <summary>Rebuilds the denormalised favourite and item counts from their rows; safe to rerun.</summary>
    [HttpPost("community-lists/recount")]
    public async Task<IResult> RecountCommunityLists()
    {
        var updated = await dbContext.CommunityLists.ExecuteUpdateAsync(s => s
            .SetProperty(l => l.FavouriteCount, l => dbContext.CommunityListFavourites.Count(f => f.ListId == l.ListId))
            .SetProperty(l => l.ItemCount, l => dbContext.CommunityListItems.Count(i => i.ListId == l.ListId)));
        return Results.Ok(new { updated });
    }

    private static Task<int> ResolveOpenReportsAsync(IQueryable<CommunityListReport> reports, string? resolvedBy) =>
        reports.Where(r => r.ResolvedAt == null)
               .ExecuteUpdateAsync(s => s.SetProperty(r => r.ResolvedAt, DateTime.UtcNow)
                                         .SetProperty(r => r.ResolvedBy, resolvedBy));
}
