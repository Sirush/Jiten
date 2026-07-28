using Jiten.Api.Dtos;
using Jiten.Api.Dtos.Requests;
using Jiten.Core.Data;
using Jiten.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

public partial class AdminController
{
    private const string SITE_UPDATE_DEFAULT_TEASER = "A new site update has been published.";

    [HttpGet("updates")]
    public async Task<IActionResult> GetSiteUpdates()
    {
        var updates = await dbContext.SiteUpdates.AsNoTracking()
                                     .OrderByDescending(u => u.CreatedAt)
                                     .Select(u => new AdminSiteUpdateDto
                                     {
                                         Id = u.Id,
                                         Title = u.Title,
                                         BodyMarkdown = u.BodyMarkdown,
                                         NotificationTeaser = u.NotificationTeaser,
                                         CreatedAt = u.CreatedAt,
                                         UpdatedAt = u.UpdatedAt,
                                         PublishedAt = u.PublishedAt,
                                         NotifiedAt = u.NotifiedAt
                                     })
                                     .ToListAsync();

        return Ok(updates);
    }

    [HttpGet("updates/{id:int}")]
    public async Task<IActionResult> GetSiteUpdate(int id)
    {
        var update = await dbContext.SiteUpdates.AsNoTracking()
                                    .Where(u => u.Id == id)
                                    .Select(u => new AdminSiteUpdateDto
                                    {
                                        Id = u.Id,
                                        Title = u.Title,
                                        BodyMarkdown = u.BodyMarkdown,
                                        NotificationTeaser = u.NotificationTeaser,
                                        CreatedAt = u.CreatedAt,
                                        UpdatedAt = u.UpdatedAt,
                                        PublishedAt = u.PublishedAt,
                                        NotifiedAt = u.NotifiedAt
                                    })
                                    .FirstOrDefaultAsync();

        if (update == null)
            return NotFound(new { Message = "Update not found" });

        return Ok(update);
    }

    [HttpPost("updates")]
    public async Task<IActionResult> CreateSiteUpdate([FromBody] SaveSiteUpdateRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var update = new SiteUpdate
        {
            Title = request.Title.Trim(),
            BodyMarkdown = request.BodyMarkdown,
            NotificationTeaser = string.IsNullOrWhiteSpace(request.NotificationTeaser) ? null : request.NotificationTeaser.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        dbContext.SiteUpdates.Add(update);
        await dbContext.SaveChangesAsync();

        logger.LogInformation("Admin created site update: Id={Id}, Title={Title}", update.Id, update.Title);
        return Ok(new { update.Id });
    }

    [HttpPut("updates/{id:int}")]
    public async Task<IActionResult> UpdateSiteUpdate(int id, [FromBody] SaveSiteUpdateRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var update = await dbContext.SiteUpdates.FirstOrDefaultAsync(u => u.Id == id);
        if (update == null)
            return NotFound(new { Message = "Update not found" });

        update.Title = request.Title.Trim();
        update.BodyMarkdown = request.BodyMarkdown;
        update.NotificationTeaser = string.IsNullOrWhiteSpace(request.NotificationTeaser) ? null : request.NotificationTeaser.Trim();
        update.UpdatedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync();

        logger.LogInformation("Admin edited site update: Id={Id}", id);
        return Ok(new { Message = "Update saved" });
    }

    [HttpPost("updates/{id:int}/publish")]
    public async Task<IActionResult> PublishSiteUpdate(int id, [FromServices] NotificationService notificationService)
    {
        var update = await dbContext.SiteUpdates.FirstOrDefaultAsync(u => u.Id == id);
        if (update == null)
            return NotFound(new { Message = "Update not found" });

        update.PublishedAt ??= DateTime.UtcNow;

        if (update.NotifiedAt != null)
        {
            await dbContext.SaveChangesAsync();
            return Ok(new { Message = "Update was already published, no notifications sent", Count = 0 });
        }

        var userIds = await userContext.Users.AsNoTracking()
                                       .Select(u => u.Id)
                                       .ToListAsync();

        // Stamped before the fan-out so NotificationService's save commits the guard and the rows together;
        // a partial commit would let a retry notify everyone twice.
        update.NotifiedAt = DateTime.UtcNow;

        await notificationService.NotifyMany(
            userIds, NotificationType.SiteUpdate,
            update.Title,
            update.NotificationTeaser ?? SITE_UPDATE_DEFAULT_TEASER,
            $"/updates#update-{update.Id}");

        await dbContext.SaveChangesAsync();

        logger.LogInformation("Admin published site update: Id={Id}, Notified={Count}", id, userIds.Count);
        return Ok(new { Message = $"Published and notified {userIds.Count} users", Count = userIds.Count });
    }

    [HttpDelete("updates/{id:int}")]
    public async Task<IActionResult> DeleteSiteUpdate(int id)
    {
        var update = await dbContext.SiteUpdates.FirstOrDefaultAsync(u => u.Id == id);
        if (update == null)
            return NotFound(new { Message = "Update not found" });

        dbContext.SiteUpdates.Remove(update);
        await dbContext.SaveChangesAsync();

        logger.LogInformation("Admin deleted site update: Id={Id}", id);
        return Ok(new { Message = "Update deleted" });
    }
}
