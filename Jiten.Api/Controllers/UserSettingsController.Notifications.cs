using System.Text.Json;
using Jiten.Core.Data;
using Jiten.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

public record NotificationGroupPreferenceDto(NotificationGroup Group, bool Enabled);

public record NotificationGroupPreferenceRequest(bool Enabled);

public partial class UserSettingsController
{
    [HttpGet("notifications")]
    [SwaggerOperation(Summary = "Get which notification groups the user receives")]
    public async Task<ActionResult<List<NotificationGroupPreferenceDto>>> GetNotificationPreferences()
    {
        var userId = currentUserService.UserId!;

        var stored = await context.UserSettings
                                  .AsNoTracking()
                                  .Where(us => us.UserId == userId)
                                  .Select(us => us.MutedNotificationGroupsJson)
                                  .FirstOrDefaultAsync();

        return Ok(ToPreferences(NotificationService.ParseMutedGroups(stored)));
    }

    [HttpPut("notifications/{group}")]
    [SwaggerOperation(Summary = "Mute or unmute one notification group")]
    public async Task<ActionResult<List<NotificationGroupPreferenceDto>>> UpdateNotificationPreference(NotificationGroup group,
        [FromBody] NotificationGroupPreferenceRequest request)
    {
        if (!NotificationGroups.Mutable.Contains(group))
            return BadRequest(new { error = "This kind of notification can't be turned off" });

        var userId = currentUserService.UserId!;

        try
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            var settings = await LoadSettingsForUpdate(userId);
            var muted = NotificationService.ParseMutedGroups(settings.MutedNotificationGroupsJson);

            if (request.Enabled) muted.Remove(group);
            else if (!muted.Contains(group)) muted.Add(group);

            settings.MutedNotificationGroupsJson = JsonSerializer.Serialize(muted.Order().Select(g => (int)g));
            await context.SaveChangesAsync();
            await transaction.CommitAsync();

            return Ok(ToPreferences(muted));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error saving notification preference {Group} for user {UserId}", group, userId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    private static List<NotificationGroupPreferenceDto> ToPreferences(List<NotificationGroup> muted) =>
        NotificationGroups.Mutable.Select(g => new NotificationGroupPreferenceDto(g, !muted.Contains(g))).ToList();
}
