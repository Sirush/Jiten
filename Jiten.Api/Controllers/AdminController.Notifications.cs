using Jiten.Api.Dtos.Requests;
using Jiten.Core.Data;
using Jiten.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

public partial class AdminController
{
    [HttpGet("search-users")]
    public async Task<IActionResult> SearchUsers([FromQuery] string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
            return BadRequest(new { Message = "Query must be at least 2 characters" });

        var normalised = query.Trim().ToLower();

        var users = await userContext.Users.AsNoTracking()
            .Where(u => u.UserName!.ToLower().Contains(normalised) ||
                        u.Email!.ToLower().Contains(normalised) ||
                        u.Id.ToLower() == normalised)
            .OrderBy(u => u.UserName)
            .Take(10)
            .Select(u => new { userId = u.Id, userName = u.UserName, email = u.Email })
            .ToListAsync();

        return Ok(users);
    }

    [HttpPost("send-notification")]
    public async Task<IActionResult> SendNotification(
        [FromBody] SendNotificationRequest request,
        [FromServices] NotificationService notificationService)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (!request.SendToEveryone && string.IsNullOrWhiteSpace(request.UserId))
            return BadRequest(new { Message = "Either select a user or choose to send to everyone" });

        if (!string.IsNullOrEmpty(request.LinkUrl) && !request.LinkUrl.StartsWith('/'))
            return BadRequest(new { Message = "LinkUrl must be a relative path starting with /" });

        if (request.SendToEveryone)
        {
            var userIds = await userContext.Users.AsNoTracking()
                .Select(u => u.Id)
                .ToListAsync();

            await notificationService.NotifyMany(
                userIds, NotificationType.General,
                request.Title, request.Message, request.LinkUrl);

            logger.LogInformation("Admin sent notification to all users: Count={Count}, Title={Title}", userIds.Count, request.Title);
            return Ok(new { Message = $"Notification sent to {userIds.Count} users", Count = userIds.Count });
        }
        else
        {
            var userExists = await userContext.Users.AnyAsync(u => u.Id == request.UserId);
            if (!userExists)
                return NotFound(new { Message = "User not found" });

            await notificationService.Notify(
                request.UserId!, NotificationType.General,
                request.Title, request.Message, request.LinkUrl);

            logger.LogInformation("Admin sent notification to user: UserId={UserId}, Title={Title}", request.UserId, request.Title);
            return Ok(new { Message = "Notification sent successfully", Count = 1 });
        }
    }
}
