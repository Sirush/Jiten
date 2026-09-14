using Hangfire;
using Jiten.Api.Services.SmartDeck;
using Jiten.Core;
using Jiten.Core.Services.SmartDeck;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

public class SmartDeckJob(
    ISmartDeckBuilder builder,
    ISmartDeckDirtyService dirtyService,
    IDbContextFactory<UserDbContext> userContextFactory,
    IBackgroundJobClient backgroundJobs,
    ILogger<SmartDeckJob> logger)
{
    private static readonly object GateLock = new();
    private static readonly HashSet<string> RebuildingUserIds = new();

    [Queue("default")]
    [AutomaticRetry(Attempts = 1)]
    public async Task Rebuild(string userId)
    {
        bool acquired;
        lock (GateLock)
        {
            acquired = RebuildingUserIds.Add(userId);
        }
        if (!acquired)
        {
            await dirtyService.MarkDirty(userId);
            return;
        }

        try
        {
            await dirtyService.ConsumeDirty(userId);

            var result = await builder.Rebuild(userId);
            if (!result.Built)
                logger.LogDebug("Smart deck rebuild skipped for {UserId}: {Reason}", userId, result.SkipReason);
        }
        finally
        {
            await dirtyService.ReleaseQueuedGate(userId);
            lock (GateLock)
            {
                RebuildingUserIds.Remove(userId);
            }
        }

        if (await dirtyService.ConsumeDirty(userId) > 0)
            await dirtyService.MarkDirty(userId);
    }

    [Queue("default")]
    public async Task NightlySweep()
    {
        await using var userContext = await userContextFactory.CreateDbContextAsync();

        var candidates = await userContext.UserStudyDecks.AsNoTracking()
                                          .Where(sd => sd.DeckType == StudyDeckType.Smart)
                                          .Select(sd => sd.UserId)
                                          .ToListAsync();
        if (candidates.Count == 0) return;

        var settingsByUser = await userContext.UserSettings.AsNoTracking()
                                              .Where(us => candidates.Contains(us.UserId))
                                              .Select(us => new { us.UserId, us.SmartDeckJson })
                                              .ToDictionaryAsync(us => us.UserId, us => us.SmartDeckJson);

        var queued = 0;
        foreach (var userId in candidates)
        {
            var enabled = SmartDeckSettings.Parse(settingsByUser.GetValueOrDefault(userId)).Enabled;
            await dirtyService.SetEnabled(userId, enabled);
            if (!enabled) continue;

            backgroundJobs.Enqueue<SmartDeckJob>(job => job.Rebuild(userId));
            queued++;
        }

        logger.LogInformation("Smart deck nightly sweep queued {Queued} of {Total} decks", queued, candidates.Count);
    }
}
