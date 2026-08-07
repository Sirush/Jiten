using System.Text.Json;
using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Helpers;

/// <summary>Reads a user's FSRS and study settings, falling back to defaults on anything unusable.</summary>
public static class FsrsSettingsHelper
{
    public static Task<UserFsrsSettings?> LoadAsync(UserDbContext userContext, string userId)
        => userContext.UserFsrsSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId);

    public static double[] GetParameters(UserFsrsSettings? settings)
        => TryGetStoredParameters(settings, out var parameters) ? parameters : FsrsConstants.DefaultParameters;

    public static double GetDesiredRetention(UserFsrsSettings? settings)
        => settings?.DesiredRetention is double retention && IsDesiredRetentionValid(retention)
            ? retention
            : FsrsConstants.DefaultDesiredRetention;

    public static bool IsDesiredRetentionValid(double desiredRetention)
        => desiredRetention is > 0 and < 1 && !double.IsNaN(desiredRetention) && !double.IsInfinity(desiredRetention);

    public static bool TryGetStoredParameters(UserFsrsSettings? settings, out double[] parameters)
    {
        parameters = [];
        if (settings == null)
            return false;

        var stored = settings.GetParametersOnce();
        if (stored.Length != FsrsConstants.DefaultParameters.Length)
            return false;
        if (stored.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
            return false;

        parameters = stored;
        return true;
    }

    public static StudySettingsDto GetStudySettings(UserFsrsSettings? settings)
    {
        if (settings?.SettingsJson is { Length: > 2 } json)
        {
            try { return JsonSerializer.Deserialize<StudySettingsDto>(json) ?? new StudySettingsDto(); }
            catch (JsonException) { }
        }

        return new StudySettingsDto();
    }

    public static double ResolveOffsetHours(DateTime utcNow, string? timezone)
        => ResolveTimeZone(timezone)?.GetUtcOffset(utcNow).TotalHours ?? 0;

    /// <summary>Null means UTC, both for an unset setting and for an id this machine cannot resolve.</summary>
    public static TimeZoneInfo? ResolveTimeZone(string? timezone)
    {
        if (string.IsNullOrEmpty(timezone)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(timezone); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }
}
