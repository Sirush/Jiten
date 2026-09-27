using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

public partial class UserSettingsController
{
    [HttpGet("display-profiles")]
    [SwaggerOperation(Summary = "Get the saved display profiles")]
    public async Task<ActionResult<DisplayProfilesDto>> GetDisplayProfiles()
    {
        var userId = currentUserService.UserId!;

        var stored = await context.UserSettings
                                  .AsNoTracking()
                                  .Where(us => us.UserId == userId)
                                  .Select(us => us.DisplayProfilesJson)
                                  .FirstOrDefaultAsync();

        return Ok(new DisplayProfilesDto { Profiles = DisplayProfileSanitizer.Deserialize(stored), MaxProfiles = DisplayProfileSanitizer.MaxProfiles });
    }

    [HttpPut("display-profiles/{id}")]
    [SwaggerOperation(Summary = "Create or replace one display profile")]
    public async Task<ActionResult<DisplayProfileDto>> UpsertDisplayProfile(string id, [FromBody] DisplayProfileUpsertRequest request)
    {
        if (!DisplayProfileSanitizer.IsValidId(id))
            return BadRequest(new { error = "Invalid profile id" });

        var name = DisplayProfileSanitizer.SanitizeName(request.Name);
        if (name == null)
            return BadRequest(new { error = "A profile needs a name" });

        var userId = currentUserService.UserId!;

        try
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            var settings = await LoadSettingsForUpdate(userId);
            var profiles = DisplayProfileSanitizer.Deserialize(settings.DisplayProfilesJson);

            var existing = profiles.FindIndex(p => p.Id == id);
            if (existing < 0 && profiles.Count >= DisplayProfileSanitizer.MaxProfiles)
                return Conflict(new { error = $"You can keep up to {DisplayProfileSanitizer.MaxProfiles} display profiles" });

            var profile = new DisplayProfileDto
            {
                Id = id,
                Name = name,
                Values = DisplayProfileSanitizer.SanitizeValues(request.Values),
                StatColumns = DisplayProfileSanitizer.SanitizeStatColumns(request.StatColumns),
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            if (existing < 0) profiles.Add(profile);
            else profiles[existing] = profile;

            settings.DisplayProfilesJson = DisplayProfileSanitizer.Serialize(profiles);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();

            return Ok(profile);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error saving display profile {ProfileId} for user {UserId}", id, userId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    [HttpDelete("display-profiles/{id}")]
    [SwaggerOperation(Summary = "Delete one display profile")]
    public async Task<IActionResult> DeleteDisplayProfile(string id)
    {
        if (!DisplayProfileSanitizer.IsValidId(id))
            return BadRequest(new { error = "Invalid profile id" });

        var userId = currentUserService.UserId!;

        try
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            var settings = await LoadSettingsForUpdate(userId);
            var profiles = DisplayProfileSanitizer.Deserialize(settings.DisplayProfilesJson);

            var index = profiles.FindIndex(p => p.Id == id);
            if (index < 0) return NotFound();
            // Every signed-in device resolves its settings from some profile, so the list never empties.
            if (profiles.Count == 1) return Conflict(new { error = "You need at least one display profile" });

            profiles.RemoveAt(index);
            settings.DisplayProfilesJson = DisplayProfileSanitizer.Serialize(profiles);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();

            return NoContent();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting display profile {ProfileId} for user {UserId}", id, userId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>Row-locks the settings row on Postgres so two devices writing different profiles cannot drop each other's change.</summary>
    private async Task<UserSettings> LoadSettingsForUpdate(string userId)
    {
        if (context.Database.ProviderName?.Contains("Npgsql") == true)
        {
            var uuid = Guid.Parse(userId);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""INSERT INTO "user"."UserSettings" ("UserId") VALUES ({uuid}) ON CONFLICT DO NOTHING""");
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "user"."UserSettings" WHERE "UserId" = {uuid} FOR UPDATE""");
        }

        var settings = await context.UserSettings.FirstOrDefaultAsync(us => us.UserId == userId);
        if (settings != null) return settings;

        settings = new UserSettings { UserId = userId };
        context.UserSettings.Add(settings);
        return settings;
    }
}
