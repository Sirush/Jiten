using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Helpers;

public static class CoverageDirtyHelper
{
    public static async Task<List<string>> MarkWordSetSubscribersDirty(UserDbContext userContext, int setId)
    {
        var userIds = await userContext.UserWordSetStates
                                       .AsNoTracking()
                                       .Where(s => s.SetId == setId)
                                       .Select(s => s.UserId)
                                       .ToListAsync();
        if (userIds.Count == 0)
            return userIds;

        if (userContext.Database.ProviderName?.Contains("Npgsql") != true)
        {
            foreach (var userId in userIds)
                await MarkCoverageDirty(userContext, userId);
            return userIds;
        }

        var now = DateTime.UtcNow;
        await userContext.Database.ExecuteSqlRawAsync("""
            UPDATE "user"."UserMetadatas"
            SET "CoverageDirty" = TRUE,
                "CoverageDirtyAt" = {1}::timestamptz
            WHERE "UserId" IN (SELECT "UserId" FROM "user"."UserWordSetStates" WHERE "SetId" = {0})
            """, setId, now);
        await userContext.Database.ExecuteSqlRawAsync("""
            INSERT INTO "user"."UserMetadatas" ("UserId", "CoverageDirty", "CoverageDirtyAt", "CoverageRefreshedAt")
            SELECT "UserId", TRUE, {1}::timestamptz, NULL
            FROM "user"."UserWordSetStates"
            WHERE "SetId" = {0}
            ON CONFLICT DO NOTHING
            """, setId, now);

        return userIds;
    }

    public static async Task MarkCoverageDirty(UserDbContext userContext, string userId, DateTime? dirtyAt = null)
    {
        var now = dirtyAt ?? DateTime.UtcNow;
        var isNpgsql = userContext.Database.ProviderName?.Contains("Npgsql") == true;

        if (isNpgsql)
        {
            var userGuid = Guid.Parse(userId);
            var rows = await userContext.Database.ExecuteSqlRawAsync("""
                UPDATE "user"."UserMetadatas"
                SET "CoverageDirty" = TRUE,
                    "CoverageDirtyAt" = {1}::timestamptz
                WHERE "UserId" = {0}::uuid
                """, userGuid, now);

            if (rows == 0)
            {
                await userContext.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "user"."UserMetadatas" ("UserId", "CoverageDirty", "CoverageDirtyAt", "CoverageRefreshedAt")
                    VALUES ({0}::uuid, TRUE, {1}::timestamptz, NULL)
                    ON CONFLICT DO NOTHING
                    """, userGuid, now);
            }
        }
        else
        {
            var metadata = await userContext.UserMetadatas.FirstOrDefaultAsync(m => m.UserId == userId);
            if (metadata != null)
            {
                metadata.CoverageDirty = true;
                metadata.CoverageDirtyAt = now;
            }
            else
            {
                userContext.UserMetadatas.Add(new UserMetadata
                {
                    UserId = userId,
                    CoverageDirty = true,
                    CoverageDirtyAt = now
                });
            }
            await userContext.SaveChangesAsync();
        }
    }
}

