using Jiten.Api.Dtos;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/notifications")]
[Produces("application/json")]
[Authorize]
[EnableRateLimiting("fixed")]
public class NotificationController(
    JitenDbContext context,
    ICurrentUserService currentUserService) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> GetNotifications(
        [FromQuery] bool unreadOnly = false,
        [FromQuery] bool fromJitenOnly = false,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 20)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        limit = Math.Clamp(limit, 1, 50);

        var query = context.Notifications.AsNoTracking()
            .Where(n => n.UserId == userId);

        if (unreadOnly)
            query = query.Where(n => !n.IsRead);

        if (fromJitenOnly)
            query = query.Where(n => NotificationGroups.FromJitenTypes.Contains(n.Type));

        var totalCount = await query.CountAsync();

        var notifications = await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .Select(n => new NotificationDto
            {
                Id = n.Id,
                Type = n.Type,
                Title = n.Title,
                Message = n.Message,
                LinkUrl = n.LinkUrl,
                IsRead = n.IsRead,
                ReadAt = n.ReadAt,
                CreatedAt = n.CreatedAt
            })
            .ToListAsync();

        return Results.Ok(new PaginatedResponse<List<NotificationDto>>(notifications, totalCount, limit, offset));
    }

    [HttpGet("unread-count")]
    public async Task<IResult> GetUnreadCount()
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var count = await context.Notifications.AsNoTracking()
            .CountAsync(n => n.UserId == userId && !n.IsRead);

        return Results.Ok(new { count });
    }

    [HttpPost("{id:int}/read")]
    public async Task<IResult> MarkAsRead(int id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var notification = await context.Notifications
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);

        if (notification == null)
            return Results.Ok(new { success = true });

        if (!notification.IsRead)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        return Results.Ok(new { success = true });
    }

    [HttpPost("read-all")]
    public async Task<IResult> MarkAllAsRead()
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        await context.Notifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, DateTime.UtcNow));

        return Results.Ok(new { success = true });
    }

    [HttpDelete("{id:int}")]
    public async Task<IResult> DeleteNotification(int id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var notification = await context.Notifications
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);

        if (notification == null)
            return Results.Ok(new { success = true });

        context.Notifications.Remove(notification);
        await context.SaveChangesAsync();

        return Results.Ok(new { success = true });
    }
}
