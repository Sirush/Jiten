using Hangfire;
using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

public class SrsRecomputeJob(
    IDbContextFactory<UserDbContext> userContextFactory,
    ILogger<SrsRecomputeJob> logger)
{
    private const int BatchSize = 500;
    private const int PreviewBatchSize = 2000;

    private static async Task<double> ResolveOffsetHours(UserDbContext userContext, string userId)
    {
        var studySettings = FsrsSettingsHelper.GetStudySettings(await FsrsSettingsHelper.LoadAsync(userContext, userId));
        return FsrsSettingsHelper.ResolveOffsetHours(DateTime.UtcNow, studySettings.Timezone);
    }

    [Queue("default")]
    public async Task RecomputeUserSrs(string userId, double[] parameters, double desiredRetention, bool loadBalance = true,
                                       EasyDaysPolicy? easyDays = null)
    {
        // Single-shot recompute: one in-memory balancer accumulates across all batches, so every card is
        // placed against the freshly-rebalanced schedule built so far (online greedy balancing).
        DictionaryFsrsLoadBalancer? balancer = null;
        if (loadBalance)
        {
            await using var settingsContext = await userContextFactory.CreateDbContextAsync();
            balancer = new DictionaryFsrsLoadBalancer(offsetHours: await ResolveOffsetHours(settingsContext, userId));
        }
        var lastCardId = 0L;

        while (true)
        {
            var result = await RecomputeUserSrsBatch(userId, parameters, desiredRetention, lastCardId, BatchSize, loadBalance, balancer, easyDays);
            if (result.Processed == 0 || result.Done)
            {
                break;
            }

            lastCardId = result.LastCardId;
        }

        logger.LogInformation("Recomputed FSRS scheduling for user {UserId}", userId);
    }

    /// <summary>Rebuilds every card's and archive row's memory state from its log, keeping due dates; run whenever the user's FSRS version changes.</summary>
    [Queue("default")]
    public async Task RecomputeMemoryStates(string userId)
    {
        await using var userContext = await userContextFactory.CreateDbContextAsync();
        var scheduler = FsrsSettingsHelper.CreateScheduler(await FsrsSettingsHelper.LoadAsync(userContext, userId), enableFuzzing: false);

        var lastCardId = 0L;
        while (true)
        {
            var cards = await userContext.FsrsCards
                                         .Where(card => card.UserId == userId && card.CardId > lastCardId)
                                         .OrderBy(card => card.CardId)
                                         .Take(BatchSize)
                                         .ToListAsync();
            if (cards.Count == 0)
                break;

            var cardIds = cards.Select(card => card.CardId).ToList();
            var logsByCard = (await userContext.FsrsReviewLogs
                                               .AsNoTracking()
                                               .Where(log => cardIds.Contains(log.CardId))
                                               .ToListAsync())
                             .GroupBy(log => log.CardId)
                             .ToDictionary(group => group.Key, group => group.ToList());

            foreach (var card in cards)
            {
                if (logsByCard.TryGetValue(card.CardId, out var cardLogs)
                    && FsrsReplay.Recompute(card, cardLogs, scheduler, scheduler, preserveSchedule: true))
                    continue;

                if (scheduler.Version != FsrsVersion.V7)
                    card.StabilityFast = null;
                else if (card.Stability != null)
                    // Without a log there is nothing to replay; pinning the fallback keeps the migration job from revisiting the card.
                    card.StabilityFast ??= FsrsHelperV7.FromCard(card).StabilityFast;
            }

            lastCardId = cards[^1].CardId;
            await userContext.SaveChangesAsync();
            userContext.ChangeTracker.Clear();
        }

        var lastArchiveId = 0L;
        while (true)
        {
            var rows = await userContext.FsrsCardArchives
                                        .Where(a => a.UserId == userId && a.ArchiveId > lastArchiveId)
                                        .OrderBy(a => a.ArchiveId)
                                        .Take(BatchSize)
                                        .ToListAsync();
            if (rows.Count == 0)
                break;

            foreach (var row in rows)
            {
                var (reviews, corrupt) = CardArchiveService.ReadReviews(row);
                if (corrupt || reviews.Count == 0)
                {
                    if (scheduler.Version != FsrsVersion.V7)
                        row.StabilityFast = null;
                    continue;
                }

                var probe = new FsrsCard(userId, row.WordId, row.ReadingIndex);
                var logs = reviews.Select(r => new FsrsReviewLog(0, r.Rating, r.ReviewDateTime, r.ReviewDuration)).ToList();
                FsrsReplay.Recompute(probe, logs, scheduler, scheduler, preserveTerminalState: false);
                row.Stability = probe.Stability;
                row.Difficulty = probe.Difficulty;
                row.StabilityFast = probe.StabilityFast;
            }

            lastArchiveId = rows[^1].ArchiveId;
            await userContext.SaveChangesAsync();
            userContext.ChangeTracker.Clear();
        }

        logger.LogInformation("Recomputed FSRS-{Version} memory states for user {UserId}", (int)scheduler.Version, userId);
    }

    /// <summary>Due counts a reschedule would leave today and over the next week, without saving; the week is approximate when load balancing is off.</summary>
    /// <param name="reviewScope">Filters to the cards the study page serves under "review from study decks only"; null counts every card.</param>
    public async Task<ReschedulePreviewResponse> PreviewDueCounts(string userId, double[] parameters, IReadOnlyList<double> desiredRetentions,
                                                                  Func<List<(int WordId, byte ReadingIndex)>, Task<HashSet<long>>>? reviewScope = null)
    {
        await using var userContext = await userContextFactory.CreateDbContextAsync();
        var studySettings = FsrsSettingsHelper.GetStudySettings(await FsrsSettingsHelper.LoadAsync(userContext, userId));
        var now = DateTime.UtcNow;
        var window = SrsDueWindow.At(now, studySettings);
        var weekCutoff = window.Review.AddDays(UpcomingDays);
        var todayStart = FsrsSettingsHelper.LocalDayStartUtc(now, studySettings.Timezone);

        var offsetHours = FsrsSettingsHelper.ResolveOffsetHours(now, studySettings.Timezone);
        var easyDays = FsrsSettingsHelper.BuildEasyDaysPolicy(studySettings, now);
        var schedulers = desiredRetentions
                         .Select(retention => CreateRescheduler(studySettings, parameters, retention, now,
                                                                studySettings.LoadBalancing ? new DictionaryFsrsLoadBalancer(offsetHours: offsetHours) : null,
                                                                easyDays, enableFuzzing: studySettings.LoadBalancing))
                         .ToList();
        var replayScheduler = FsrsSettingsHelper.CreateScheduler(studySettings, parameters, desiredRetentions[0], enableFuzzing: false);

        // Bit 0: due now; bit i + 1: due after rescheduling at desiredRetentions[i]. The same layout shifted by UpcomingShift marks the following week.
        var dueMasks = new Dictionary<(int WordId, byte ReadingIndex), int>();
        var lastCardId = 0L;

        while (true)
        {
            var cards = await userContext.FsrsCards
                                         .AsNoTracking()
                                         .Where(card => card.UserId == userId && card.CardId > lastCardId)
                                         .OrderBy(card => card.CardId)
                                         .Take(PreviewBatchSize)
                                         .Select(card => new { card.CardId, card.WordId, card.ReadingIndex, card.State, card.Due, card.LastReview })
                                         .ToListAsync();
            if (cards.Count == 0)
                break;

            var cardIds = cards.Select(card => card.CardId).ToList();
            var logsByCard = (await userContext.FsrsReviewLogs
                                               .AsNoTracking()
                                               .Where(log => cardIds.Contains(log.CardId))
                                               .ToListAsync())
                             .GroupBy(log => log.CardId)
                             .ToDictionary(group => group.Key, group => group.ToList());

            foreach (var card in cards)
            {
                var mask = DueBucketBit(window, weekCutoff, now, todayStart, card.State, card.Due, card.LastReview);

                // Replay keeps these states, and they never come due.
                if (card.State is not (FsrsState.Mastered or FsrsState.Blacklisted or FsrsState.Suspended))
                {
                    var placements = logsByCard.TryGetValue(card.CardId, out var cardLogs)
                        ? FsrsReplay.ProjectFinalPlacements(cardLogs, replayScheduler, schedulers)
                        : null;

                    for (var i = 0; i < schedulers.Count; i++)
                    {
                        var (state, due, lastReview) = placements?[i] ?? (card.State, card.Due, card.LastReview);
                        mask |= DueBucketBit(window, weekCutoff, now, todayStart, state, due, lastReview) << (i + 1);
                    }
                }

                if (mask != 0)
                    dueMasks[(card.WordId, card.ReadingIndex)] = mask;
            }

            lastCardId = cards[^1].CardId;
        }

        var served = reviewScope == null ? null : await reviewScope(dueMasks.Keys.ToList());
        var counts = new int[schedulers.Count + 1];
        var upcoming = new int[schedulers.Count + 1];
        foreach (var (key, mask) in dueMasks)
        {
            if (served != null && !served.Contains(WordFormHelper.EncodeWordKey(key.WordId, key.ReadingIndex)))
                continue;
            for (var bit = 0; bit < counts.Length; bit++)
            {
                if ((mask & (1 << bit)) != 0)
                    counts[bit]++;
                if ((mask & (1 << (bit + UpcomingShift))) != 0)
                    upcoming[bit]++;
            }
        }

        return new ReschedulePreviewResponse
        {
            CurrentDue = counts[0],
            CurrentUpcoming = upcoming[0],
            UpcomingDays = UpcomingDays,
            Options = desiredRetentions.Select((retention, i) => new ReschedulePreviewOption
                                       {
                                           DesiredRetention = retention, Due = counts[i + 1], Upcoming = upcoming[i + 1]
                                       })
                                       .ToList()
        };
    }

    /// <summary>Schedules a reschedule's surviving placement; <see cref="PreviewDueCounts"/> builds the same one so its counts match.</summary>
    /// <remarks>The fuzz cutoff stops fuzz anchored at a past review from landing cards on days already gone, which would add to today's due count.</remarks>
    private static FsrsScheduler CreateRescheduler(StudySettingsDto studySettings, double[] parameters, double desiredRetention, DateTime utcNow,
                                                   IFsrsLoadBalancer? balancer, EasyDaysPolicy? easyDays, bool enableFuzzing)
        => FsrsSettingsHelper.CreateScheduler(studySettings, parameters, desiredRetention, enableFuzzing, balancer, easyDays,
                                              SrsDueWindow.ReviewCutoff(utcNow, studySettings));

    private const int UpcomingDays = 7;
    private const int UpcomingShift = 16;

    /// <summary>1 when the card is due now, 1 shifted by <see cref="UpcomingShift"/> when it comes due later in the week, else 0.</summary>
    private static int DueBucketBit(SrsDueWindow window, DateTime weekCutoff, DateTime utcNow, DateTime todayStart,
                                    FsrsState state, DateTime due, DateTime? lastReview)
    {
        if (state is not (FsrsState.Learning or FsrsState.Review or FsrsState.Relearning))
            return 0;
        if (CountsAsDueReview(window, utcNow, todayStart, state, due, lastReview))
            return 1;
        return due <= weekCutoff ? 1 << UpcomingShift : 0;
    }

    /// <summary>Mirrors the study page: a Review card graded today waits for its real due time even inside the day-boundary cutoff.</summary>
    private static bool CountsAsDueReview(SrsDueWindow window, DateTime utcNow, DateTime todayStart,
                                          FsrsState state, DateTime due, DateTime? lastReview)
        => (state is FsrsState.Learning or FsrsState.Review or FsrsState.Relearning)
           && window.IsDue(state, due)
           && (due <= utcNow || state != FsrsState.Review || lastReview == null || lastReview < todayStart);

    /// <param name="sharedBalancer">
    /// When provided (single-shot loop), used and accumulated across batches. When null and
    /// <paramref name="loadBalance"/> is true (stateless client-driven batches), a fresh balancer is seeded
    /// from the user's current schedule in the database — which already reflects prior batches' saved
    /// placements — so balancing still works across independent HTTP calls.
    /// </param>
    public async Task<SrsRecomputeBatchResponse> RecomputeUserSrsBatch(string userId, double[] parameters, double desiredRetention,
                                                                       long lastCardId, int batchSize, bool loadBalance = true,
                                                                       IFsrsLoadBalancer? sharedBalancer = null,
                                                                       EasyDaysPolicy? easyDays = null)
    {
        await using var userContext = await userContextFactory.CreateDbContextAsync();

        IFsrsLoadBalancer? balancer = null;
        if (loadBalance)
        {
            balancer = sharedBalancer
                       ?? await FsrsLoadBalancerSeeder.SeedAsync(userContext, userId, await ResolveOffsetHours(userContext, userId));
        }

        var studySettings = FsrsSettingsHelper.GetStudySettings(await FsrsSettingsHelper.LoadAsync(userContext, userId));
        var scheduler = CreateRescheduler(studySettings, parameters, desiredRetention, DateTime.UtcNow, balancer, easyDays, enableFuzzing: true);
        // Replay scheduler for historical reviews: their due dates are superseded by the next review,
        // so fuzzing/balancing them would only register phantom load in the balancer's histogram.
        // Stability/difficulty depend solely on log timestamps, so skipping fuzz changes nothing else
        // and makes the replay deterministic.
        var replayScheduler = FsrsSettingsHelper.CreateScheduler(studySettings, parameters, desiredRetention, enableFuzzing: false);

        var total = await userContext.FsrsCards.CountAsync(card => card.UserId == userId);
        var cards = await userContext.FsrsCards
                                     .Where(card => card.UserId == userId && card.CardId > lastCardId)
                                     .OrderBy(card => card.CardId)
                                     .Take(batchSize)
                                     .ToListAsync();

        if (cards.Count == 0)
        {
            return new SrsRecomputeBatchResponse
            {
                Processed = 0,
                Total = total,
                LastCardId = lastCardId,
                Done = true
            };
        }

        var cardIds = cards.Select(card => card.CardId).ToList();
        var logs = await userContext.FsrsReviewLogs
                                    .AsNoTracking()
                                    .Where(log => cardIds.Contains(log.CardId))
                                    .OrderBy(log => log.ReviewDateTime)
                                    .ThenBy(log => log.ReviewLogId)
                                    .ToListAsync();

        var logsByCard = logs.GroupBy(log => log.CardId)
                             .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var card in cards)
        {
            if (logsByCard.TryGetValue(card.CardId, out var cardLogs))
                FsrsReplay.Recompute(card, cardLogs, scheduler, replayScheduler);
        }

        var newLastCardId = cards[^1].CardId;
        await userContext.SaveChangesAsync();
        userContext.ChangeTracker.Clear();

        return new SrsRecomputeBatchResponse
        {
            Processed = cards.Count,
            Total = total,
            LastCardId = newLastCardId,
            Done = cards.Count < batchSize
        };
    }
}
