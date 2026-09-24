using Hangfire;
using Jiten.Api.Helpers;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

/// <summary>
/// Recounts every card's and archive row's lapses from its log. Only ever lowers a count: a higher recount is a
/// user's cleared lapses, which must stay cleared.
/// </summary>
public class LapseRecountJob(IDbContextFactory<UserDbContext> userContextFactory, ILogger<LapseRecountJob> logger)
{
    private const int BatchSize = 1000;

    [Queue("default")]
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public async Task RecountAll()
    {
        await using var userContext = await userContextFactory.CreateDbContextAsync();
        var schedulers = new Dictionary<string, FsrsScheduler>();

        async Task<FsrsScheduler> SchedulerFor(string userId)
        {
            if (!schedulers.TryGetValue(userId, out var scheduler))
            {
                scheduler = FsrsSettingsHelper.CreateScheduler(await FsrsSettingsHelper.LoadAsync(userContext, userId), enableFuzzing: false);
                schedulers[userId] = scheduler;
            }

            return scheduler;
        }

        var cardsLowered = 0;
        var lastCardId = 0L;
        while (true)
        {
            var cards = await userContext.FsrsCards.AsNoTracking()
                                         .Where(c => c.CardId > lastCardId && c.Lapses > 0)
                                         .OrderBy(c => c.CardId)
                                         .Take(BatchSize)
                                         .Select(c => new { c.CardId, c.UserId, c.Lapses })
                                         .ToListAsync();
            if (cards.Count == 0)
                break;

            var cardIds = cards.Select(c => c.CardId).ToList();
            var logsByCard = (await userContext.FsrsReviewLogs.AsNoTracking()
                                               .Where(l => cardIds.Contains(l.CardId))
                                               .ToListAsync())
                             .GroupBy(l => l.CardId)
                             .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var card in cards)
            {
                if (!logsByCard.TryGetValue(card.CardId, out var logs))
                    continue;

                var recounted = FsrsReplay.CountLapses(logs, await SchedulerFor(card.UserId));
                if (recounted >= card.Lapses)
                    continue;

                // Conditional on the count read, so a review landing mid-run is never overwritten.
                cardsLowered += await userContext.FsrsCards
                                                 .Where(c => c.CardId == card.CardId && c.Lapses == card.Lapses)
                                                 .ExecuteUpdateAsync(s => s.SetProperty(c => c.Lapses, recounted));
            }

            lastCardId = cards[^1].CardId;
        }

        var archivesLowered = 0;
        var lastArchiveId = 0L;
        while (true)
        {
            var rows = await userContext.FsrsCardArchives
                                        .Where(a => a.ArchiveId > lastArchiveId && a.Lapses > 0)
                                        .OrderBy(a => a.ArchiveId)
                                        .Take(BatchSize)
                                        .ToListAsync();
            if (rows.Count == 0)
                break;

            foreach (var row in rows)
            {
                var (reviews, corrupt) = CardArchiveService.ReadReviews(row);
                if (corrupt || reviews.Count == 0)
                    continue;

                var logs = reviews.Select(r => new FsrsReviewLog(0, r.Rating, r.ReviewDateTime, r.ReviewDuration)).ToList();
                var recounted = FsrsReplay.CountLapses(logs, await SchedulerFor(row.UserId));
                if (recounted >= row.Lapses)
                    continue;

                row.Lapses = recounted;
                archivesLowered++;
            }

            lastArchiveId = rows[^1].ArchiveId;
            await userContext.SaveChangesAsync();
            userContext.ChangeTracker.Clear();
        }

        logger.LogInformation("Lapse recount lowered {Cards} cards and {Archives} archived cards across {Users} users",
                              cardsLowered, archivesLowered, schedulers.Count);
    }
}
