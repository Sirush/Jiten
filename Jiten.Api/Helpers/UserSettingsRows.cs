using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Helpers;

public static class UserSettingsRows
{
    /// <summary>Row-locks the settings row on Postgres so two devices writing the same document cannot drop each other's change; call inside a transaction.</summary>
    public static async Task<UserSettings> LoadForUpdate(UserDbContext context, string userId)
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
