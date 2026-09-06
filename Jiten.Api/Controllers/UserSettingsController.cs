using System.Text.Json;
using Jiten.Api.Dtos;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/user/settings")]
[Authorize]
public class UserSettingsController(
    UserDbContext context,
    ILogger<UserSettingsController> logger,
    ICurrentUserService currentUserService) : ControllerBase
{
    private const int MaxPresets = 50;
    private const int MaxPresetNameLength = 40;
    private const int MaxQueryValueLength = 500;

    /// <summary>Mirrors PRESET_QUERY_KEYS in Jiten.Web/app/utils/mediaFilterPresets.ts; both sides must gain a key together.</summary>
    private static readonly HashSet<string> AllowedQueryKeys = new(StringComparer.Ordinal)
    {
        "mediaType", "title", "sortBy", "sortOrder", "status",
        "charCountMin", "charCountMax",
        "difficultyMin", "difficultyMax",
        "releaseYearMin", "releaseYearMax",
        "uniqueKanjiMin", "uniqueKanjiMax",
        "subdeckCountMin", "subdeckCountMax",
        "extRatingMin", "extRatingMax",
        "speechSpeedMin", "speechSpeedMax",
        "speechDurationMin", "speechDurationMax",
        "coverageMin", "coverageMax",
        "uniqueCoverageMin", "uniqueCoverageMax",
        "totalCoverageMin", "totalCoverageMax",
        "uTotalCoverageMin", "uTotalCoverageMax",
        "genres", "excludeGenres", "tags", "excludeTags",
        "excludeSequels", "favourite",
        "runtimeMin", "runtimeMax", "excludeMediaTypes",
    };

    [HttpGet("media-filter-presets")]
    [SwaggerOperation(Summary = "Get the saved media browser filter presets")]
    public async Task<ActionResult<MediaFilterPresetsDto>> GetMediaFilterPresets()
    {
        var userId = currentUserService.UserId!;

        var stored = await context.UserSettings
                                  .AsNoTracking()
                                  .Where(us => us.UserId == userId)
                                  .Select(us => us.MediaFilterPresetsJson)
                                  .FirstOrDefaultAsync();

        return Ok(Sanitize(Deserialize(stored)));
    }

    [HttpPut("media-filter-presets")]
    [SwaggerOperation(Summary = "Replace the saved media browser filter presets")]
    public async Task<ActionResult<MediaFilterPresetsDto>> UpdateMediaFilterPresets([FromBody] MediaFilterPresetsDto request)
    {
        var userId = currentUserService.UserId!;
        var sanitized = Sanitize(request);

        try
        {
            var settings = await context.UserSettings.FirstOrDefaultAsync(us => us.UserId == userId);
            if (settings == null)
            {
                settings = new UserSettings { UserId = userId };
                context.UserSettings.Add(settings);
            }

            settings.MediaFilterPresetsJson = JsonSerializer.Serialize(sanitized);
            await context.SaveChangesAsync();

            return Ok(sanitized);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error saving media filter presets for user {UserId}", userId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>A document written by an older or broken client degrades to "no presets" rather than failing the read.</summary>
    private static MediaFilterPresetsDto? Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "{}") return null;
        try { return JsonSerializer.Deserialize<MediaFilterPresetsDto>(json); }
        catch (JsonException) { return null; }
    }

    private static MediaFilterPresetsDto Sanitize(MediaFilterPresetsDto? dto)
    {
        var result = new MediaFilterPresetsDto();
        if (dto == null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in dto.Presets ?? new List<MediaFilterPresetDto>())
        {
            if (result.Presets.Count >= MaxPresets) break;

            var name = Truncate(preset?.Name?.Trim(), MaxPresetNameLength);
            if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;

            result.Presets.Add(new MediaFilterPresetDto
                               {
                                   Name = name,
                                   Query = SanitizeQuery(preset!.Query),
                                   CreatedAt = preset.CreatedAt,
                               });
        }

        var defaultName = Truncate(dto.DefaultPreset?.Trim(), MaxPresetNameLength);
        // A pointer at a preset that did not survive sanitisation would silently apply nothing.
        result.DefaultPreset = result.Presets.Any(p => string.Equals(p.Name, defaultName, StringComparison.OrdinalIgnoreCase))
            ? defaultName
            : null;

        return result;
    }

    private static Dictionary<string, string> SanitizeQuery(Dictionary<string, string>? query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (query == null) return result;

        foreach (var (key, value) in query)
        {
            if (!AllowedQueryKeys.Contains(key)) continue;
            if (string.IsNullOrEmpty(value) || value.Length > MaxQueryValueLength) continue;
            result[key] = value;
        }

        return result;
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length > maxLength ? value[..maxLength] : value;
    }
}
