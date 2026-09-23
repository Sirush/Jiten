using Hangfire;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

/// <summary>Recomputes users who follow <see cref="FsrsVersions.Unoptimised"/> but hold states from the other model; idempotent.</summary>
public class FsrsModelMigrationJob(
    IDbContextFactory<UserDbContext> userContextFactory,
    SrsRecomputeJob recomputeJob,
    ILogger<FsrsModelMigrationJob> logger)
{
    [Queue("default")]
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public async Task Run()
    {
        var target = FsrsVersions.Unoptimised;
        await using var userContext = await userContextFactory.CreateDbContextAsync();

        // Under FSRS-7 a reviewed card without a fast trace was last written by FSRS-6; under FSRS-6 any fast trace is stale.
        var candidates = target == FsrsVersion.V7
            ? await userContext.FsrsCards.Where(c => c.Stability != null && c.StabilityFast == null)
                               .Select(c => c.UserId).Distinct().ToListAsync()
            : await userContext.FsrsCards.Where(c => c.StabilityFast != null)
                               .Select(c => c.UserId).Distinct().ToListAsync();

        var settingsByUser = await userContext.UserFsrsSettings.AsNoTracking()
                                              .Where(s => candidates.Contains(s.UserId))
                                              .ToDictionaryAsync(s => s.UserId);

        var migrated = 0;
        foreach (var userId in candidates)
        {
            var (_, version) = FsrsSettingsHelper.ResolveParameters(settingsByUser.GetValueOrDefault(userId));
            if (version != target)
                continue;

            await recomputeJob.RecomputeMemoryStates(userId);
            migrated++;
        }

        logger.LogInformation("FSRS model migration to FSRS-{Version}: recomputed {Migrated} of {Candidates} candidate users",
                              (int)target, migrated, candidates.Count);
    }
}
