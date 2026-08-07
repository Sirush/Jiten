using Hangfire;
using Jiten.Api.Dtos;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Helpers;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Swashbuckle.AspNetCore.Annotations;
using System.Text;
using System.Text.Json;
using WanaKanaShaapu;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/user")]
[Authorize]
public class UserController(
    ICurrentUserService userService,
    JitenDbContext jitenContext,
    IDbContextFactory<JitenDbContext> contextFactory,
    UserDbContext userContext,
    IBackgroundJobClient backgroundJobs,
    IWordFormSiblingCache wordFormCache,
    IConfiguration configuration,
    IConnectionMultiplexer redis,
    IDeckWordResolver deckWordResolver,
    IDeckDownloadService downloadService,
    IUserLimitsService userLimits,
    IStudySessionService sessionService,
    ILogger<UserController> logger) : ControllerBase
{
    private const int MaxAnkiTxtBytes = 50 * 1024 * 1024;

    private const int MaxAnkiTxtLines = 50_000;

    private const int MaxBulkRestore = 2000;

    private const int PurgeCardChunkSize = 2000;

    /// <summary>
    /// Get all known JMdict word IDs for the current user.
    /// </summary>
    [HttpGet("vocabulary/known-ids/amount")]
    public async Task<IResult> GetKnownWordAmount()
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var amounts = await ComputeKnownWordAmountAsync(userId);
        amounts.ArchivedCards = await userContext.FsrsCardArchives.AsNoTracking().CountAsync(a => a.UserId == userId);

        return Results.Ok(amounts);
    }

    private async Task<KnownWordAmountDto> ComputeKnownWordAmountAsync(string userId)
    {
        var fsrsCards = await userContext.FsrsCards
                                         .AsNoTracking()
                                         .Where(uk => uk.UserId == userId)
                                         .Select(uk => new { uk.WordId, uk.ReadingIndex, uk.State, uk.Due, uk.LastReview })
                                         .ToListAsync();

        var now = DateTime.UtcNow;

        // Build effective state per (WordId, ReadingIndex) form.
        // FSRS cards take precedence over WordSet membership per form.
        var effectiveForms = new Dictionary<(int WordId, int ReadingIndex), KnownState>();
        foreach (var c in fsrsCards)
        {
            effectiveForms[(c.WordId, c.ReadingIndex)] =
                ComputeEffectiveCategory(c.State, c.Due, c.LastReview, now) ?? KnownState.New;
        }

        // Expand kanji-kana redundancy: build set of kana forms covered by known kanji cards
        // (deferred merge — WordSets take priority over kana redundancy)
        var redundantExpansions = new Dictionary<(int, int), KnownState>();
        foreach (var kvp in effectiveForms)
        {
            if (kvp.Value == KnownState.New) continue;
            var kanaIndexes = wordFormCache.GetKanaIndexesForKanji(kvp.Key.WordId, (byte)kvp.Key.ReadingIndex);
            if (kanaIndexes == null) continue;
            foreach (var kanaIdx in kanaIndexes)
            {
                var kanaKey = (kvp.Key.WordId, (int)kanaIdx);
                if (effectiveForms.ContainsKey(kanaKey)) continue;

                if (!redundantExpansions.TryGetValue(kanaKey, out var existing) ||
                    StateRank(kvp.Value) > StateRank(existing))
                    redundantExpansions[kanaKey] = kvp.Value;
            }
        }

        // Count FSRS-only forms and words
        int youngForms = 0, matureForms = 0, masteredForms = 0, blacklistedForms = 0;
        foreach (var state in effectiveForms.Values)
        {
            switch (state)
            {
                case KnownState.Young: youngForms++; break;
                case KnownState.Mature: matureForms++; break;
                case KnownState.Mastered: masteredForms++; break;
                case KnownState.Blacklisted: blacklistedForms++; break;
            }
        }

        int youngWords = 0, matureWords = 0, masteredWords = 0, blacklistedWords = 0;
        foreach (var wordGroup in effectiveForms.GroupBy(kvp => kvp.Key.WordId))
        {
            var best = wordGroup.Max(kvp => kvp.Value);
            switch (best)
            {
                case KnownState.Young: youngWords++; break;
                case KnownState.Mature: matureWords++; break;
                case KnownState.Mastered: masteredWords++; break;
                case KnownState.Blacklisted: blacklistedWords++; break;
            }
        }

        var fsrsWordIds = new HashSet<int>(effectiveForms.Keys.Select(k => k.WordId));

        // Word set contributions (only forms/words not already in FSRS)
        int wsMasteredForms = 0, wsBlacklistedForms = 0;
        var wsMasteredWordIds = new HashSet<int>();
        var wsBlacklistedWordIds = new HashSet<int>();

        var userSetStates = await userContext.UserWordSetStates
            .Where(uwss => uwss.UserId == userId)
            .ToListAsync();

        if (userSetStates.Count > 0)
        {
            var masteredSetIds = userSetStates
                .Where(s => s.State == WordSetStateType.Mastered).Select(s => s.SetId).ToList();
            var blacklistedSetIds = userSetStates
                .Where(s => s.State == WordSetStateType.Blacklisted).Select(s => s.SetId).ToList();

            if (masteredSetIds.Count > 0)
            {
                var masteredSetForms = await jitenContext.WordSetMembers
                    .Where(wsm => masteredSetIds.Contains(wsm.SetId))
                    .Select(wsm => new { wsm.WordId, ReadingIndex = (int)wsm.ReadingIndex })
                    .Distinct()
                    .ToListAsync();

                foreach (var m in masteredSetForms)
                {
                    var key = (m.WordId, m.ReadingIndex);
                    if (!effectiveForms.ContainsKey(key))
                    {
                        effectiveForms[key] = KnownState.Mastered;
                        wsMasteredForms++;
                        if (!fsrsWordIds.Contains(m.WordId))
                            wsMasteredWordIds.Add(m.WordId);
                    }
                }
            }

            if (blacklistedSetIds.Count > 0)
            {
                var blacklistedSetForms = await jitenContext.WordSetMembers
                    .Where(wsm => blacklistedSetIds.Contains(wsm.SetId))
                    .Select(wsm => new { wsm.WordId, ReadingIndex = (int)wsm.ReadingIndex })
                    .Distinct()
                    .ToListAsync();

                foreach (var m in blacklistedSetForms)
                {
                    var key = (m.WordId, m.ReadingIndex);
                    if (!effectiveForms.ContainsKey(key))
                    {
                        effectiveForms[key] = KnownState.Blacklisted;
                        wsBlacklistedForms++;
                        if (!fsrsWordIds.Contains(m.WordId))
                            wsBlacklistedWordIds.Add(m.WordId);
                    }
                }
            }
        }

        // Merge kana redundancy after WordSets (WordSet > KanaRedundancy priority)
        int redundantForms = 0;
        foreach (var (key, state) in redundantExpansions)
        {
            if (effectiveForms.TryAdd(key, state))
                redundantForms++;
        }

        return new KnownWordAmountDto
               {
                   Young = youngWords,
                   YoungForm = youngForms,
                   Mature = matureWords,
                   MatureForm = matureForms,
                   Mastered = masteredWords,
                   MasteredForm = masteredForms,
                   Blacklisted = blacklistedWords,
                   BlacklistedForm = blacklistedForms,
                   WordSetMastered = wsMasteredWordIds.Count,
                   WordSetMasteredForm = wsMasteredForms,
                   WordSetBlacklisted = wsBlacklistedWordIds.Count,
                   WordSetBlacklistedForm = wsBlacklistedForms,
                   RedundantForms = redundantForms
               };
    }

    /// <summary>
    /// Get all known JMdict word IDs for the current user.
    /// </summary>
    [HttpGet("vocabulary/known-ids")]
    public async Task<IResult> GetKnownWordIds()
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var ids = await userContext.FsrsCards
                                   .AsNoTracking()
                                   .Where(uk => uk.UserId == userId)
                                   .Select(uk => new { uk.WordId, uk.ReadingIndex })
                                   .ToListAsync();


        return Results.Ok(ids);
    }

    /// <summary>
    /// Remove all known words for the current user.
    /// </summary>
    [HttpDelete("vocabulary/known-ids/clear")]
    public async Task<IResult> ClearKnownWordIds()
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var cards = await userContext.FsrsCards
                                     .Where(uk => uk.UserId == userId)
                                     .ToListAsync();
        var cardIds = cards.Select(c => c.CardId).ToList();
        var reviewLogs = await userContext.FsrsReviewLogs
                                          .Where(rl => cardIds.Contains(rl.CardId))
                                          .ToListAsync();
        if (cards.Count == 0) return Results.Ok(new { removed = 0 });

        // Deliberately not archived: this is a Danger Zone action behind an explicit confirm, archiving would
        // double-write the whole card table, and backup export already covers "I want this back".
        userContext.FsrsReviewLogs.RemoveRange(reviewLogs);
        userContext.FsrsCards.RemoveRange(cards);
        await userContext.SaveChangesAsync();

        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
        await userContext.SaveChangesAsync();
        backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserCoverage(userId));

        await MarkReviewRollupDirty(userId);

        logger.LogInformation("User cleared all known words: UserId={UserId}, RemovedCount={RemovedCount}, RemovedLogsCount={RemovedLogsCount}",
                              userId, cards.Count, reviewLogs.Count);
        return Results.Ok(new { removed = cards.Count, removedLogs = reviewLogs.Count });
    }

    /// <summary>
    /// Add known words for the current user by JMdict word IDs, filtered by reading frequency.
    /// </summary>
    [HttpPost("vocabulary/import-from-ids")]
    public async Task<IResult> ImportWordsFromIds([FromBody] ImportFromIdsRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var knownIds = (request.WordIds ?? []).Where(id => id > 0).Distinct().ToList();
        var blacklistedIds = (request.BlacklistedWordIds ?? []).Where(id => id > 0).Distinct().ToList();
        var suspendedIds = (request.SuspendedWordIds ?? []).Where(id => id > 0).Distinct().ToList();

        var allIds = knownIds.Union(blacklistedIds).Union(suspendedIds).ToList();
        if (allIds.Count == 0) return Results.BadRequest("No word IDs provided");

        var jmdictWords = await jitenContext.JMDictWords
                                            .AsNoTracking()
                                            .Where(w => allIds.Contains(w.WordId))
                                            .ToListAsync();

        if (jmdictWords.Count == 0) return Results.BadRequest("Invalid words provided");

        var jmdictWordIds = jmdictWords.Select(w => w.WordId).ToList();

        var formFrequencies = await jitenContext.WordFormFrequencies
                                                .AsNoTracking()
                                                .Where(wff => jmdictWordIds.Contains(wff.WordId))
                                                .ToListAsync();
        var formFreqsByWord = formFrequencies
            .GroupBy(wff => wff.WordId)
            .ToDictionary(g => g.Key, g => g.OrderBy(wff => wff.ReadingIndex).ToList());

        var alreadyKnown = await userContext.FsrsCards
                                            .AsNoTracking()
                                            .Where(uk => uk.UserId == userId && jmdictWordIds.Contains(uk.WordId))
                                            .ToListAsync();

        List<FsrsCard> toInsert = new();
        var alreadyKnownSet = alreadyKnown.Select(uk => (uk.WordId, (int)uk.ReadingIndex)).ToHashSet();
        var blacklistedSet = blacklistedIds.ToHashSet();
        var suspendedSet = suspendedIds.ToHashSet();

        var allImportCardKeys = new HashSet<(int WordId, byte ReadingIndex)>();
        foreach (var word in jmdictWords)
        {
            var indices = GetReadingIndicesToImport(formFreqsByWord.GetValueOrDefault(word.WordId), request.FrequencyThreshold);
            foreach (var i in indices)
                allImportCardKeys.Add((word.WordId, (byte)i));
        }
        foreach (var k in alreadyKnownSet)
            allImportCardKeys.Add((k.WordId, (byte)k.Item2));

        foreach (var word in jmdictWords)
        {
            var wordFormFreqs = formFreqsByWord.GetValueOrDefault(word.WordId);
            var readingIndicesToImport = GetReadingIndicesToImport(wordFormFreqs, request.FrequencyThreshold);

            var state = blacklistedSet.Contains(word.WordId) ? FsrsState.Blacklisted
                      : suspendedSet.Contains(word.WordId)   ? FsrsState.Suspended
                      : FsrsState.Mastered;

            foreach (var i in readingIndicesToImport)
            {
                if (alreadyKnownSet.Contains((word.WordId, i)))
                    continue;

                toInsert.Add(new FsrsCard(userId, word.WordId, (byte)i, due: DateTime.UtcNow, lastReview: DateTime.UtcNow,
                                          state: state));
            }
        }

        var redundantIds = await WordFormHelper.ArchiveRedundantImportCards(
            userContext, wordFormCache, userId, toInsert, allImportCardKeys, _ => []);
        toInsert.RemoveAll(redundantIds.Contains);

        if (toInsert.Count > 0)
        {
            await userContext.FsrsCards.AddRangeAsync(toInsert);
            await userContext.SaveChangesAsync();
        }

        var pruned = await WordFormHelper.PruneRedundantForms(userContext, wordFormCache, userId, jmdictWordIds);
        if (pruned > 0)
            await userContext.SaveChangesAsync();

        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
        await userContext.SaveChangesAsync();
        backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserCoverage(userId));

        logger.LogInformation("User imported words from IDs: UserId={UserId}, AddedCount={AddedCount}, SkippedCount={SkippedCount}, PrunedCount={PrunedCount}",
                              userId, toInsert.Count, alreadyKnown.Count, pruned);
        return Results.Ok(new { added = toInsert.Count, skipped = alreadyKnown.Count, pruned });
    }

    private static List<int> GetReadingIndicesToImport(
        List<JmDictWordFormFrequency>? formFreqs,
        int? threshold)
    {
        if (formFreqs == null || formFreqs.Count <= 1)
            return [0];

        var bestRank = int.MaxValue;
        var bestIndex = 0;

        foreach (var ff in formFreqs)
        {
            if (ff.FrequencyRank > 0 && ff.FrequencyRank < bestRank)
            {
                bestRank = ff.FrequencyRank;
                bestIndex = ff.ReadingIndex;
            }
        }

        if (bestRank == int.MaxValue)
            return [0];

        var result = new List<int> { bestIndex };

        if (threshold == null)
            return result;

        foreach (var ff in formFreqs)
        {
            if (ff.ReadingIndex == bestIndex)
                continue;

            if (ff.FrequencyRank > 0 && ff.FrequencyRank <= bestRank + threshold.Value)
                result.Add(ff.ReadingIndex);
        }

        return result;
    }

    [HttpPost("vocabulary/import-jpdb-reviews")]
    public async Task<IResult> ImportJpdbReviews([FromBody] ImportJpdbReviewsRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        if (request.Cards == null || request.Cards.Count == 0)
            return Results.BadRequest("No cards provided");

        var distinctWordIds = request.Cards
            .Where(c => c.WordId is > 0 and <= int.MaxValue)
            .Select(c => (int)c.WordId).Distinct().ToList();

        var wordForms = await jitenContext.WordForms
                                          .AsNoTracking()
                                          .Where(wf => distinctWordIds.Contains(wf.WordId))
                                          .ToListAsync();

        var formsByWord = wordForms.GroupBy(wf => wf.WordId)
                                   .ToDictionary(g => g.Key, g => g.ToList());

        var existingCards = await userContext.FsrsCards
                                             .Where(c => c.UserId == userId && distinctWordIds.Contains(c.WordId))
                                             .ToDictionaryAsync(c => (c.WordId, (int)c.ReadingIndex));

        var existingCardIds = existingCards.Values.Select(c => c.CardId).ToList();
        // Tracked so we can update the rating of already-imported reviews on reimport (e.g. after a grade-mapping change),
        // instead of skipping them on a duplicate timestamp.
        var existingLogs = await userContext.FsrsReviewLogs
                                            .Where(l => existingCardIds.Contains(l.CardId))
                                            .ToListAsync();
        var existingLogMap = new Dictionary<(long CardId, DateTime ReviewDateTime), FsrsReviewLog>();
        foreach (var log in existingLogs)
            existingLogMap[(log.CardId, log.ReviewDateTime)] = log;

        var settings = await userContext.UserFsrsSettings.AsNoTracking()
                                        .FirstOrDefaultAsync(s => s.UserId == userId);
        var parameters = settings?.GetParametersOnce() is { Length: > 0 } p ? p : FsrsConstants.DefaultParameters;
        var desiredRetention = settings?.DesiredRetention is double dr and > 0 and < 1 ? dr : FsrsConstants.DefaultDesiredRetention;
        var scheduler = new FsrsScheduler(desiredRetention: desiredRetention, parameters: parameters, enableFuzzing: false);

        var cardsToAdd = new List<FsrsCard>();
        var logsToAdd = new List<FsrsReviewLog>();
        var pendingLogKeys = new HashSet<(int WordId, int ReadingIndex, DateTime ReviewDateTime)>();
        var cardsToReplay = new List<FsrsCard>();
        int skipped = 0;
        int updatedLogs = 0;

        foreach (var jpdbCard in request.Cards)
        {
            if (jpdbCard.WordId is <= 0 or > int.MaxValue)
            {
                skipped++;
                continue;
            }

            var wordId = (int)jpdbCard.WordId;
            if (!formsByWord.TryGetValue(wordId, out var forms))
            {
                skipped++;
                continue;
            }

            var validReviews = jpdbCard.Reviews
                .Where(r => MapJpdbGrade(r.Grade) != null)
                .OrderBy(r => r.Timestamp)
                .ToList();

            if (validReviews.Count == 0)
            {
                skipped++;
                continue;
            }

            byte readingIndex = ResolveReadingIndex(forms, jpdbCard.Spelling);
            var key = (wordId, (int)readingIndex);

            FsrsCard card;
            if (existingCards.TryGetValue(key, out var existingCard))
            {
                card = existingCard;
            }
            else
            {
                card = new FsrsCard(userId, wordId, readingIndex);
                cardsToAdd.Add(card);
                existingCards[key] = card;
            }

            foreach (var review in validReviews)
            {
                var reviewDt = DateTimeOffset.FromUnixTimeSeconds(review.Timestamp).UtcDateTime;
                var rating = MapJpdbGrade(review.Grade)!.Value;

                if (card.CardId > 0 && existingLogMap.TryGetValue((card.CardId, reviewDt), out var existingLog))
                {
                    if (existingLog.Rating != rating)
                    {
                        existingLog.Rating = rating;
                        updatedLogs++;
                    }

                    continue;
                }

                var pendingKey = (wordId, (int)readingIndex, reviewDt);
                if (!pendingLogKeys.Add(pendingKey))
                    continue;

                logsToAdd.Add(new FsrsReviewLog(card.CardId, rating, reviewDt) { Card = card });
            }

            cardsToReplay.Add(card);
        }

        var allJpdbCardKeys = cardsToReplay.Select(c => (c.WordId, c.ReadingIndex))
            .Concat(existingCards.Values.Select(c => (c.WordId, c.ReadingIndex)))
            .ToHashSet();

        var pendingByCard = logsToAdd.GroupBy(l => l.Card).ToDictionary(g => g.Key, g => g.ToList());

        var coveringByCard = await WordFormHelper.ArchiveRedundantImportCards(
            userContext, wordFormCache, userId, cardsToAdd, allJpdbCardKeys,
            card => pendingByCard.TryGetValue(card, out var logs)
                ? logs.Select(l => new PackedReview(l.Rating, l.ReviewDateTime, l.ReviewDuration)).ToList()
                : [],
            scheduler);

        var archivedRedundant = coveringByCard.Archived;

        if (coveringByCard.Count > 0)
        {
            cardsToAdd.RemoveAll(coveringByCard.Contains);
            cardsToReplay.RemoveAll(coveringByCard.Contains);
            logsToAdd.RemoveAll(l => coveringByCard.Contains(l.Card));
            skipped += coveringByCard.Count;
        }

        var restoredCards = await CardRestoreService.AutoRestoreAsync(
            userContext, userId, cardsToAdd, restoreSchedule: false,
            pendingReviewTimes: card => pendingByCard.TryGetValue(card, out var logs)
                ? logs.Select(l => l.ReviewDateTime)
                : []);

        if (cardsToAdd.Count > 0)
        {
            await userContext.FsrsCards.AddRangeAsync(cardsToAdd);
            await userContext.SaveChangesAsync();
        }

        foreach (var log in logsToAdd)
        {
            log.CardId = log.Card.CardId;
        }

        if (logsToAdd.Count > 0)
        {
            await userContext.FsrsReviewLogs.AddRangeAsync(logsToAdd);
            await userContext.SaveChangesAsync();
        }

        // Flush rating updates to existing logs so the AsNoTracking replay below reads them (no-op if already saved above)
        if (updatedLogs > 0)
            await userContext.SaveChangesAsync();

        // Replay all reviews for affected cards to compute state
        var replayCardIds = cardsToReplay.Where(c => c.CardId > 0).Select(c => c.CardId).ToList();
        var allLogs = await userContext.FsrsReviewLogs
                                       .AsNoTracking()
                                       .Where(l => replayCardIds.Contains(l.CardId))
                                       .OrderBy(l => l.ReviewDateTime)
                                       .ThenBy(l => l.ReviewLogId)
                                       .ToListAsync();
        var logsByCard = allLogs.GroupBy(l => l.CardId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var card in cardsToReplay)
        {
            if (!logsByCard.TryGetValue(card.CardId, out var cardLogs) || cardLogs.Count == 0)
                continue;

            FsrsReplay.Recompute(card, cardLogs, scheduler, scheduler,
                                 preserveTerminalState: !request.OverwriteCardStates);
        }

        await userContext.SaveChangesAsync();

        var droppedFromArchive = await CardArchiveService.DropReviewsHeldLiveAsync(
            userContext, userId, cardsToReplay,
            card => logsByCard.TryGetValue(card.CardId, out var live) ? live.Select(l => l.ReviewDateTime) : []);
        if (droppedFromArchive > 0)
            await userContext.SaveChangesAsync();

        var pruned = await WordFormHelper.PruneRedundantForms(userContext, wordFormCache, userId, distinctWordIds);
        if (pruned > 0)
            await userContext.SaveChangesAsync();

        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
        await userContext.SaveChangesAsync();
        backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserCoverage(userId));

        await MarkReviewRollupDirty(userId);

        logger.LogInformation(
                              "User imported JPDB reviews: UserId={UserId}, Cards={Cards}, Reviews={Reviews}, Updated={Updated}, Skipped={Skipped}, Archived={Archived}, Restored={Restored}, Pruned={Pruned}",
                              userId, cardsToReplay.Count, logsToAdd.Count, updatedLogs, skipped, archivedRedundant, restoredCards, pruned);
        return Results.Ok(new
                          {
                              cardsProcessed = cardsToReplay.Count, reviewsImported = logsToAdd.Count,
                              reviewsUpdated = updatedLogs, skipped, archivedRedundant, restoredCards, pruned
                          });
    }

    /// <summary>Anki's incoming reviews for a card, minus the ratings the import would refuse anyway.</summary>
    private static IReadOnlyList<PackedReview> AnkiHistoryOf(
        Dictionary<(int WordId, byte ReadingIndex), (FsrsCard Card, List<AnkiReviewLogImport> AllReviewLogs)> processedPairs,
        FsrsCard card)
        => processedPairs.TryGetValue((card.WordId, card.ReadingIndex), out var processed)
            ? processed.AllReviewLogs
                       .Where(l => l.Rating is >= FsrsRating.Again and <= FsrsRating.Easy)
                       .Select(l => new PackedReview(l.Rating, l.ReviewDateTime, l.ReviewDuration))
                       .ToList()
            : [];

    private static FsrsRating? MapJpdbGrade(string grade) => grade switch
    {
        "nothing" or "unknown" or "fail" or "something" => FsrsRating.Again,
        "hard" => FsrsRating.Hard,
        "okay" or "pass" => FsrsRating.Good,
        "easy" or "known" => FsrsRating.Easy,
        _ => null
    };

    /// <summary>Second precision is the resolution shared by Anki exports and Jiten's own logs.</summary>
    private static DateTime TruncateToSecond(DateTime value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);

    private static int CountLapsesFromLogs(List<FsrsReviewLogExportDto> logs)
        => CountLapsesFromLogs(logs.Select(l => (l.Rating, DateTimeOffset.FromUnixTimeSeconds(l.ReviewDateTime).UtcDateTime, l.ReviewDuration)));

    private static int CountLapsesFromLogs(IEnumerable<(FsrsRating Rating, DateTime ReviewDateTime, int? ReviewDuration)> logs)
        => FsrsReplay.CountLapses(logs.Select(l => new FsrsReviewLog
                                                   {
                                                       Rating = l.Rating, ReviewDateTime = l.ReviewDateTime,
                                                       ReviewDuration = l.ReviewDuration
                                                   })
                                      .ToList(),
                                  new FsrsScheduler(enableFuzzing: false));

    private static byte ResolveReadingIndex(List<JmDictWordForm> forms, string spelling)
    {
        var match = forms.FirstOrDefault(f => f.Text == spelling);
        if (match != null)
            return (byte)match.ReadingIndex;

        return 0;
    }

    [HttpGet("vocabulary/redundant-forms")]
    [SwaggerOperation(Summary = "List redundant vocabulary forms",
                      Description = "Returns the caller's cards that a Mastered sibling form already covers")]
    public async Task<IResult> GetRedundantForms()
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var found = await ComputeRedundantForms(userId);
        if (found.Count == 0)
            return Results.Ok(new { items = Array.Empty<RedundantFormDto>(), totalItems = 0 });

        var wordIds = found.Select(f => f.Card.WordId).Distinct().ToList();
        var presentation = await WordFormHelper.LoadWordPresentation(jitenContext, wordIds);

        var items = found
                    .Select(f => new RedundantFormDto
                    {
                        WordId = f.Card.WordId,
                        ReadingIndex = f.Card.ReadingIndex,
                        Reading = presentation.FormText(f.Card.WordId, f.Card.ReadingIndex),
                        State = f.Card.State,
                        ReviewCount = f.ReviewCount,
                        CoveringReadingIndex = f.CoveringReadingIndex,
                        CoveringReading = presentation.FormText(f.Card.WordId, f.CoveringReadingIndex),
                        CoveringState = FsrsState.Mastered,
                        MainDefinition = presentation.Definition(f.Card.WordId),
                        FrequencyRank = presentation.FrequencyRank(f.Card.WordId, f.Card.ReadingIndex)
                    })
                    .OrderBy(i => i.FrequencyRank == 0 ? int.MaxValue : i.FrequencyRank)
                    .ThenBy(i => i.WordId)
                    .ToList();

        return Results.Ok(new { items, totalItems = items.Count });
    }

    [HttpPost("vocabulary/redundant-forms/resolve")]
    [SwaggerOperation(Summary = "Remove redundant vocabulary forms",
                      Description = "Deletes the listed cards, but only those still confirmed redundant server-side. Their review history is archived and can be restored from Recently Removed.")]
    public async Task<IResult> ResolveRedundantForms([FromBody] ResolveRedundantFormsRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        if (request.Forms == null || request.Forms.Count == 0)
            return Results.BadRequest("No forms provided");

        var requested = request.Forms.Select(f => (f.WordId, f.ReadingIndex)).ToHashSet();

        var found = await ComputeRedundantForms(userId);
        var confirmed = found.Where(f => requested.Contains((f.Card.WordId, f.Card.ReadingIndex))).ToList();
        var toRemove = confirmed.Select(f => f.Card).ToList();

        if (toRemove.Count == 0)
            return Results.Ok(new { removed = 0, skipped = requested.Count });

        var coveringByCardId = confirmed.ToDictionary(f => f.Card.CardId, f => f.CoveringReadingIndex);
        await CardArchiveService.ArchiveCardsAsync(userContext, userId, toRemove, CardArchiveReason.RedundancyResolve,
                                                   c => coveringByCardId.TryGetValue(c.CardId, out var ri) ? ri : null);
        userContext.FsrsCards.RemoveRange(toRemove);
        await userContext.SaveChangesAsync();

        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
        await userContext.SaveChangesAsync();
        backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserCoverage(userId));

        await sessionService.BumpStudyOverviewVersion(userId);

        logger.LogInformation("User removed redundant vocabulary forms: UserId={UserId}, Removed={Removed}, Skipped={Skipped}",
                              userId, toRemove.Count, requested.Count - toRemove.Count);
        return Results.Ok(new { removed = toRemove.Count, skipped = requested.Count - toRemove.Count });
    }

    /// <summary>
    /// Restores the archive section of a backup
    /// </summary>
    private async Task<int> ImportArchiveSection(string userId, List<FsrsCardArchiveExportDto>? section)
    {
        if (section is not { Count: > 0 })
            return 0;

        var wordIds = section.Select(a => a.WordId).Distinct().ToList();
        var readingCounts = await jitenContext.WordForms
                                              .AsNoTracking()
                                              .Where(wf => wordIds.Contains(wf.WordId))
                                              .GroupBy(wf => wf.WordId)
                                              .Select(g => new { WordId = g.Key, ReadingCount = g.Count() })
                                              .ToDictionaryAsync(w => w.WordId, w => w.ReadingCount);

        var existing = (await userContext.FsrsCardArchives
                                         .Where(a => a.UserId == userId && wordIds.Contains(a.WordId))
                                         .ToListAsync())
                       .ToDictionary(a => (a.WordId, a.ReadingIndex));

        var imported = 0;

        foreach (var dto in section)
        {
            if (!readingCounts.TryGetValue(dto.WordId, out var readingCount) || dto.ReadingIndex >= readingCount)
                continue;

            var reviews = dto.ReviewLogs
                             .Select(l => new PackedReview(l.Rating,
                                                           DateTimeOffset.FromUnixTimeSeconds(l.ReviewDateTime).UtcDateTime,
                                                           l.ReviewDuration));
            var packed = ReviewLogPacker.Pack(reviews);

            var incoming = new FsrsCardArchive
                           {
                               UserId = userId,
                               WordId = dto.WordId,
                               ReadingIndex = dto.ReadingIndex,
                               ArchivedAt = DateTimeOffset.FromUnixTimeSeconds(dto.ArchivedAt).UtcDateTime,
                               Reason = dto.Reason,
                               CoveringReadingIndex = dto.CoveringReadingIndex,
                               State = dto.State,
                               Step = dto.Step,
                               Stability = dto.Stability,
                               Difficulty = dto.Difficulty,
                               Due = DateTimeOffset.FromUnixTimeSeconds(dto.Due).UtcDateTime,
                               LastReview = dto.LastReview.HasValue
                                   ? DateTimeOffset.FromUnixTimeSeconds(dto.LastReview.Value).UtcDateTime
                                   : null,
                               Lapses = dto.Lapses,
                               CardCreatedAt = DateTimeOffset.FromUnixTimeSeconds(dto.CardCreatedAt).UtcDateTime,
                               ReviewCount = Math.Max(dto.ReviewCount, packed.ReviewCount),
                               HistoryMerged = dto.HistoryMerged,
                               FirstReview = packed.FirstReview,
                               HistoryTruncated = packed.Truncated,
                               Logs = packed.Logs
                           };

            if (existing.TryGetValue((dto.WordId, dto.ReadingIndex), out var row))
            {
                CardArchiveService.MergeArchiveRows(row, incoming);
            }
            else
            {
                userContext.FsrsCardArchives.Add(incoming);
                existing[(dto.WordId, dto.ReadingIndex)] = incoming;
            }

            imported++;
        }

        if (imported > 0)
            await userContext.SaveChangesAsync();

        return imported;
    }

    /// <summary>
    /// Restores the per-day activity counters from a backup
    /// </summary>
    private async Task<bool> ImportReviewActivitySection(string userId, List<UserReviewDailyExportDto>? section)
    {
        if (section is not { Count: > 0 })
            return false;

        var incoming = new Dictionary<DateOnly, UserReviewDailyExportDto>();
        foreach (var dto in section)
            if (DateOnly.TryParse(dto.LocalDate, out var date))
                incoming[date] = dto;

        if (incoming.Count == 0)
            return false;

        var dates = incoming.Keys.ToList();
        var existing = await userContext.UserReviewDailies
                                        .Where(d => d.UserId == userId && dates.Contains(d.LocalDate))
                                        .ToDictionaryAsync(d => d.LocalDate);

        foreach (var (date, dto) in incoming)
        {
            if (!existing.TryGetValue(date, out var row))
            {
                row = new UserReviewDaily { UserId = userId, LocalDate = date };
                userContext.UserReviewDailies.Add(row);
            }

            row.ReviewCount = Math.Max(0, dto.ReviewCount);
            row.CorrectCount = Math.Max(0, dto.CorrectCount);
            row.NewCardCount = Math.Max(0, dto.NewCardCount);
            row.TotalDurationMs = Math.Max(0, dto.TotalDurationMs);
        }

        await ReviewRollupHelper.MarkRebuilt(userContext, userId);

        await userContext.SaveChangesAsync();
        return true;
    }

    #region Card archive

    [HttpGet("vocabulary/archive")]
    [SwaggerOperation(Summary = "List removed cards",
                      Description = "Cards removed from the collection, newest first, with the review history kept for each.")]
    public async Task<IResult> GetArchivedCards(
        [FromQuery] CardArchiveReason? reason = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 200);

        var query = userContext.FsrsCardArchives.AsNoTracking().Where(a => a.UserId == userId);
        if (reason.HasValue)
            query = query.Where(a => a.Reason == reason.Value);

        var totalItems = await query.CountAsync();
        var rows = await query
                         .OrderByDescending(a => a.ArchivedAt)
                         .ThenBy(a => a.WordId)
                         .Skip(offset)
                         .Take(limit)
                         .Select(a => new
                                      {
                                          a.WordId, a.ReadingIndex, a.ArchivedAt, a.Reason, a.CoveringReadingIndex,
                                          a.State, a.ReviewCount, a.FirstReview, a.LastReview, a.Lapses, a.HistoryTruncated
                                      })
                         .ToListAsync();

        if (rows.Count == 0)
            return Results.Ok(new PaginatedResponse<List<ArchivedCardDto>>([], totalItems, limit, offset));

        var wordIds = rows.Select(r => r.WordId).Distinct().ToList();
        var presentation = await WordFormHelper.LoadWordPresentation(jitenContext, wordIds);

        var items = rows.Select(r => new ArchivedCardDto
                                     {
                                         WordId = r.WordId,
                                         ReadingIndex = r.ReadingIndex,
                                         Reading = presentation.FormText(r.WordId, r.ReadingIndex),
                                         MainDefinition = presentation.Definition(r.WordId),
                                         FrequencyRank = presentation.FrequencyRank(r.WordId, r.ReadingIndex),
                                         ArchivedAt = r.ArchivedAt,
                                         Reason = r.Reason,
                                         CoveringReadingIndex = r.CoveringReadingIndex,
                                         CoveringReading = r.CoveringReadingIndex.HasValue
                                             ? presentation.FormText(r.WordId, r.CoveringReadingIndex.Value)
                                             : null,
                                         State = r.State,
                                         ReviewCount = r.ReviewCount,
                                         FirstReview = r.FirstReview,
                                         LastReview = r.LastReview,
                                         Lapses = r.Lapses,
                                         HistoryTruncated = r.HistoryTruncated,
                                         AutoRestores = r.Reason.IsAutoRestorable()
                                     }).ToList();

        return Results.Ok(new PaginatedResponse<List<ArchivedCardDto>>(items, totalItems, limit, offset));
    }

    [HttpGet("vocabulary/archive/{wordId:int}/{readingIndex}")]
    [SwaggerOperation(Summary = "Look up one removed form",
                      Description = "Returns the archived entry for a single form, or null when nothing was removed for it")]
    public async Task<IResult> GetArchivedCard(int wordId, byte readingIndex)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var row = await userContext.FsrsCardArchives
                                   .AsNoTracking()
                                   .Where(a => a.UserId == userId && a.WordId == wordId && a.ReadingIndex == readingIndex)
                                   .Select(a => new
                                                {
                                                    a.WordId, a.ReadingIndex, a.ArchivedAt, a.Reason,
                                                    a.ReviewCount, a.State, a.HistoryTruncated
                                                })
                                   .FirstOrDefaultAsync();

        if (row == null)
            return Results.Ok(new { found = false });

        return Results.Ok(new
                          {
                              found = true,
                              row.WordId,
                              row.ReadingIndex,
                              row.ArchivedAt,
                              row.Reason,
                              row.ReviewCount,
                              row.State,
                              historyTruncated = row.HistoryTruncated,
                              autoRestores = row.Reason.IsAutoRestorable()
                          });
    }

    [HttpPost("vocabulary/archive/restore")]
    [SwaggerOperation(Summary = "Restore removed cards",
                      Description = "Puts archived cards and their review history back")]
    public async Task<IResult> RestoreArchivedCards([FromBody] RestoreArchivedCardsRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        List<(int WordId, byte ReadingIndex)> keys;
        var remaining = 0;

        if (request.All)
        {
            var query = userContext.FsrsCardArchives.AsNoTracking().Where(a => a.UserId == userId);
            if (request.Reason.HasValue)
                query = query.Where(a => a.Reason == request.Reason.Value);

            var total = await query.CountAsync();
            keys = (await query.OrderBy(a => a.ArchiveId)
                               .Take(MaxBulkRestore)
                               .Select(a => new { a.WordId, a.ReadingIndex })
                               .ToListAsync())
                   .Select(a => (a.WordId, a.ReadingIndex))
                   .ToList();

            remaining = Math.Max(0, total - keys.Count);
        }
        else
        {
            if (request.Forms == null || request.Forms.Count == 0)
                return Results.BadRequest("No forms provided");
            if (request.Forms.Count > MaxBulkRestore)
                return Results.BadRequest($"Too many forms in one request (max {MaxBulkRestore}).");

            keys = request.Forms.Select(f => (f.WordId, f.ReadingIndex)).ToList();
        }

        if (keys.Count == 0)
            return Results.Ok(new { restored = 0, skipped = 0, remaining = 0, results = Array.Empty<CardRestoreOutcome>() });

        var settings = await FsrsSettingsHelper.LoadAsync(userContext, userId);

        var outcomes = await CardRestoreService.RestoreAsync(
            userContext, jitenContext, userId, keys,
            FsrsSettingsHelper.GetParameters(settings), FsrsSettingsHelper.GetDesiredRetention(settings));

        var restored = outcomes.Count(o => o.Restored);
        if (restored > 0)
        {
            await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
            await userContext.SaveChangesAsync();
            backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserCoverage(userId));
            await MarkReviewRollupDirty(userId);
            await sessionService.BumpStudyOverviewVersion(userId);
        }

        logger.LogInformation("User restored archived cards: UserId={UserId}, Restored={Restored}, Requested={Requested}",
                              userId, restored, keys.Count);

        var restoredStates = restored > 0
            ? await userService.GetKnownWordsState(outcomes.Where(o => o.Restored).Select(o => (o.WordId, o.ReadingIndex)))
            : new Dictionary<(int WordId, byte ReadingIndex), List<KnownState>>();

        return Results.Ok(new
                          {
                              restored,
                              skipped = outcomes.Count - restored,
                              remaining,
                              results = outcomes.Select(o => new
                                                             {
                                                                 o.WordId,
                                                                 o.ReadingIndex,
                                                                 o.Restored,
                                                                 o.ReviewsRestored,
                                                                 o.HistoryTruncated,
                                                                 o.Error,
                                                                 knownStates = restoredStates.GetValueOrDefault((o.WordId, o.ReadingIndex))
                                                             })
                          });
    }

    [HttpDelete("vocabulary/archive")]
    [SwaggerOperation(Summary = "Permanently forget removed cards",
                      Description = "Drops archived entries for good")]
    public async Task<IResult> ForgetArchivedCards([FromBody] ForgetArchivedCardsRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var query = userContext.FsrsCardArchives.Where(a => a.UserId == userId);

        if (request.Reason.HasValue)
            query = query.Where(a => a.Reason == request.Reason.Value);

        int removed;

        if (request.Forms is { Count: > 0 })
        {
            var keys = request.Forms.Select(f => (f.WordId, f.ReadingIndex)).ToHashSet();
            var wordIds = keys.Select(k => k.WordId).Distinct().ToList();
            var ids = (await query.Where(a => wordIds.Contains(a.WordId))
                                  .Select(a => new { a.ArchiveId, a.WordId, a.ReadingIndex })
                                  .ToListAsync())
                      .Where(a => keys.Contains((a.WordId, a.ReadingIndex)))
                      .Select(a => a.ArchiveId)
                      .ToList();

            removed = ids.Count == 0
                ? 0
                : await userContext.FsrsCardArchives.Where(a => ids.Contains(a.ArchiveId)).ExecuteDeleteAsync();
        }
        else
        {
            removed = await query.ExecuteDeleteAsync();
        }

        // Their reviews leave the rebuildable corpus with them, so the day counters no longer match.
        if (removed > 0)
            await MarkReviewRollupDirty(userId);

        logger.LogInformation("User forgot archived cards: UserId={UserId}, Removed={Removed}, Reason={Reason}",
                              userId, removed, request.Reason);

        return Results.Ok(new { removed });
    }

    [HttpDelete("vocabulary/review-history")]
    [SwaggerOperation(Summary = "Erase review history",
                      Description = "Deletes review logs in the given range and clears the history kept on archived entries, then reschedules the cards that keep part of their history and removes the ones left with none. Card removal never does this; only this action does.")]
    public async Task<IResult> PurgeReviewHistory([FromQuery] DateTime? since = null,
                                                  [FromQuery] DateTime? until = null,
                                                  [FromQuery] bool dropEmptyArchives = false)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var from = since?.ToUniversalTime() ?? DateTime.UnixEpoch;
        var to = until?.ToUniversalTime() ?? DateTime.UtcNow.AddYears(1);
        if (from > to)
            return Results.BadRequest("The start of the range is after its end.");

        var userCardIds = userContext.FsrsCards.Where(c => c.UserId == userId).Select(c => c.CardId);

        var emptiedCardIds = await userContext.FsrsCards
                                              .Where(c => c.UserId == userId
                                                          && (c.State == FsrsState.Learning
                                                              || c.State == FsrsState.Review
                                                              || c.State == FsrsState.Relearning)
                                                          && userContext.FsrsReviewLogs.Any(l => l.CardId == c.CardId)
                                                          && !userContext.FsrsReviewLogs
                                                                         .Any(l => l.CardId == c.CardId
                                                                                   && (l.ReviewDateTime < from || l.ReviewDateTime > to)))
                                              .Select(c => c.CardId)
                                              .ToListAsync();

        var deletedLogs = await userContext.FsrsReviewLogs
                                           .Where(l => userCardIds.Contains(l.CardId)
                                                       && l.ReviewDateTime >= from
                                                       && l.ReviewDateTime <= to)
                                           .ExecuteDeleteAsync();

        var deletedCards = 0;
        foreach (var chunk in emptiedCardIds.Chunk(PurgeCardChunkSize))
            deletedCards += await userContext.FsrsCards.Where(c => chunk.Contains(c.CardId)).ExecuteDeleteAsync();

        var archiveRows = await userContext.FsrsCardArchives
                                           .Where(a => a.UserId == userId && a.ReviewCount > 0)
                                           .ToListAsync();

        var clearedArchives = 0;
        foreach (var row in archiveRows)
        {
            var (reviews, corrupt) = CardArchiveService.ReadReviews(row);
            if (corrupt)
                continue;

            var kept = reviews.Where(r => r.ReviewDateTime < from || r.ReviewDateTime > to).ToList();
            if (kept.Count == reviews.Count)
                continue;

            var packed = ReviewLogPacker.Pack(kept, markTruncated: row.HistoryTruncated);
            row.Logs = packed.Logs;
            row.FirstReview = packed.FirstReview;
            row.HistoryTruncated = packed.Truncated;
            row.ReviewCount = packed.ReviewCount;
            clearedArchives++;
        }

        if (clearedArchives > 0)
            await userContext.SaveChangesAsync();

        var droppedArchives = 0;
        if (dropEmptyArchives)
            droppedArchives = await userContext.FsrsCardArchives
                                               .Where(a => a.UserId == userId && a.ReviewCount == 0)
                                               .ExecuteDeleteAsync();

        if (deletedCards > 0)
        {
            await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
            await userContext.SaveChangesAsync();
            backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserCoverage(userId));
        }

        await MarkReviewRollupDirty(userId);

        var settings = await FsrsSettingsHelper.LoadAsync(userContext, userId);
        var studySettings = FsrsSettingsHelper.GetStudySettings(settings);
        backgroundJobs.Enqueue<SrsRecomputeJob>(job => job.RecomputeUserSrs(
                                                    userId,
                                                    FsrsSettingsHelper.GetParameters(settings),
                                                    FsrsSettingsHelper.GetDesiredRetention(settings),
                                                    studySettings.LoadBalancing,
                                                    null));

        await sessionService.BumpStudyOverviewVersion(userId);

        logger.LogInformation(
            "User purged review history: UserId={UserId}, DeletedLogs={DeletedLogs}, DeletedCards={DeletedCards}, ClearedArchives={ClearedArchives}, DroppedArchives={DroppedArchives}",
            userId, deletedLogs, deletedCards, clearedArchives, droppedArchives);

        return Results.Ok(new { deletedLogs, deletedCards, clearedArchives, droppedArchives });
    }

    [HttpPost("vocabulary/review-activity/rebuild")]
    [SwaggerOperation(Summary = "Rebuild activity history",
                      Description = "Discards the stored per-day activity counters and recomputes them from review logs and archived history")]
    public async Task<IResult> RebuildReviewActivity()
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var cleared = await userContext.UserReviewDailies.Where(d => d.UserId == userId).ExecuteDeleteAsync();
        await MarkReviewRollupDirty(userId);

        logger.LogInformation("User queued an activity history rebuild: UserId={UserId}, ClearedDays={ClearedDays}", userId, cleared);

        return Results.Ok(new { clearedDays = cleared, queued = true });
    }

    #endregion

    private Task MarkReviewRollupDirty(string userId)
        => ReviewRollupHelper.MarkDirtyAndQueue(userContext, backgroundJobs, userId);

    private async Task<List<(FsrsCard Card, byte CoveringReadingIndex, int ReviewCount)>> ComputeRedundantForms(string userId)
    {
        var candidateWordIds = await userContext.FsrsCards
                                                .Where(c => c.UserId == userId)
                                                .GroupBy(c => c.WordId)
                                                .Where(g => g.Count() > 1 && g.Any(c => c.State == FsrsState.Mastered))
                                                .Select(g => g.Key)
                                                .ToListAsync();

        if (candidateWordIds.Count == 0)
            return [];

        var cards = await userContext.FsrsCards
                                     .Where(c => c.UserId == userId && candidateWordIds.Contains(c.WordId))
                                     .ToListAsync();

        var reviewCounts = await WordFormHelper.LoadReviewCounts(userContext, cards.Select(c => c.CardId).ToList());

        var result = new List<(FsrsCard, byte, int)>();
        foreach (var group in cards.GroupBy(c => c.WordId))
        {
            var byIndex = group.ToDictionary(c => c.ReadingIndex);
            foreach (var pair in WordFormHelper.FindRedundantForms(wordFormCache, group.Key, group.ToList(), reviewCounts))
            {
                if (byIndex.TryGetValue(pair.ReadingIndex, out var card))
                    result.Add((card, pair.CoveringReadingIndex, reviewCounts.GetValueOrDefault(card.CardId)));
            }
        }

        return result;
    }

    /// <summary>
    /// Parse an Anki-exported TXT file and add all parsed words as known for the current user.
    /// </summary>
    [HttpPost("vocabulary/import-from-anki-txt")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxAnkiTxtBytes)]
    public async Task<IResult> AddKnownFromAnkiTxt(IFormFile? file, [FromQuery] bool parseWords = false,
                                                   [FromQuery] bool overwriteExisting = false)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        if (file == null) return Results.BadRequest("No file provided.");
        if (file.Length == 0) return Results.BadRequest("File is empty.");
        if (file.Length > MaxAnkiTxtBytes) return Results.BadRequest("File exceeds 50 MB limit.");

        using var reader = new StreamReader(file.OpenReadStream());
        var lineCount = 0;
        var validWords = new List<string>();

        while (await reader.ReadLineAsync() is { } line)
        {
            lineCount++;
            if (lineCount > MaxAnkiTxtLines)
                return Results.BadRequest($"File has more than {MaxAnkiTxtLines:N0} lines.");
            if (line.StartsWith("#"))
                continue;

            var tabIndex = line.IndexOf('\t');
            if (tabIndex <= 0)
                tabIndex = line.IndexOf(',');
            if (tabIndex <= 0)
                tabIndex = line.Length;

            var word = line.Substring(0, tabIndex);
            if (word.Length <= 25)
                validWords.Add(word);
        }

        if (validWords.Count == 0)
            return Results.BadRequest("No valid words found in file");

        var combinedText = string.Join(Environment.NewLine, validWords);
        var parsedWords = parseWords
            ? await Parser.Parser.ParseText(contextFactory, combinedText)
            : await Parser.Parser.GetWordsDirectLookup(contextFactory, validWords);
        var result = await userService.AddKnownWords(parsedWords, overwriteExisting);

        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
        await userContext.SaveChangesAsync();
        backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserCoverage(userId));

        logger.LogInformation("User imported words from Anki TXT: UserId={UserId}, ParsedCount={ParsedCount}, AddedCount={AddedCount}, UpdatedCount={UpdatedCount}",
                              userId, parsedWords.Count, result.Inserted, result.Updated);
        return Results.Ok(new { parsed = parsedWords.Count, added = result.Inserted, updated = result.Updated });
    }

    /// <summary>
    /// Parse a JSON coming from AnkiConnect
    /// </summary>
    [HttpPost("vocabulary/import-from-anki")]
    [Consumes("text/json", "application/json")]
    public async Task<IResult> ImportFromAnki([FromBody] AnkiImportRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        if (request?.Cards == null || request.Cards.Count == 0)
            return Results.BadRequest("No cards provided.");

        // Step 1: Parse all unique words to get WordId + ReadingIndex
        var uniqueWords = request.Cards
                                 .Select(c => c.Card.Word)
                                 .Where(w => !string.IsNullOrWhiteSpace(w))
                                 .Distinct()
                                 .ToList();

        if (uniqueWords.Count == 0)
            return Results.BadRequest("No valid words found.");

        // Track skipped words (no reviews)
        List<string> skippedWordsNoReviews = new List<string>();
        int skippedCountNoReviews = 0;

        for (int i = request.Cards.Count - 1; i >= 0; i--)
        {
            if (request.Cards[i].Card.LastReview != null)
                continue;

            skippedWordsNoReviews.Add(new string(request.Cards[i].Card.Word));
            request.Cards.RemoveAt(i);
            skippedCountNoReviews++;
        }

        // Resolve to (WordId, ReadingIndex), keyed by (surface, reading). The optional reading lets
        // us disambiguate cards that share a surface but have different readings — without it they
        // would collapse to a single resolution. ParseWords (conjugated) path stays surface-only.
        var wordLookup = new Dictionary<(string Word, string Reading), (int WordId, byte ReadingIndex)>();
        if (request.ParseWords)
        {
            var combinedText = string.Join(Environment.NewLine, uniqueWords);
            var parsedWords = await Parser.Parser.ParseText(contextFactory, combinedText);
            foreach (var parsed in parsedWords)
                wordLookup.TryAdd((parsed.OriginalText, ""), (parsed.WordId, parsed.ReadingIndex));
        }
        else
        {
            var uniquePairs = request.Cards
                                     .Where(c => !string.IsNullOrWhiteSpace(c.Card.Word))
                                     .Select(c => (Word: c.Card.Word.Trim(), Reading: c.Card.Reading?.Trim() ?? ""))
                                     .Distinct()
                                     .ToList();

            var resolved = await Parser.Parser.GetWordsDirectLookupByReading(contextFactory, uniquePairs);
            foreach (var kv in resolved)
                wordLookup[kv.Key] = (kv.Value.WordId, kv.Value.ReadingIndex);
        }

        // Step 2: Get existing cards
        var parsedWordIds = wordLookup.Values.Select(v => v.WordId).Distinct().ToList();
        var existingCards = await userContext.FsrsCards
                                             .Include(c => c.ReviewLogs)
                                             .Where(c => c.UserId == userId && parsedWordIds.Contains(c.WordId))
                                             .ToListAsync();

        var existingCardsMap = existingCards
            .ToDictionary(c => (c.WordId, c.ReadingIndex));

        // Step 3: Process cards - separate new cards vs updates
        var cardsToAdd = new List<FsrsCard>();
        var cardsToUpdate = new List<FsrsCard>();
        var cardToAnkiMap = new Dictionary<FsrsCard, AnkiCardWrapper>();

        var processedPairs = new Dictionary<(int WordId, byte ReadingIndex), (FsrsCard Card, List<AnkiReviewLogImport> AllReviewLogs)>();

        // Track skipped words (not in dictionary)
        int skippedCount = 0;
        var skippedWords = new List<string>();

        foreach (var wrapper in request.Cards)
        {
            var word = wrapper.Card.Word?.Trim();
            if (string.IsNullOrWhiteSpace(word))
            {
                skippedCount++;
                continue;
            }

            // Lookup by (surface, reading); fall back to the surface-only resolution (e.g. the
            // ParseWords path, or when the reading didn't resolve) before giving up.
            var reading = wrapper.Card.Reading?.Trim() ?? "";
            if (!wordLookup.TryGetValue((word, reading), out var wordInfo)
                && (reading.Length == 0 || !wordLookup.TryGetValue((word, ""), out wordInfo)))
            {
                skippedWords.Add(word);
                skippedCount++;
                continue;
            }

            var key = (wordInfo.WordId, wordInfo.ReadingIndex);

            // Check for duplicates
            if (processedPairs.TryGetValue(key, out var existing))
            {
                logger.LogWarning("Duplicate word detected in import request: {Word} (WordId={WordId}, ReadingIndex={ReadingIndex})",
                                  word, wordInfo.WordId, wordInfo.ReadingIndex);
                skippedCount++;
                continue;
            }

            // Validate and clamp values
            var stability = Math.Max(0, wrapper.Card.Stability ?? 0);
            var difficulty = Math.Clamp(wrapper.Card.Difficulty ?? 5.0, 1.0, 10.0);
            var state = wrapper.Card.State;

            if (state == FsrsState.New)
            {
                skippedCount++;
                continue;
            }

            // Check if card already exists
            if (existingCardsMap.TryGetValue(key, out var existingCard))
            {
                if (!request.Overwrite)
                {
                    skippedCount++;
                    continue;
                }

                // Scheduling belongs to whichever side was studied most recently, so someone who kept
                // reviewing in Jiten does not get pulled back to a stale Anki schedule.
                if (existingCard.LastReview == null || wrapper.Card.LastReview > existingCard.LastReview)
                {
                    existingCard.State = state;
                    existingCard.Stability = stability;
                    existingCard.Difficulty = difficulty;
                    existingCard.Due = wrapper.Card.Due;
                    existingCard.LastReview = wrapper.Card.LastReview;
                    existingCard.Step = state == FsrsState.Learning ? (byte?)0 : null;
                }

                cardsToUpdate.Add(existingCard);
                cardToAnkiMap[existingCard] = wrapper;
                processedPairs[key] = (existingCard, new List<AnkiReviewLogImport>(wrapper.ReviewLogs));
            }
            else
            {
                // Create new card
                var fsrsCard = new FsrsCard(
                                            userId,
                                            wordInfo.WordId,
                                            wordInfo.ReadingIndex,
                                            state: state,
                                            due: wrapper.Card.Due,
                                            lastReview: wrapper.Card.LastReview
                                           )
                               {
                                   Stability = stability, Difficulty = difficulty, Step = state == FsrsState.Learning ? (byte?)0 : null,
                               };

                cardsToAdd.Add(fsrsCard);
                cardToAnkiMap[fsrsCard] = wrapper;
                processedPairs[key] = (fsrsCard, new List<AnkiReviewLogImport>(wrapper.ReviewLogs));
            }
        }

        var allAnkiCardKeys = processedPairs.Keys
            .Concat(existingCardsMap.Keys.Select(k => (k.WordId, k.ReadingIndex)))
            .ToHashSet();

        var redundantAnkiCards = await WordFormHelper.ArchiveRedundantImportCards(
            userContext, wordFormCache, userId, cardsToAdd.Concat(cardsToUpdate).ToList(), allAnkiCardKeys,
            card => card.CardId == 0 ? AnkiHistoryOf(processedPairs, card) : []);
        var archivedRedundant = redundantAnkiCards.Archived;

        if (redundantAnkiCards.Count > 0)
        {
            cardsToAdd.RemoveAll(redundantAnkiCards.Contains);
            cardsToUpdate.RemoveAll(redundantAnkiCards.Contains);
            foreach (var card in redundantAnkiCards.Covering.Keys)
                processedPairs.Remove((card.WordId, card.ReadingIndex));
            skippedCount += redundantAnkiCards.Count;
        }

        // Step 4: Bulk insert/update with transaction
        if (cardsToAdd.Count == 0 && cardsToUpdate.Count == 0)
        {
            if (archivedRedundant > 0)
            {
                await userContext.SaveChangesAsync();
                await MarkReviewRollupDirty(userId);
            }

            return Results.Ok(new
                              {
                                  imported = 0, updated = 0, skipped = skippedCount, reviewLogs = 0,
                                  skippedWords = skippedWords.Take(50).ToList(),
                                  skippedWordsNoReviews = skippedWordsNoReviews.Take(50).ToList(), skippedCountNoReviews,
                                  archivedRedundant, restoredCards = 0, pruned = 0
                              });
        }

        await using var transaction = await userContext.Database.BeginTransactionAsync();
        try
        {
            var restoredCards = await CardRestoreService.AutoRestoreAsync(
                userContext, userId, cardsToAdd, restoreSchedule: false,
                pendingReviewTimes: card => processedPairs.TryGetValue((card.WordId, card.ReadingIndex), out var processed)
                    ? processed.AllReviewLogs.Select(l => l.ReviewDateTime)
                    : []);

            // Insert new cards
            if (cardsToAdd.Count > 0)
            {
                await userContext.FsrsCards.AddRangeAsync(cardsToAdd);
                await userContext.SaveChangesAsync();
            }

            // Handle review logs
            var logsToAdd = new List<FsrsReviewLog>();
            var allCards = cardsToAdd.Concat(cardsToUpdate).ToList();
            var updatedCards = cardsToUpdate.ToHashSet();
            var liveTimesByCard = new Dictionary<FsrsCard, HashSet<DateTime>>();

            foreach (var card in allCards)
            {
                var key = (card.WordId, card.ReadingIndex);

                // Get ALL review logs for this card (including from duplicates)
                if (!processedPairs.TryGetValue(key, out var processed))
                    continue;

                // Jiten's own history is kept and Anki's merged into it. A review already stored for the
                // same second is that same review re-imported, so repeating an import adds nothing.
                var knownReviewTimes = card.ReviewLogs.Select(l => TruncateToSecond(l.ReviewDateTime)).ToHashSet();
                liveTimesByCard[card] = knownReviewTimes;
                var mergedLogs = new List<FsrsReviewLog>();

                foreach (var log in processed.AllReviewLogs)
                {
                    // Validate rating
                    if (log.Rating is < FsrsRating.Again or > FsrsRating.Easy)
                        continue;

                    if (!knownReviewTimes.Add(TruncateToSecond(log.ReviewDateTime)))
                        continue;

                    mergedLogs.Add(new FsrsReviewLog
                                   {
                                       CardId = card.CardId, Rating = log.Rating, ReviewDateTime = log.ReviewDateTime,
                                       ReviewDuration = log.ReviewDuration,
                                   });
                }

                logsToAdd.AddRange(mergedLogs);

                if (updatedCards.Contains(card) && mergedLogs.Count > 0)
                {
                    card.Lapses = CountLapsesFromLogs(card.ReviewLogs.Concat(mergedLogs)
                                                          .Select(l => (l.Rating, l.ReviewDateTime, l.ReviewDuration)));
                }
            }

            if (logsToAdd.Count > 0)
            {
                await userContext.FsrsReviewLogs.AddRangeAsync(logsToAdd);
            }

            await userContext.SaveChangesAsync();

            // A form forgotten and re-imported would otherwise keep a copy of these reviews in its archive
            // row, counted a second time by every whole-history statistic. knownReviewTimes ends the merge
            // loop holding every review now live on the card, Jiten's own and the file's.
            var droppedFromArchive = await CardArchiveService.DropReviewsHeldLiveAsync(
                userContext, userId, allCards,
                card => liveTimesByCard.TryGetValue(card, out var times) ? times : []);
            if (droppedFromArchive > 0)
                await userContext.SaveChangesAsync();

            var pruned = await WordFormHelper.PruneRedundantForms(userContext, wordFormCache, userId, parsedWordIds);
            if (pruned > 0)
                await userContext.SaveChangesAsync();

            await transaction.CommitAsync();

            await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
            await userContext.SaveChangesAsync();
            backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserCoverage(userId));

            await MarkReviewRollupDirty(userId);

            logger.LogInformation(
                                  "Anki import completed: UserId={UserId}, Imported={Imported}, Updated={Updated}, Skipped={Skipped}, Logs={Logs}, NotFound={NotFound}, Archived={Archived}, Restored={Restored}, Pruned={Pruned}",
                                  userId, cardsToAdd.Count, cardsToUpdate.Count, skippedCount, logsToAdd.Count, skippedWords.Count,
                                  archivedRedundant, restoredCards, pruned
                                 );

            return Results.Ok(new
                              {
                                  imported = cardsToAdd.Count, updated = cardsToUpdate.Count, skipped = skippedCount,
                                  reviewLogs = logsToAdd.Count, skippedWords = skippedWords,
                                  skippedWordsNoReviews = skippedWordsNoReviews.ToList(), skippedCountNoReviews = skippedCountNoReviews,
                                  archivedRedundant, restoredCards, pruned
                              });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            logger.LogError(ex, "Anki import failed");
            return Results.Problem("Import failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Add a single word for the current user.
    /// </summary>
    /// <returns></returns>
    [HttpPost("vocabulary/add/{wordId}/{readingIndex}")]
    public async Task<IResult> AddKnownWord(int wordId, byte readingIndex)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var upsert = await userService.AddKnownWord(wordId, readingIndex);

        await WordFormHelper.RemoveRedundantKanaSrsCards(userContext, wordFormCache, userId, wordId, readingIndex);
        await userContext.SaveChangesAsync();

        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
        await userContext.SaveChangesAsync();

        if (upsert.Restored > 0)
            await MarkReviewRollupDirty(userId);

        return Results.Ok(new
                          {
                              restored = upsert.Restored,
                              restoredReviews = upsert.Restored > 0 ? await CountRestoredReviews(userId, wordId, readingIndex) : 0
                          });
    }

    /// <summary>Review count of a just-restored card, for the toast that tells the user their history came back.</summary>
    private Task<int> CountRestoredReviews(string userId, int wordId, byte readingIndex)
        => userContext.FsrsReviewLogs
                      .CountAsync(l => l.Card.UserId == userId
                                       && l.Card.WordId == wordId
                                       && l.Card.ReadingIndex == readingIndex);

    /// <summary>
    /// Remove a single word for the current user.
    /// </summary>
    /// <returns></returns>
    [HttpPost("vocabulary/remove/{wordId}/{readingIndex}")]
    public async Task<IResult> RemoveKnownWord(int wordId, byte readingIndex)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        await userService.RemoveKnownWord(wordId, readingIndex);

        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
        await userContext.SaveChangesAsync();

        return Results.Ok();
    }

    [HttpPost("vocabulary/blacklist/{wordId}/{readingIndex}")]
    public async Task<IResult> BlacklistWord(int wordId, byte readingIndex)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        await userService.BlacklistWords([new DeckWord { WordId = wordId, ReadingIndex = readingIndex }]);

        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
        await userContext.SaveChangesAsync();

        return Results.Ok();
    }

    /// <summary>
    /// Add known words for the current user by frequency rank range (inclusive).
    /// Only readings whose per-reading frequency rank falls within the range are imported.
    /// </summary>
    [HttpPost("vocabulary/import-from-frequency/{minFrequency:int}/{maxFrequency:int}")]
    public async Task<IResult> ImportWordsFromFrequency(int minFrequency, int maxFrequency)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        if (minFrequency < 0 || maxFrequency < minFrequency || maxFrequency > 10000)
            return Results.BadRequest("Invalid frequency range");

        var targetReadings = await jitenContext.WordFormFrequencies
            .AsNoTracking()
            .Where(wff => wff.FrequencyRank >= minFrequency && wff.FrequencyRank <= maxFrequency && wff.FrequencyRank > 0)
            .OrderBy(wff => wff.FrequencyRank)
            .Select(wff => new ReadingFrequencyResult(wff.WordId, wff.ReadingIndex, wff.FrequencyRank))
            .ToListAsync();

        if (targetReadings.Count == 0)
            return Results.BadRequest("No readings found for the requested frequency range");

        var targetWordIds = targetReadings.Select(r => r.WordId).Distinct().ToList();

        // Determine which entries are already known (by word + reading index)
        var alreadyKnown = await userContext.FsrsCards
                                            .AsNoTracking()
                                            .Where(uk => uk.UserId == userId && targetWordIds.Contains(uk.WordId))
                                            .ToListAsync();

        var alreadyKnownSet = alreadyKnown.Select(uk => (uk.WordId, (short)uk.ReadingIndex)).ToHashSet();

        var allFreqCardKeys = targetReadings.Select(r => (r.WordId, (byte)r.ReadingIndex))
            .Concat(alreadyKnown.Select(k => (k.WordId, k.ReadingIndex)))
            .ToHashSet();
        List<FsrsCard> toInsert = new();

        foreach (var target in targetReadings)
        {
            if (alreadyKnownSet.Contains((target.WordId, target.ReadingIndex)))
                continue;

            toInsert.Add(new FsrsCard(userId, target.WordId, (byte)target.ReadingIndex,
                                      due: DateTime.UtcNow, lastReview: DateTime.UtcNow,
                                      state: FsrsState.Mastered));
        }

        var redundantFreq = await WordFormHelper.ArchiveRedundantImportCards(
            userContext, wordFormCache, userId, toInsert, allFreqCardKeys, _ => []);
        toInsert.RemoveAll(redundantFreq.Contains);

        if (toInsert.Count > 0)
        {
            await userContext.FsrsCards.AddRangeAsync(toInsert);
            await userContext.SaveChangesAsync();
        }

        var pruned = await WordFormHelper.PruneRedundantForms(userContext, wordFormCache, userId, targetWordIds);
        if (pruned > 0)
            await userContext.SaveChangesAsync();

        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
        await userContext.SaveChangesAsync();
        backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserCoverage(userId));

        var uniqueWords = toInsert.Select(c => c.WordId).Distinct().Count();
        logger.LogInformation("User imported words from frequency range: UserId={UserId}, MinFreq={MinFrequency}, MaxFreq={MaxFrequency}, WordCount={WordCount}, FormCount={FormCount}, PrunedCount={PrunedCount}",
                              userId, minFrequency, maxFrequency, uniqueWords, toInsert.Count, pruned);
        return Results.Ok(new { words = uniqueWords, forms = toInsert.Count, pruned });
    }


    /// <summary>
    /// Get user metadata
    /// </summary>
    [HttpGet("metadata")]
    public async Task<IResult> GetMetadata()
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var metadata = await userContext.UserMetadatas.SingleOrDefaultAsync(m => m.UserId == userId);
        if (metadata == null)
            return Results.Ok(new UserMetadata());

        return Results.Ok(metadata);
    }

    /// <summary>
    /// Queue a coverage refresh
    /// </summary>
    [HttpPost("coverage/refresh")]
    public async Task<IResult> RefreshCoverage()
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var cooldownSeconds = 90;
        var lockKey = $"jiten:coverage-refresh:{userId}";

        try
        {
            var redisDb = redis.GetDatabase();
            var lockAcquired = await redisDb.StringSetAsync(
                lockKey,
                DateTime.UtcNow.ToString("O"),
                TimeSpan.FromSeconds(cooldownSeconds),
                When.NotExists);

            if (!lockAcquired)
            {
                var ttl = await redisDb.KeyTimeToLiveAsync(lockKey);
                var retryAfter = ttl.HasValue ? (int)Math.Ceiling(ttl.Value.TotalSeconds) : cooldownSeconds;
                logger.LogInformation("Coverage refresh already in progress for user {UserId}, retry after {RetryAfter}s", userId, retryAfter);
                return Results.Json(new { status = "already_in_progress", retryAfterSeconds = retryAfter });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis unavailable for coverage refresh lock, proceeding without deduplication");
        }

        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
        await userContext.SaveChangesAsync();
        backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserCoverage(userId));

        return Results.Json(new { status = "queued" });
    }

    /// <summary>
    /// Get deck preference for a specific deck
    /// </summary>
    [HttpGet("deck-preferences/{deckId}")]
    public async Task<IResult> GetDeckPreference(int deckId)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var preference = await userContext.UserDeckPreferences
                                          .AsNoTracking()
                                          .FirstOrDefaultAsync(p => p.UserId == userId && p.DeckId == deckId);

        if (preference == null)
            return Results.Ok(new { deckId, status = DeckStatus.None, isFavourite = false, isIgnored = false });

        return Results.Ok(new { preference.DeckId, preference.Status, preference.IsFavourite, preference.IsIgnored });
    }

    /// <summary>
    /// Set favourite status for a deck
    /// </summary>
    [HttpPost("deck-preferences/{deckId}/favourite")]
    public async Task<IResult> SetFavourite(int deckId, [FromBody] SetFavouriteRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var preference = await userContext.UserDeckPreferences
                                          .FirstOrDefaultAsync(p => p.UserId == userId && p.DeckId == deckId);

        if (preference == null)
        {
            preference = new UserDeckPreference { UserId = userId, DeckId = deckId };
            userContext.UserDeckPreferences.Add(preference);
        }

        if (request.IsFavourite && preference.IsIgnored)
            return Results.BadRequest("A deck cannot be both favourited and ignored");

        preference.IsFavourite = request.IsFavourite;
        await userContext.SaveChangesAsync();

        return Results.Ok(new { preference.DeckId, preference.Status, preference.IsFavourite, preference.IsIgnored });
    }

    /// <summary>
    /// Set ignore status for a deck
    /// </summary>
    [HttpPost("deck-preferences/{deckId}/ignore")]
    public async Task<IResult> SetIgnore(int deckId, [FromBody] SetIgnoreRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var preference = await userContext.UserDeckPreferences
                                          .FirstOrDefaultAsync(p => p.UserId == userId && p.DeckId == deckId);

        if (preference == null)
        {
            preference = new UserDeckPreference { UserId = userId, DeckId = deckId };
            userContext.UserDeckPreferences.Add(preference);
        }

        if (request.IsIgnored && preference.IsFavourite)
            return Results.BadRequest("A deck cannot be both favourited and ignored");

        preference.IsIgnored = request.IsIgnored;
        await userContext.SaveChangesAsync();

        return Results.Ok(new { preference.DeckId, preference.Status, preference.IsFavourite, preference.IsIgnored });
    }

    /// <summary>
    /// Set status for a deck
    /// </summary>
    [HttpPost("deck-preferences/{deckId}/status")]
    public async Task<IResult> SetDeckStatus(int deckId, [FromBody] SetDeckStatusRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var preference = await userContext.UserDeckPreferences
                                          .FirstOrDefaultAsync(p => p.UserId == userId && p.DeckId == deckId);

        var previousStatus = preference?.Status ?? DeckStatus.None;

        if (preference == null)
        {
            preference = new UserDeckPreference { UserId = userId, DeckId = deckId };
            userContext.UserDeckPreferences.Add(preference);
        }

        preference.Status = request.Status;
        await userContext.SaveChangesAsync();

        // Trigger accomplishment recomputation if status changed to/from Completed
        if (previousStatus != request.Status &&
            (previousStatus == DeckStatus.Completed || request.Status == DeckStatus.Completed))
        {
            backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserAccomplishments(userId));
        }

        // Auto-cascade status to parent deck
        int? parentDeckId = null;
        DeckStatus? parentStatus = null;
        bool allChildrenCompleted = false;
        var deck = await jitenContext.Decks.AsNoTracking()
                                     .Where(d => d.DeckId == deckId)
                                     .Select(d => new { d.ParentDeckId })
                                     .FirstOrDefaultAsync();

        if (deck?.ParentDeckId != null)
        {
            var result = await UpdateParentDeckStatus(userId, deck.ParentDeckId.Value, request.Status);
            parentDeckId = result.ParentDeckId;
            parentStatus = result.ParentStatus;
            allChildrenCompleted = result.AllChildrenCompleted;
            if (allChildrenCompleted)
                parentDeckId ??= deck.ParentDeckId;
        }

        return Results.Ok(new { preference.DeckId, preference.Status, preference.IsFavourite, preference.IsIgnored, parentDeckId, parentStatus, allChildrenCompleted });
    }

    private async Task<(int? ParentDeckId, DeckStatus? ParentStatus, bool AllChildrenCompleted)> UpdateParentDeckStatus(string userId, int parentDeckId, DeckStatus childNewStatus)
    {
        var siblingIds = await jitenContext.Decks.AsNoTracking()
                                           .Where(d => d.ParentDeckId == parentDeckId)
                                           .Select(d => d.DeckId)
                                           .ToListAsync();

        var siblingStatuses = await userContext.UserDeckPreferences
                                               .Where(p => p.UserId == userId && siblingIds.Contains(p.DeckId))
                                               .ToDictionaryAsync(p => p.DeckId, p => p.Status);

        var parentPref = await userContext.UserDeckPreferences
                                          .FirstOrDefaultAsync(p => p.UserId == userId && p.DeckId == parentDeckId);

        var previousParentStatus = parentPref?.Status ?? DeckStatus.None;

        var allCompleted = siblingIds.All(id =>
            siblingStatuses.TryGetValue(id, out var status) && status == DeckStatus.Completed);

        DeckStatus? newParentStatus = null;
        var allChildrenCompleted = false;

        if (allCompleted)
        {
            if (previousParentStatus is DeckStatus.None or DeckStatus.Planning or DeckStatus.Ongoing)
            {
                allChildrenCompleted = true;
                if (previousParentStatus is DeckStatus.None or DeckStatus.Planning)
                    newParentStatus = DeckStatus.Ongoing;
            }
        }
        else if (childNewStatus is DeckStatus.Completed or DeckStatus.Ongoing or DeckStatus.Dropped)
        {
            if (previousParentStatus is DeckStatus.None or DeckStatus.Planning)
                newParentStatus = DeckStatus.Ongoing;
        }

        if (newParentStatus != null && newParentStatus != previousParentStatus)
        {
            if (parentPref == null)
            {
                parentPref = new UserDeckPreference { UserId = userId, DeckId = parentDeckId };
                userContext.UserDeckPreferences.Add(parentPref);
            }

            parentPref.Status = newParentStatus.Value;
            await userContext.SaveChangesAsync();

            if (previousParentStatus == DeckStatus.Completed || newParentStatus == DeckStatus.Completed)
            {
                backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserAccomplishments(userId));
            }

            return (parentDeckId, newParentStatus, allChildrenCompleted);
        }

        return (null, null, allChildrenCompleted);
    }

    /// <summary>
    /// Delete all preferences for a deck
    /// </summary>
    [HttpDelete("deck-preferences/{deckId}")]
    public async Task<IResult> DeleteDeckPreference(int deckId)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var preference = await userContext.UserDeckPreferences
                                          .FirstOrDefaultAsync(p => p.UserId == userId && p.DeckId == deckId);

        if (preference == null)
            return Results.Ok(new { deleted = false });

        userContext.UserDeckPreferences.Remove(preference);
        await userContext.SaveChangesAsync();

        return Results.Ok(new { deleted = true });
    }

    /// <summary>
    /// Export all FSRS cards and review logs for the current user as JSON.
    /// </summary>
    /// <param name="includeWordText">Adds the surface and kana reading of every form, so a backup stays readable without JMdict ids.</param>
    [HttpGet("vocabulary/export")]
    public async Task<IResult> ExportVocabulary([FromQuery] bool includeWordText = false)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var cards = await userContext.FsrsCards
                                     .AsNoTracking()
                                     .Include(c => c.ReviewLogs)
                                     .Where(c => c.UserId == userId)
                                     .OrderBy(c => c.WordId)
                                     .ThenBy(c => c.ReadingIndex)
                                     .ToListAsync();

        var archiveRows = await userContext.FsrsCardArchives
                                           .AsNoTracking()
                                           .Where(a => a.UserId == userId)
                                           .OrderBy(a => a.WordId)
                                           .ThenBy(a => a.ReadingIndex)
                                           .ToListAsync();

        var activityRows = await userContext.UserReviewDailies
                                            .AsNoTracking()
                                            .Where(d => d.UserId == userId)
                                            .OrderBy(d => d.LocalDate)
                                            .ToListAsync();

        Dictionary<(int, short), JmDictWordForm> wordForms = [];
        if (includeWordText)
        {
            var textWordIds = cards.Select(c => c.WordId).Concat(archiveRows.Select(a => a.WordId)).Distinct().ToList();
            if (textWordIds.Count > 0)
                wordForms = await WordFormHelper.LoadWordForms(jitenContext, textWordIds);
        }

        var exportDto = new FsrsExportDto
                        {
                            ExportDate = DateTime.UtcNow, UserId = userId, TotalCards = cards.Count,
                            TotalReviews = cards.Sum(c => c.ReviewLogs.Count), Cards = cards.Select(c => new FsrsCardExportDto
                                {
                                    WordId = c.WordId, ReadingIndex = c.ReadingIndex, State = c.State, Step = c.Step,
                                    Stability = EnsureValidNumber(c.Stability), Difficulty = EnsureValidNumber(c.Difficulty),
                                    Due = new DateTimeOffset(c.Due).ToUnixTimeSeconds(), LastReview = c.LastReview.HasValue
                                        ? new DateTimeOffset(c.LastReview.Value).ToUnixTimeSeconds()
                                        : null,
                                    CreatedAt = new DateTimeOffset(c.CreatedAt).ToUnixTimeSeconds(),
                                    ReviewLogs = c.ReviewLogs.OrderBy(r => r.ReviewDateTime)
                                                  .Select(r => new FsrsReviewLogExportDto
                                                               {
                                                                   Rating = r.Rating, ReviewDateTime =
                                                                       new DateTimeOffset(r.ReviewDateTime)
                                                                           .ToUnixTimeSeconds(),
                                                                   ReviewDuration = r.ReviewDuration
                                                               }).ToList(),
                                    Text = FormText(c.WordId, c.ReadingIndex), Reading = FormReading(c.WordId, c.ReadingIndex)
                                }).ToList(),
                            Archive = archiveRows.Select(a =>
                                {
                                    var (reviews, _) = CardArchiveService.ReadReviews(a);
                                    return new FsrsCardArchiveExportDto
                                           {
                                               WordId = a.WordId, ReadingIndex = a.ReadingIndex,
                                               ArchivedAt = new DateTimeOffset(a.ArchivedAt).ToUnixTimeSeconds(),
                                               Reason = a.Reason, CoveringReadingIndex = a.CoveringReadingIndex,
                                               State = a.State, Step = a.Step,
                                               Stability = EnsureValidNumber(a.Stability), Difficulty = EnsureValidNumber(a.Difficulty),
                                               Due = new DateTimeOffset(a.Due).ToUnixTimeSeconds(),
                                               LastReview = a.LastReview.HasValue
                                                   ? new DateTimeOffset(a.LastReview.Value).ToUnixTimeSeconds()
                                                   : null,
                                               Lapses = a.Lapses,
                                               CardCreatedAt = new DateTimeOffset(a.CardCreatedAt).ToUnixTimeSeconds(),
                                               ReviewCount = a.ReviewCount,
                                               HistoryMerged = a.HistoryMerged,
                                               ReviewLogs = reviews.Select(r => new FsrsReviewLogExportDto
                                                                                {
                                                                                    Rating = r.Rating,
                                                                                    ReviewDateTime = new DateTimeOffset(r.ReviewDateTime).ToUnixTimeSeconds(),
                                                                                    ReviewDuration = r.ReviewDuration
                                                                                }).ToList(),
                                               Text = FormText(a.WordId, a.ReadingIndex), Reading = FormReading(a.WordId, a.ReadingIndex),
                                               CoveringText = a.CoveringReadingIndex.HasValue
                                                   ? FormText(a.WordId, a.CoveringReadingIndex.Value)
                                                   : null
                                           };
                                }).ToList(),
                            ReviewActivity = activityRows.Select(d => new UserReviewDailyExportDto
                                                                      {
                                                                          LocalDate = d.LocalDate.ToString("yyyy-MM-dd"),
                                                                          ReviewCount = d.ReviewCount,
                                                                          CorrectCount = d.CorrectCount,
                                                                          NewCardCount = d.NewCardCount,
                                                                          TotalDurationMs = d.TotalDurationMs
                                                                      }).ToList()
                        };

        logger.LogInformation(
            "User exported vocabulary: UserId={UserId}, CardCount={CardCount}, ReviewCount={ReviewCount}, ArchiveCount={ArchiveCount}, ActivityDays={ActivityDays}, WordText={WordText}",
            userId, exportDto.TotalCards, exportDto.TotalReviews, archiveRows.Count, activityRows.Count, includeWordText);

        return Results.Ok(exportDto);

        double? EnsureValidNumber(double? value)
        {
            if (!value.HasValue) return null;
            if (double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return null;
            return value;
        }

        string? FormText(int wordId, byte readingIndex)
            => wordForms.GetValueOrDefault((wordId, (short)readingIndex))?.Text;

        string? FormReading(int wordId, byte readingIndex)
        {
            var form = wordForms.GetValueOrDefault((wordId, (short)readingIndex));
            if (form == null) return null;
            return form.FormType == JmDictFormType.KanaForm ? form.Text : RubyTextHelper.KanaFromRubyText(form.RubyText);
        }
    }

    /// <summary>
    /// Export user's vocabulary as a TXT organised by learning state.
    /// </summary>
    [HttpGet("vocabulary/export-words")]
    [Produces("text/plain")]
    public async Task<IResult> ExportWords(
        [FromQuery] bool exportKanaOnly = false,
        [FromQuery] bool exportMastered = true,
        [FromQuery] bool exportMature = true,
        [FromQuery] bool exportYoung = true,
        [FromQuery] bool exportBlacklisted = true)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        if (!exportMastered && !exportMature && !exportYoung && !exportBlacklisted)
            return Results.BadRequest("At least one category must be selected");

        var cards = await userContext.FsrsCards
                                     .AsNoTracking()
                                     .Where(c => c.UserId == userId)
                                     .OrderBy(c => c.WordId)
                                     .ThenBy(c => c.ReadingIndex)
                                     .ToListAsync();

        if (cards.Count == 0)
        {
            return Results.NoContent();
        }

        var masteredCards = new List<FsrsCard>();
        var matureCards = new List<FsrsCard>();
        var youngCards = new List<FsrsCard>();
        var blacklistedCards = new List<FsrsCard>();

        var knownStates = await userService.GetKnownWordsState(cards.Select(c => (c.WordId, c.ReadingIndex)));

        foreach (var card in cards)
        {
            var states = knownStates[(card.WordId, card.ReadingIndex)];

            if (exportMastered && states.Contains(KnownState.Mastered))
            {
                masteredCards.Add(card);
            }

            if (exportBlacklisted && states.Contains(KnownState.Blacklisted))
            {
                blacklistedCards.Add(card);
            }

            if (exportMature && states.Contains(KnownState.Mature))
            {
                matureCards.Add(card);
            }

            if (exportYoung && states.Contains(KnownState.Young))
            {
                youngCards.Add(card);
            }
        }

        var allCards = masteredCards.Concat(matureCards).Concat(youngCards).Concat(blacklistedCards).ToList();
        var wordIds = allCards.Select(c => c.WordId).Distinct().ToList();

        var jmdictWords = await jitenContext.JMDictWords
                                            .AsNoTracking()
                                            .Where(w => wordIds.Contains(w.WordId))
                                            .Select(w => new { w.WordId })
                                            .ToDictionaryAsync(w => w.WordId);
        var exportForms = await WordFormHelper.LoadWordForms(jitenContext, wordIds);

        var txt = new StringBuilder();

        AppendSection("MASTERED", masteredCards);
        AppendSection("MATURE", matureCards);
        AppendSection("YOUNG", youngCards);
        AppendSection("BLACKLISTED", blacklistedCards);

        var txtBytes = Encoding.UTF8.GetBytes(txt.ToString());

        logger.LogInformation(
                              "User exported words: UserId={UserId}, TotalCards={TotalCards}, Mastered={Mastered}, Mature={Mature}, Young={Young}, Blacklisted={Blacklisted}",
                              userId, allCards.Count, masteredCards.Count, matureCards.Count, youngCards.Count, blacklistedCards.Count);

        var dateStr = DateTime.UtcNow.ToString("yyyy-MM-dd");
        return Results.File(txtBytes, "text/plain", $"jiten-vocabulary-export-{dateStr}.txt");

        void AppendSection(string sectionName, List<FsrsCard> sectionCards)
        {
            if (sectionCards.Count == 0) return;

            txt.AppendLine($"=== {sectionName} ===");

            foreach (var card in sectionCards)
            {
                if (!jmdictWords.TryGetValue(card.WordId, out var word)) continue;
                var form = exportForms.GetValueOrDefault((card.WordId, (short)card.ReadingIndex));
                if (form == null) continue;

                var reading = form.Text;

                if (!exportKanaOnly && WanaKana.IsKana(reading)) continue;

                txt.AppendLine(reading);
            }
        }
    }

    /// <summary>
    /// Get all FSRS cards for the current user with word information.
    /// </summary>
    [HttpGet("vocabulary/cards")]
    public async Task<IResult> GetCards()
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var cards = await userContext.FsrsCards
                                     .AsNoTracking()
                                     .Where(c => c.UserId == userId)
                                     .OrderBy(c => c.Due)
                                     .ToListAsync();

        var wordIds = cards.Select(c => c.WordId).Distinct().ToList();

        var cardForms = await WordFormHelper.LoadWordForms(jitenContext, wordIds);
        var cardFormFreqs = await WordFormHelper.LoadWordFormFrequencies(jitenContext, wordIds);

        var result = cards.Select(c =>
        {
            var form = cardForms.GetValueOrDefault((c.WordId, (short)c.ReadingIndex));
            var formFreq = cardFormFreqs.GetValueOrDefault((c.WordId, (short)c.ReadingIndex));

            return new FsrsCardWithWordDto
                   {
                       CardId = c.CardId, WordId = c.WordId, ReadingIndex = c.ReadingIndex, State = c.State, Step = c.Step,
                       Stability = EnsureValidNumber(c.Stability), Difficulty = EnsureValidNumber(c.Difficulty), Due = c.Due,
                       LastReview = c.LastReview, CreatedAt = c.CreatedAt, WordText = form?.Text ?? "",
                       ReadingType = form != null ? (JmDictReadingType)(int)form.FormType : JmDictReadingType.Reading,
                       FrequencyRank = formFreq?.FrequencyRank ?? 0,
                       Lapses = c.Lapses,
                   };
        }).ToList();

        logger.LogInformation("User fetched cards view: UserId={UserId}, CardCount={CardCount}",
                              userId, result.Count);

        return Results.Ok(result);

        double? EnsureValidNumber(double? value)
        {
            if (!value.HasValue) return null;
            if (double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return null;
            return value;
        }
    }

    /// <summary>
    /// Import FSRS cards and review logs from JSON export.
    /// </summary>
    [HttpPost("vocabulary/import")]
    public async Task<IResult> ImportVocabulary([FromBody] FsrsExportDto exportDto, [FromQuery] bool overwrite = false)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        if (exportDto?.Cards == null || exportDto.Cards.Count == 0)
            return Results.BadRequest("Invalid export data");

        var result = new FsrsImportResultDto
                     {
                         ValidationErrors = [], CardsImported = 0, CardsSkipped = 0, CardsUpdated = 0, ReviewLogsImported = 0
                     };

        var distinctWordIds = exportDto.Cards.Select(c => c.WordId).Distinct().ToList();

        var wordValidationMap = await jitenContext.WordForms
                                                  .AsNoTracking()
                                                  .Where(wf => distinctWordIds.Contains(wf.WordId))
                                                  .GroupBy(wf => wf.WordId)
                                                  .Select(g => new { WordId = g.Key, ReadingCount = g.Count() })
                                                  .ToDictionaryAsync(w => w.WordId);

        var validCards = new List<FsrsCardExportDto>(exportDto.Cards.Count);
        int skippedNew = 0;
        int skippedRedundant = 0;

        foreach (var card in exportDto.Cards)
        {
            if (card.State == FsrsState.New)
            {
                skippedNew++;
                continue;
            }

            if (!wordValidationMap.TryGetValue(card.WordId, out var wordInfo))
            {
                result.ValidationErrors.Add($"WordId {card.WordId} does not exist in JMDict");
                continue;
            }

            if (card.ReadingIndex >= wordInfo.ReadingCount)
            {
                result.ValidationErrors.Add($"WordId {card.WordId} ReadingIndex {card.ReadingIndex} is invalid");
                continue;
            }

            validCards.Add(card);
        }

        var importWordIds = validCards.Select(c => c.WordId).Distinct().ToList();
        var existingUserCardKeys = await userContext.FsrsCards
            .AsNoTracking()
            .Where(c => c.UserId == userId && importWordIds.Contains(c.WordId))
            .Select(c => new { c.WordId, c.ReadingIndex })
            .ToListAsync();

        var allBackupCardKeys = validCards.Select(c => (c.WordId, c.ReadingIndex))
            .Concat(existingUserCardKeys.Select(k => (k.WordId, k.ReadingIndex)))
            .ToHashSet();
        // A file listing one form twice is malformed rather than fatal: the later entry wins, as it would once the
        // import loop reached it.
        var dtoByKey = new Dictionary<(int WordId, byte ReadingIndex), FsrsCardExportDto>();
        foreach (var card in validCards)
            dtoByKey[(card.WordId, card.ReadingIndex)] = card;

        // The gate works on cards, so each entry is offered as the card it would become, minus the logs: the file's
        // history is only unpacked for the forms actually dropped.
        FsrsCard BackupCard(FsrsCardExportDto dto) => new(userId, dto.WordId, dto.ReadingIndex)
                                                      {
                                                          State = dto.State, Step = dto.Step, Stability = dto.Stability,
                                                          Difficulty = dto.Difficulty,
                                                          Due = DateTimeOffset.FromUnixTimeSeconds(dto.Due).UtcDateTime,
                                                          LastReview = dto.LastReview.HasValue
                                                              ? DateTimeOffset.FromUnixTimeSeconds(dto.LastReview.Value).UtcDateTime
                                                              : null,
                                                          CreatedAt = dto.CreatedAt > 0
                                                              ? DateTimeOffset.FromUnixTimeSeconds(dto.CreatedAt).UtcDateTime
                                                              : DateTime.UtcNow
                                                      };

        var backupCandidates = dtoByKey.Values.Select(BackupCard).ToList();

        var archiveImported = await ImportArchiveSection(userId, exportDto.Archive);

        // After the file's own archive section, which saves, so a form present in both merges into one row.
        var redundantBackup = await WordFormHelper.ArchiveRedundantImportCards(
            userContext, wordFormCache, userId, backupCandidates, allBackupCardKeys,
            card => dtoByKey[(card.WordId, card.ReadingIndex)].ReviewLogs
                                                             .DistinctBy(l => l.ReviewDateTime)
                                                             .Select(l => new PackedReview(
                                                                         l.Rating,
                                                                         DateTimeOffset.FromUnixTimeSeconds(l.ReviewDateTime)
                                                                                       .UtcDateTime,
                                                                         l.ReviewDuration))
                                                             .ToList());
        var archivedRedundant = redundantBackup.Archived;
        if (archivedRedundant > 0)
            await userContext.SaveChangesAsync();

        var redundantKeys = redundantBackup.Covering.Keys.Select(c => (c.WordId, c.ReadingIndex)).ToHashSet();
        validCards.RemoveAll(c => redundantKeys.Contains((c.WordId, c.ReadingIndex)));
        skippedRedundant = redundantKeys.Count;

        result.CardsSkipped += skippedNew + skippedRedundant;

        // Carried counters win over a rebuild: they were built under the timezone the backup was taken in, and
        // recomputing would silently re-bucket every day. A file without the section falls back to a rebuild.
        var activityImported = await ImportReviewActivitySection(userId, exportDto.ReviewActivity);
        var rollupNeedsRebuild = !activityImported;

        if (validCards.Count == 0)
        {
            if ((archiveImported > 0 || archivedRedundant > 0) && rollupNeedsRebuild)
                await MarkReviewRollupDirty(userId);
            return Results.Ok(result);
        }

        await using var transaction = await userContext.Database.BeginTransactionAsync();
        try
        {
            var existingCardsMap = await userContext.FsrsCards
                                                    .Include(c => c.ReviewLogs)
                                                    .Where(c => c.UserId == userId)
                                                    .ToDictionaryAsync(c => (c.WordId, c.ReadingIndex));

            var cardsToAdd = new List<FsrsCard>();
            var liveTimesByCard = new Dictionary<FsrsCard, List<DateTime>>();

            foreach (var cardDto in validCards)
            {
                var key = (cardDto.WordId, cardDto.ReadingIndex);

                var uniqueIncomingLogs = cardDto.ReviewLogs
                                                .DistinctBy(l => l.ReviewDateTime)
                                                .ToList();

                var importLapses = CountLapsesFromLogs(uniqueIncomingLogs);

                if (existingCardsMap.TryGetValue(key, out var existingCard))
                {
                    if (!overwrite)
                    {
                        result.CardsSkipped++;
                        continue;
                    }

                    existingCard.State = cardDto.State;
                    existingCard.Step = cardDto.Step;
                    existingCard.Stability = cardDto.Stability;
                    existingCard.Difficulty = cardDto.Difficulty;
                    existingCard.Due = DateTimeOffset.FromUnixTimeSeconds(cardDto.Due).UtcDateTime;
                    existingCard.LastReview = cardDto.LastReview.HasValue
                        ? DateTimeOffset.FromUnixTimeSeconds(cardDto.LastReview.Value).UtcDateTime
                        : null;
                    existingCard.Lapses = importLapses;
                    if (cardDto.CreatedAt > 0)
                        existingCard.CreatedAt = DateTimeOffset.FromUnixTimeSeconds(cardDto.CreatedAt).UtcDateTime;

                    userContext.FsrsReviewLogs.RemoveRange(existingCard.ReviewLogs);

                    foreach (var logDto in uniqueIncomingLogs)
                    {
                        existingCard.ReviewLogs.Add(new FsrsReviewLog
                                                    {
                                                        Rating = logDto.Rating, ReviewDateTime = DateTimeOffset
                                                            .FromUnixTimeSeconds(logDto.ReviewDateTime)
                                                            .UtcDateTime,
                                                        ReviewDuration = logDto.ReviewDuration,
                                                    });
                    }

                    result.CardsUpdated++;
                    result.ReviewLogsImported += cardDto.ReviewLogs.Count;
                    liveTimesByCard[existingCard] = uniqueIncomingLogs
                                                    .Select(l => DateTimeOffset.FromUnixTimeSeconds(l.ReviewDateTime).UtcDateTime)
                                                    .ToList();
                }
                else
                {
                    var newCard = new FsrsCard(userId, cardDto.WordId, cardDto.ReadingIndex)
                                  {
                                      State = cardDto.State, Step = cardDto.Step, Stability = cardDto.Stability,
                                      Difficulty = cardDto.Difficulty, Due = DateTimeOffset.FromUnixTimeSeconds(cardDto.Due).UtcDateTime,
                                      LastReview = cardDto.LastReview.HasValue
                                          ? DateTimeOffset.FromUnixTimeSeconds(cardDto.LastReview.Value).UtcDateTime
                                          : null,
                                      Lapses = importLapses,
                                      CreatedAt = cardDto.CreatedAt > 0
                                          ? DateTimeOffset.FromUnixTimeSeconds(cardDto.CreatedAt).UtcDateTime
                                          : DateTime.UtcNow,
                                      ReviewLogs = uniqueIncomingLogs.Select(l => new FsrsReviewLog
                                                                                  {
                                                                                      Rating = l.Rating, ReviewDateTime = DateTimeOffset
                                                                                          .FromUnixTimeSeconds(l.ReviewDateTime)
                                                                                          .UtcDateTime,
                                                                                      ReviewDuration = l.ReviewDuration
                                                                                  }).ToList()
                                  };

                    cardsToAdd.Add(newCard);
                    result.CardsImported++;
                    result.ReviewLogsImported += cardDto.ReviewLogs.Count;
                    liveTimesByCard[newCard] = newCard.ReviewLogs.Select(l => l.ReviewDateTime).ToList();
                }
            }

            // The file's own logs are already on the card, so the archive only fills whatever gaps they leave.
            var restoredCards = await CardRestoreService.AutoRestoreAsync(userContext, userId, cardsToAdd,
                                                                         restoreSchedule: false);

            if (cardsToAdd.Count > 0)
            {
                await userContext.FsrsCards.AddRangeAsync(cardsToAdd);
            }

            await userContext.SaveChangesAsync();

            var droppedFromArchive = await CardArchiveService.DropReviewsHeldLiveAsync(
                userContext, userId, liveTimesByCard.Keys.ToList(),
                card => liveTimesByCard.TryGetValue(card, out var times) ? times : []);
            if (droppedFromArchive > 0)
                await userContext.SaveChangesAsync();

            var pruned = await WordFormHelper.PruneRedundantForms(userContext, wordFormCache, userId, importWordIds);
            if (pruned > 0)
                await userContext.SaveChangesAsync();

            await transaction.CommitAsync();

            await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
            await userContext.SaveChangesAsync();
            backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserCoverage(userId));

            if (rollupNeedsRebuild)
                await MarkReviewRollupDirty(userId);

            logger.LogInformation(
                                  "Import stats: Imported={Imported}, Updated={Updated}, Skipped={Skipped}, SkippedNew={SkippedNew}, SkippedRedundant={SkippedRedundant}, Archived={Archived}, Restored={Restored}, Pruned={Pruned}",
                                  result.CardsImported, result.CardsUpdated, result.CardsSkipped, skippedNew, skippedRedundant,
                                  archivedRedundant, restoredCards, pruned);

            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            logger.LogError(ex, "Import failed");
            return Results.Problem("Import failed: " + ex.Message);
        }
    }

    #region Accomplishments

    /// <summary>
    /// Get accomplishments for a user. Requires own profile or public profile.
    /// </summary>
    [HttpGet("user/{targetUserId}/accomplishments")]
    [AllowAnonymous]
    public async Task<IResult> GetUserAccomplishments(string targetUserId)
    {
        var currentUserId = userService.UserId;
        var isOwnProfile = currentUserId == targetUserId;

        if (!isOwnProfile)
        {
            var profile = await userContext.UserProfiles
                                           .AsNoTracking()
                                           .FirstOrDefaultAsync(p => p.UserId == targetUserId);

            if (profile == null || !profile.IsPublic)
                return Results.Forbid();
        }

        var accomplishments = await userContext.UserAccomplishments
                                               .AsNoTracking()
                                               .Where(ua => ua.UserId == targetUserId)
                                               .OrderBy(ua => ua.MediaType)
                                               .ToListAsync();

        return Results.Ok(accomplishments);
    }

    /// <summary>
    /// Get accomplishment for a specific MediaType. Use 'global' for all types combined.
    /// Requires own profile or public profile.
    /// </summary>
    [HttpGet("user/{targetUserId}/accomplishments/global")]
    [HttpGet("user/{targetUserId}/accomplishments/{mediaType}")]
    [AllowAnonymous]
    public async Task<IResult> GetUserAccomplishment(string targetUserId, Jiten.Core.Data.MediaType? mediaType = null)
    {
        var currentUserId = userService.UserId;
        var isOwnProfile = currentUserId == targetUserId;

        if (!isOwnProfile)
        {
            var profile = await userContext.UserProfiles
                                           .AsNoTracking()
                                           .FirstOrDefaultAsync(p => p.UserId == targetUserId);

            if (profile == null || !profile.IsPublic)
                return Results.Forbid();
        }

        var accomplishment = await userContext.UserAccomplishments
                                              .AsNoTracking()
                                              .FirstOrDefaultAsync(ua => ua.UserId == targetUserId && ua.MediaType == mediaType);

        if (accomplishment == null)
            return Results.NotFound();

        return Results.Ok(accomplishment);
    }

    /// <summary>
    /// Manually trigger accomplishment recomputation.
    /// </summary>
    [HttpPost("accomplishments/refresh")]
    public IResult RefreshAccomplishments()
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserAccomplishments(userId));

        return Results.Accepted();
    }

    /// <summary>
    /// Get aggregated vocabulary across all completed decks.
    /// Requires own profile or public profile.
    /// </summary>
    [HttpGet("user/{targetUserId}/accomplishments/vocabulary")]
    [AllowAnonymous]
    public async Task<ActionResult<PaginatedResponse<AccomplishmentVocabularyDto>>> GetAccomplishmentVocabulary(
        string targetUserId,
        [FromQuery] MediaType? mediaType = null,
        [FromQuery] int offset = 0,
        [FromQuery] int pageSize = 100,
        [FromQuery] string sortBy = "occurrences",
        [FromQuery] bool descending = true)
    {
        var currentUserId = userService.UserId;
        var isOwnProfile = currentUserId == targetUserId;

        if (!isOwnProfile)
        {
            var profile = await userContext.UserProfiles
                                           .AsNoTracking()
                                           .FirstOrDefaultAsync(p => p.UserId == targetUserId);

            if (profile == null || !profile.IsPublic)
                return Forbid();
        }

        pageSize = Math.Clamp(pageSize, 1, 100);

        // Get all completed deck IDs for the user
        var userCompletedDeckIds = await userContext.UserDeckPreferences
                                                    .AsNoTracking()
                                                    .Where(udp => udp.UserId == targetUserId && udp.Status == DeckStatus.Completed)
                                                    .Select(udp => udp.DeckId)
                                                    .ToListAsync();

        // Load completed decks with parent relationship
        var allCompletedDecks = await jitenContext.Decks
                                                  .AsNoTracking()
                                                  .Where(d => userCompletedDeckIds.Contains(d.DeckId))
                                                  .Select(d => new { d.DeckId, d.ParentDeckId, d.MediaType })
                                                  .ToListAsync();

        // Build effective deck set: include parents, and children only if their parent is NOT completed
        var completedParentIds = allCompletedDecks
                                 .Where(d => d.ParentDeckId == null)
                                 .Select(d => d.DeckId)
                                 .ToHashSet();

        var effectiveDecks = allCompletedDecks
                             .Where(d => d.ParentDeckId == null || !completedParentIds.Contains(d.ParentDeckId.Value))
                             .ToList();

        // Apply media type filter if provided
        if (mediaType.HasValue)
        {
            effectiveDecks = effectiveDecks.Where(d => d.MediaType == mediaType.Value).ToList();
        }

        var completedDeckIds = effectiveDecks.Select(d => d.DeckId).ToList();

        if (completedDeckIds.Count == 0)
        {
            return Ok(new PaginatedResponse<AccomplishmentVocabularyDto>(
                                                                         new AccomplishmentVocabularyDto { Words = [] }, 0, pageSize,
                                                                         offset));
        }

        // Aggregate vocabulary across completed decks
        var aggregatedWordsQuery = jitenContext.DeckWords
                                               .AsNoTracking()
                                               .Where(dw => completedDeckIds.Contains(dw.DeckId))
                                               .GroupBy(dw => new { dw.WordId, dw.ReadingIndex })
                                               .Select(g => new AggregatedWord
                                                            {
                                                                WordId = g.Key.WordId, ReadingIndex = g.Key.ReadingIndex,
                                                                TotalOccurrences = g.Sum(dw => dw.Occurrences)
                                                            });

        int totalCount = await aggregatedWordsQuery.CountAsync();

        // Apply sorting and pagination
        List<AggregatedWord> pagedWords;
        if (sortBy.Equals("alphabetical", StringComparison.OrdinalIgnoreCase))
        {
            // For alphabetical sorting, we need to join with JMDict to get reading text
            var sortedQuery = from aw in aggregatedWordsQuery
                              join wf in jitenContext.WordForms on new { aw.WordId, ReadingIndex = (short)aw.ReadingIndex } equals new { wf.WordId, wf.ReadingIndex }
                              select new { aw, ReadingText = wf.Text };

            var orderedQuery = descending
                ? sortedQuery.OrderByDescending(x => x.ReadingText)
                : sortedQuery.OrderBy(x => x.ReadingText);

            pagedWords = await orderedQuery
                               .Skip(offset)
                               .Take(pageSize)
                               .Select(x => x.aw)
                               .ToListAsync();
        }
        else
        {
            // Default: sort by occurrences
            var orderedQuery = descending
                ? aggregatedWordsQuery.OrderByDescending(w => w.TotalOccurrences)
                : aggregatedWordsQuery.OrderBy(w => w.TotalOccurrences);

            pagedWords = await orderedQuery
                               .Skip(offset)
                               .Take(pageSize)
                               .ToListAsync();
        }

        var wordIds = pagedWords.Select(w => w.WordId).ToList();

        // Fetch JMDict words
        var jmdictWords = await jitenContext.JMDictWords
                                            .AsNoTracking()
                                            .Where(w => wordIds.Contains(w.WordId))
                                            .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
                                            .ToListAsync();

        var jmdictLookup = jmdictWords.ToDictionary(w => w.WordId);

        var accForms = await WordFormHelper.LoadWordForms(jitenContext, wordIds);
        var accFormFreqs = await WordFormHelper.LoadWordFormFrequencies(jitenContext, wordIds);

        // Build WordDtos - preserve the order from pagedWords
        var wordDtos = new List<WordDto>();
        foreach (var pw in pagedWords)
        {
            if (!jmdictLookup.TryGetValue(pw.WordId, out var jmWord))
                continue;

            var mainForm = accForms.GetValueOrDefault((pw.WordId, (short)pw.ReadingIndex));
            if (mainForm == null)
                continue;

            var allFormsForWord = accForms.Where(f => f.Key.Item1 == pw.WordId)
                                          .OrderBy(f => f.Key.Item2)
                                          .Select(f => f.Value)
                                          .ToList();

            var alternativeReadings = allFormsForWord
                                            .Where(f => f.ReadingIndex != pw.ReadingIndex)
                                            .Select(f => WordFormHelper.ToPlainFormDto(f, accFormFreqs.GetValueOrDefault((f.WordId, f.ReadingIndex))))
                                            .ToList();

            var mainReading = WordFormHelper.ToFormDto(mainForm, accFormFreqs.GetValueOrDefault((pw.WordId, (short)pw.ReadingIndex)));

            wordDtos.Add(new WordDto
                         {
                             WordId = jmWord.WordId, MainReading = mainReading, AlternativeReadings = alternativeReadings,
                             PartsOfSpeech = jmWord.PartsOfSpeech.ToHumanReadablePartsOfSpeech(),
                             Definitions = jmWord.Definitions.ToDefinitionDtos(), Occurrences = pw.TotalOccurrences,
                             PitchAccents = jmWord.PitchAccents
                         });
        }

        // Apply known states
        var knownStates = await userService.GetKnownWordsState(
                                                               wordDtos.Select(w => (w.WordId, w.MainReading.ReadingIndex)).ToList());
        wordDtos.ApplyKnownWordsState(knownStates);

        var dto = new AccomplishmentVocabularyDto { Words = wordDtos };

        return Ok(new PaginatedResponse<AccomplishmentVocabularyDto>(dto, totalCount, pageSize, offset));
    }

    #endregion

    #region Profile

    /// <summary>
    /// Get current user's profile.
    /// </summary>
    [HttpGet("profile")]
    public async Task<IResult> GetProfile()
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var profile = await userContext.UserProfiles
                                       .AsNoTracking()
                                       .FirstOrDefaultAsync(p => p.UserId == userId);

        if (profile == null)
        {
            // Return default profile
            return Results.Ok(new UserProfile { UserId = userId, IsPublic = false });
        }

        return Results.Ok(profile);
    }

    /// <summary>
    /// Update current user's profile.
    /// </summary>
    [HttpPatch("profile")]
    public async Task<IResult> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var profile = await userContext.UserProfiles
                                       .FirstOrDefaultAsync(p => p.UserId == userId);

        if (profile == null)
        {
            profile = new UserProfile { UserId = userId };
            userContext.UserProfiles.Add(profile);
        }

        if (request.IsPublic.HasValue)
            profile.IsPublic = request.IsPublic.Value;

        if (request.IsMediaListPublic.HasValue)
            profile.IsMediaListPublic = request.IsMediaListPublic.Value;

        // The media list can only be public while the whole profile is public.
        // Turning the profile private auto-deactivates the media list flag.
        if (!profile.IsPublic)
            profile.IsMediaListPublic = false;

        await userContext.SaveChangesAsync();

        return Results.Ok(profile);
    }

    /// <summary>
    /// Get another user's profile by user ID (limited info if not public).
    /// </summary>
    [HttpGet("user/{targetUserId}/profile")]
    [AllowAnonymous]
    public async Task<IResult> GetUserProfile(string targetUserId)
    {
        // Check user exists
        var user = await userContext.Users
                                    .AsNoTracking()
                                    .FirstOrDefaultAsync(u => u.Id == targetUserId);

        if (user == null)
            return Results.NotFound(new { message = "User not found" });

        var currentUserId = userService.UserId;
        var isOwnProfile = currentUserId == targetUserId;

        var profile = await userContext.UserProfiles
                                       .AsNoTracking()
                                       .FirstOrDefaultAsync(p => p.UserId == targetUserId);

        if (isOwnProfile)
        {
            return Results.Ok(new UserProfileResponse
                              {
                                  UserId = user.Id, Username = user.UserName ?? string.Empty,
                                  IsPublic = profile?.IsPublic ?? false, IsMediaListPublic = profile?.IsMediaListPublic ?? false
                              });
        }

        if (profile == null || !profile.IsPublic)
        {
            // Return minimal info for non-public profiles
            return Results.Ok(new UserProfileResponse { UserId = user.Id, Username = user.UserName ?? string.Empty, IsPublic = false });
        }

        return Results.Ok(new UserProfileResponse
                          {
                              UserId = user.Id, Username = user.UserName ?? string.Empty,
                              IsPublic = profile.IsPublic, IsMediaListPublic = profile.IsMediaListPublic
                          });
    }

    /// <summary>
    /// Get a user's profile by username (case-insensitive).
    /// Returns 404 if user doesn't exist or profile is private (to prevent username enumeration).
    /// </summary>
    [HttpGet("profile/{username}")]
    [AllowAnonymous]
    public async Task<IResult> GetUserProfileByUsername(string username)
    {
        var user = await userContext.Users
                                    .AsNoTracking()
                                    .FirstOrDefaultAsync(u => u.NormalizedUserName == username.ToUpperInvariant());

        if (user == null)
            return Results.NotFound(new { message = "Profile not found" });

        var currentUserId = userService.UserId;
        var isOwnProfile = currentUserId == user.Id;

        var profile = await userContext.UserProfiles
                                       .AsNoTracking()
                                       .FirstOrDefaultAsync(p => p.UserId == user.Id);

        // Return 404 for private profiles (same as non-existent to prevent enumeration)
        if (!isOwnProfile && (profile == null || !profile.IsPublic))
            return Results.NotFound(new { message = "Profile not found" });

        return Results.Ok(new UserProfileResponse
                          {
                              UserId = user.Id, Username = user.UserName ?? string.Empty,
                              IsPublic = profile?.IsPublic ?? false, IsMediaListPublic = profile?.IsMediaListPublic ?? false
                          });
    }

    /// <summary>
    /// Get a user's tracked media list (decks with a status) by username, as DeckDtos so the frontend
    /// can render them with the same card/compact/table views as the catalogue. Child decks are collapsed
    /// to their parent (series) for display. Visible to non-owners only when both the profile and the
    /// media list are public. Returns 404 otherwise.
    /// </summary>
    [HttpGet("profile/{username}/media-list")]
    [AllowAnonymous]
    public async Task<IResult> GetUserMediaListByUsername(string username)
    {
        var (userId, _, allowed) = await ResolveMediaListAccessAsync(username);
        if (userId == null || !allowed)
            return Results.NotFound(new { message = "Media list not found" });

        var entries = await BuildMediaListAsync(userId);

        var dtos = entries
                   .Select(e => new DeckDto(e.Display) { Status = e.Status, IsFavourite = e.IsFavourite })
                   .OrderBy(d => d.OriginalTitle)
                   .ToList();

        // Populate the viewer's coverage so cards render the coverage border, exactly like the catalogue.
        var viewerId = userService.UserId;
        if (!string.IsNullOrEmpty(viewerId) && dtos.Count > 0)
        {
            var coverage = await UserCoverageChunkHelper.GetCoverage(userContext, viewerId, dtos.Select(d => d.DeckId).ToList());
            coverage.ApplyTo(dtos);
        }

        return Results.Ok(dtos);
    }

    /// <summary>
    /// Resolves and merges the vocabulary of every tracked deck in a status category into a single
    /// deduplicated list (occurrences summed), honouring the full set of download filters per deck.
    /// Shared by the download, count and learn endpoints so previews always match the result.
    /// </summary>
    private async Task<(List<(int WordId, byte ReadingIndex, int Occurrences)> Words, List<int> SentenceDeckIds, IResult? Error)>
        ResolveMergedCategoryAsync(string userId, DeckStatus status, DeckDownloadRequest request)
    {
        var entries = (await BuildMediaListAsync(userId)).Where(e => e.Status == status).ToList();

        var merged = new Dictionary<(int WordId, byte ReadingIndex), int>();
        var sentenceDeckIds = new List<int>();
        foreach (var (display, _, _) in entries)
        {
            var (words, error) = await deckWordResolver.ResolveDeckWords(new DeckWordResolveRequest(
                display.DeckId, display, request.DownloadType, request.Order,
                request.MinFrequency, request.MaxFrequency,
                request.ExcludeMatureMasteredBlacklisted, request.ExcludeAllTrackedWords,
                request.TargetPercentage, request.MinOccurrences, request.MaxOccurrences,
                StartFromKnown: request.StartFromKnown));

            if (error != null)
                return (new(), new(), error);
            if (words == null)
                continue;

            foreach (var w in words)
            {
                var key = (w.WordId, w.ReadingIndex);
                merged[key] = merged.GetValueOrDefault(key) + w.Occurrences;
            }

            sentenceDeckIds.AddRange(display.Children.Count != 0
                                         ? display.Children.Select(c => c.DeckId)
                                         : new[] { display.DeckId });
        }

        var list = merged
                   .Select(kv => (WordId: kv.Key.WordId, ReadingIndex: kv.Key.ReadingIndex, Occurrences: kv.Value))
                   .ToList();

        if (request.ExcludeKana && list.Count > 0)
        {
            var forms = await WordFormHelper.LoadWordForms(jitenContext, list.Select(x => x.WordId).Distinct().ToList());
            list = list.Where(x =>
            {
                var form = forms.GetValueOrDefault((x.WordId, (short)x.ReadingIndex));
                return form == null || !WanaKana.IsKana(form.Text);
            }).ToList();
        }

        return (list, sentenceDeckIds.Distinct().ToList(), null);
    }

    /// <summary>
    /// Download the combined vocabulary of every tracked deck in a single status category as one merged file.
    /// </summary>
    [HttpPost("profile/{username}/media-list/{status:int}/download")]
    [AllowAnonymous]
    [EnableRateLimiting("download")]
    public async Task<IResult> DownloadMediaList(string username, int status, [FromBody] DeckDownloadRequest request)
    {
        var (userId, userName, allowed) = await ResolveMediaListAccessAsync(username);
        if (userId == null || !allowed)
            return Results.NotFound(new { message = "Media list not found" });

        var deckStatus = (DeckStatus)status;
        var (deckWords, sentenceDeckIds, error) = await ResolveMergedCategoryAsync(userId, deckStatus, request);
        if (error != null)
            return error;
        if (deckWords.Count == 0)
            return Results.NotFound(new { message = "No vocabulary to download." });

        var title = $"{userName} - {deckStatus}";

        if (request.Format == DeckFormat.Yomitan)
        {
            var yomitanBytes = await YomitanHelper.GenerateYomitanFrequencyDeckFromWords(contextFactory, deckWords, title);
            return Results.File(yomitanBytes, "application/zip", $"freq_{title}.zip");
        }

        var wordIds = deckWords.Select(dw => (long)dw.WordId).Distinct().ToList();
        var bytes = await downloadService.GenerateDownload(request, wordIds, title, deckWords, sentenceDeckIds);
        if (bytes == null)
            return Results.BadRequest();

        logger.LogInformation("User downloaded combined media list: Owner={Owner}, Status={Status}, Words={Words}, Format={Format}",
                              userId, deckStatus, deckWords.Count, request.Format);

        return request.Format switch
        {
            DeckFormat.Anki => Results.File(bytes, "application/x-binary", $"{title}.apkg"),
            DeckFormat.Csv => Results.File(bytes, "text/csv", $"{title}.csv"),
            DeckFormat.Txt or DeckFormat.TxtRepeated => Results.File(bytes, "text/plain", $"{title}.txt"),
            _ => Results.BadRequest()
        };
    }

    /// <summary>
    /// Counts the merged vocabulary of a status category after applying the given download filters.
    /// </summary>
    [HttpPost("profile/{username}/media-list/{status:int}/vocabulary-count")]
    [AllowAnonymous]
    public async Task<IResult> CountMediaListVocabulary(string username, int status, [FromBody] DeckDownloadRequest request)
    {
        var (userId, _, allowed) = await ResolveMediaListAccessAsync(username);
        if (userId == null || !allowed)
            return Results.NotFound(new { message = "Media list not found" });

        var (deckWords, _, error) = await ResolveMergedCategoryAsync(userId, (DeckStatus)status, request);
        if (error != null)
            return error;

        return Results.Ok(deckWords.Count);
    }

    /// <summary>
    /// Counts distinct vocabulary across a status category within a global frequency-rank range.
    /// </summary>
    [HttpGet("profile/{username}/media-list/{status:int}/vocabulary-count-frequency")]
    [AllowAnonymous]
    public async Task<IResult> CountMediaListFrequency(string username, int status, int minFrequency, int maxFrequency)
    {
        var (userId, _, allowed) = await ResolveMediaListAccessAsync(username);
        if (userId == null || !allowed)
            return Results.NotFound(new { message = "Media list not found" });

        var deckIds = await GetCategoryDisplayDeckIdsAsync(userId, (DeckStatus)status);
        if (deckIds.Count == 0)
            return Results.Ok(0);

        var count = await jitenContext.DeckWords.AsNoTracking()
                                      .Where(dw => deckIds.Contains(dw.DeckId) &&
                                                   jitenContext.WordFormFrequencies.Any(wff => wff.WordId == dw.WordId &&
                                                       wff.ReadingIndex == (short)dw.ReadingIndex &&
                                                       wff.FrequencyRank >= minFrequency && wff.FrequencyRank <= maxFrequency))
                                      .Select(dw => new { dw.WordId, dw.ReadingIndex })
                                      .Distinct()
                                      .CountAsync();

        return Results.Ok(count);
    }

    /// <summary>
    /// Counts distinct vocabulary across a status category filtered by per-deck occurrence thresholds.
    /// </summary>
    [HttpGet("profile/{username}/media-list/{status:int}/vocabulary-count-occurrences")]
    [AllowAnonymous]
    public async Task<IResult> CountMediaListOccurrences(string username, int status, int? minOccurrences = null, int? maxOccurrences = null)
    {
        var (userId, _, allowed) = await ResolveMediaListAccessAsync(username);
        if (userId == null || !allowed)
            return Results.NotFound(new { message = "Media list not found" });

        var deckIds = await GetCategoryDisplayDeckIdsAsync(userId, (DeckStatus)status);
        if (deckIds.Count == 0)
            return Results.Ok(0);

        var query = jitenContext.DeckWords.AsNoTracking().Where(dw => deckIds.Contains(dw.DeckId));
        if (minOccurrences.HasValue)
            query = query.Where(dw => dw.Occurrences >= minOccurrences.Value);
        if (maxOccurrences.HasValue)
            query = query.Where(dw => dw.Occurrences <= maxOccurrences.Value);

        var count = await query.Select(dw => new { dw.WordId, dw.ReadingIndex }).Distinct().CountAsync();
        return Results.Ok(count);
    }

    /// <summary>
    /// Marks the combined vocabulary of a status category as mastered or blacklisted in the caller's tracker.
    /// </summary>
    [HttpPost("profile/{username}/media-list/{status:int}/learn")]
    [Authorize]
    [EnableRateLimiting("download")]
    public async Task<IResult> LearnMediaList(string username, int status, [FromBody] DeckLearnRequest request)
    {
        var state = request.VocabularyState?.ToLowerInvariant();
        if (state is not ("mastered" or "blacklisted"))
            return Results.BadRequest("VocabularyState must be 'mastered' or 'blacklisted'.");

        var (userId, _, allowed) = await ResolveMediaListAccessAsync(username);
        if (userId == null || !allowed)
            return Results.NotFound(new { message = "Media list not found" });

        var (deckWords, _, error) = await ResolveMergedCategoryAsync(userId, (DeckStatus)status, request.ToDownloadRequest());
        if (error != null)
            return error;

        var entities = deckWords
                       .Select(x => new DeckWord { WordId = x.WordId, ReadingIndex = x.ReadingIndex, Occurrences = x.Occurrences })
                       .ToList();

        var applied = (state == "mastered"
            ? await userService.AddKnownWords(entities, countAsNewlyLearned: request.CountAsNewlyLearned)
            : await userService.BlacklistWords(entities)).Inserted;

        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userService.UserId!);
        await userContext.SaveChangesAsync();

        return Results.Ok(new { applied, state });
    }

    /// <summary>
    /// Resolves a username to its user id and whether the caller may view that user's media list.
    /// Non-owners need both the profile and the media list to be public.
    /// </summary>
    private async Task<(string? UserId, string? UserName, bool Allowed)> ResolveMediaListAccessAsync(string username)
    {
        var user = await userContext.Users
                                    .AsNoTracking()
                                    .Where(u => u.NormalizedUserName == username.ToUpperInvariant())
                                    .Select(u => new { u.Id, u.UserName })
                                    .FirstOrDefaultAsync();

        if (user == null)
            return (null, null, false);

        if (userService.UserId == user.Id)
            return (user.Id, user.UserName, true);

        var flags = await userContext.UserProfiles
                                     .AsNoTracking()
                                     .Where(p => p.UserId == user.Id)
                                     .Select(p => new { p.IsPublic, p.IsMediaListPublic })
                                     .FirstOrDefaultAsync();

        return (user.Id, user.UserName, flags is { IsPublic: true, IsMediaListPublic: true });
    }

    /// <summary>
    /// Returns the display deck ids of a user's tracked decks in a single status category.
    /// </summary>
    private async Task<List<int>> GetCategoryDisplayDeckIdsAsync(string userId, DeckStatus status) =>
        (await BuildMediaListAsync(userId)).Where(e => e.Status == status).Select(e => e.Display.DeckId).ToList();

    /// <summary>
    /// Builds a user's media list: one entry per display deck (child decks collapsed to their parent series),
    /// with the most representative status and a favourite flag. Used by both the list and download endpoints.
    /// </summary>
    private async Task<List<(Deck Display, DeckStatus Status, bool IsFavourite)>> BuildMediaListAsync(string userId)
    {
        var prefs = await userContext.UserDeckPreferences
                                     .AsNoTracking()
                                     .Where(p => p.UserId == userId && p.Status != DeckStatus.None)
                                     .Select(p => new { p.DeckId, p.Status, p.IsFavourite })
                                     .ToListAsync();

        if (prefs.Count == 0)
            return new List<(Deck, DeckStatus, bool)>();

        var prefDeckIds = prefs.Select(p => p.DeckId).ToList();

        var parentByDeck = await jitenContext.Decks
                                             .AsNoTracking()
                                             .Where(d => prefDeckIds.Contains(d.DeckId))
                                             .Select(d => new { d.DeckId, d.ParentDeckId })
                                             .ToDictionaryAsync(d => d.DeckId, d => d.ParentDeckId);

        static int Rank(DeckStatus s) => s switch
                                         {
                                             DeckStatus.Completed => 4,
                                             DeckStatus.Ongoing => 3,
                                             DeckStatus.Planning => 2,
                                             DeckStatus.Dropped => 1,
                                             _ => 0
                                         };

        var agg = new Dictionary<int, (DeckStatus Status, bool Own, bool Fav)>();
        foreach (var p in prefs)
        {
            if (!parentByDeck.TryGetValue(p.DeckId, out var parentId)) continue;
            var displayId = parentId ?? p.DeckId;
            var isOwn = displayId == p.DeckId;

            if (!agg.TryGetValue(displayId, out var cur))
            {
                agg[displayId] = (p.Status, isOwn, p.IsFavourite);
                continue;
            }

            var statusValue = cur.Status;
            if (isOwn && !cur.Own)
                statusValue = p.Status;
            else if (isOwn == cur.Own && Rank(p.Status) > Rank(cur.Status))
                statusValue = p.Status;

            agg[displayId] = (statusValue, cur.Own || isOwn, cur.Fav || p.IsFavourite);
        }

        var displayIds = agg.Keys.ToList();
        var displayDecks = await jitenContext.Decks
                                             .AsNoTracking()
                                             .Include(d => d.Children)
                                             .Include(d => d.Links)
                                             .Include(d => d.Titles)
                                             .Include(d => d.DeckGenres)
                                             .Include(d => d.DeckTags)
                                             .ThenInclude(dt => dt.Tag)
                                             .Include(d => d.DeckDifficulty)
                                             .Where(d => displayIds.Contains(d.DeckId))
                                             .ToListAsync();

        return displayDecks
               .Select(d => (Display: d, agg[d.DeckId].Status, agg[d.DeckId].Fav))
               .ToList();
    }

    /// <summary>
    /// Get accomplishments for a user by username. Requires own profile or public profile.
    /// Returns 404 if user doesn't exist or profile is private.
    /// </summary>
    [HttpGet("profile/{username}/accomplishments")]
    [AllowAnonymous]
    public async Task<IResult> GetUserAccomplishmentsByUsername(string username)
    {
        var user = await userContext.Users
                                    .AsNoTracking()
                                    .FirstOrDefaultAsync(u => u.NormalizedUserName == username.ToUpperInvariant());

        if (user == null)
            return Results.NotFound(new { message = "Profile not found" });

        var currentUserId = userService.UserId;
        var isOwnProfile = currentUserId == user.Id;

        if (!isOwnProfile)
        {
            var profile = await userContext.UserProfiles
                                           .AsNoTracking()
                                           .FirstOrDefaultAsync(p => p.UserId == user.Id);

            if (profile == null || !profile.IsPublic)
                return Results.NotFound(new { message = "Profile not found" });
        }

        var accomplishments = await userContext.UserAccomplishments
                                               .AsNoTracking()
                                               .Where(ua => ua.UserId == user.Id)
                                               .OrderBy(ua => ua.MediaType)
                                               .ToListAsync();

        return Results.Ok(accomplishments);
    }

    /// <summary>
    /// Get aggregated vocabulary across all completed decks by username.
    /// Requires own profile or public profile.
    /// Returns 404 if user doesn't exist or profile is private.
    /// </summary>
    [HttpGet("profile/{username}/accomplishments/vocabulary")]
    [AllowAnonymous]
    public async Task<ActionResult<PaginatedResponse<AccomplishmentVocabularyDto>>> GetAccomplishmentVocabularyByUsername(
        string username,
        [FromQuery] Jiten.Core.Data.MediaType? mediaType = null,
        [FromQuery] int offset = 0,
        [FromQuery] int pageSize = 100,
        [FromQuery] string sortBy = "occurrences",
        [FromQuery] bool descending = true,
        [FromQuery] string displayFilter = "all",
        [FromQuery] string? search = null,
        [FromQuery] string? pos = null,
        [FromQuery] string? excludePos = null,
        [FromQuery] bool hideKanaOnly = false)
    {
        var user = await userContext.Users
                                    .AsNoTracking()
                                    .FirstOrDefaultAsync(u => u.NormalizedUserName == username.ToUpperInvariant());

        if (user == null)
            return NotFound(new { message = "Profile not found" });

        var targetUserId = user.Id;
        var currentUserId = userService.UserId;
        var isOwnProfile = currentUserId == targetUserId;

        if (!isOwnProfile)
        {
            var profile = await userContext.UserProfiles
                                           .AsNoTracking()
                                           .FirstOrDefaultAsync(p => p.UserId == targetUserId);

            if (profile == null || !profile.IsPublic)
                return NotFound(new { message = "Profile not found" });
        }

        pageSize = Math.Clamp(pageSize, 1, 100);

        // Get all completed deck IDs for the user
        var userCompletedDeckIds = await userContext.UserDeckPreferences
                                                    .AsNoTracking()
                                                    .Where(udp => udp.UserId == targetUserId && udp.Status == DeckStatus.Completed)
                                                    .Select(udp => udp.DeckId)
                                                    .ToListAsync();

        // Load completed decks with parent relationship
        var allCompletedDecks = await jitenContext.Decks
                                                  .AsNoTracking()
                                                  .Where(d => userCompletedDeckIds.Contains(d.DeckId))
                                                  .Select(d => new { d.DeckId, d.ParentDeckId, d.MediaType })
                                                  .ToListAsync();

        // Build effective deck set: include parents, and children only if their parent is NOT completed
        var completedParentIds = allCompletedDecks
                                 .Where(d => d.ParentDeckId == null)
                                 .Select(d => d.DeckId)
                                 .ToHashSet();

        var effectiveDecks = allCompletedDecks
                             .Where(d => d.ParentDeckId == null || !completedParentIds.Contains(d.ParentDeckId.Value))
                             .ToList();

        // Apply media type filter if provided
        if (mediaType.HasValue)
        {
            effectiveDecks = effectiveDecks.Where(d => d.MediaType == mediaType.Value).ToList();
        }

        var completedDeckIds = effectiveDecks.Select(d => d.DeckId).ToList();

        if (completedDeckIds.Count == 0)
        {
            return Ok(new PaginatedResponse<AccomplishmentVocabularyDto>(
                                                                         new AccomplishmentVocabularyDto { Words = [] }, 0, pageSize,
                                                                         offset));
        }

        // Aggregate vocabulary across completed decks
        var aggregatedWordsQuery = jitenContext.DeckWords
                                               .AsNoTracking()
                                               .Where(dw => completedDeckIds.Contains(dw.DeckId))
                                               .GroupBy(dw => new { dw.WordId, dw.ReadingIndex })
                                               .Select(g => new AggregatedWord
                                                            {
                                                                WordId = g.Key.WordId, ReadingIndex = g.Key.ReadingIndex,
                                                                TotalOccurrences = g.Sum(dw => dw.Occurrences)
                                                            });

        // Materialise the aggregated words for filtering and sorting
        var allAggregatedWords = await aggregatedWordsQuery.ToListAsync();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var matchingWordIds = await SearchHelper.ResolveSearchWordIds(jitenContext, search);
            allAggregatedWords = allAggregatedWords.Where(aw => matchingWordIds.Contains(aw.WordId)).ToList();
        }

        var posMatchIds = await VocabularyFilterHelper.GetPosFilteredWordIds(
            jitenContext, pos, allAggregatedWords.Select(aw => aw.WordId));
        if (posMatchIds != null)
            allAggregatedWords = allAggregatedWords.Where(aw => posMatchIds.Contains(aw.WordId)).ToList();

        var posExcludeIds = await VocabularyFilterHelper.GetPosFilteredWordIds(
            jitenContext, excludePos, allAggregatedWords.Select(aw => aw.WordId));
        if (posExcludeIds != null)
            allAggregatedWords = allAggregatedWords.Where(aw => !posExcludeIds.Contains(aw.WordId)).ToList();

        if (hideKanaOnly)
        {
            var kanaFormKeys = await WordFormHelper.GetKanaFormKeys(
                jitenContext, allAggregatedWords.Select(aw => aw.WordId).Distinct());
            if (kanaFormKeys.Count > 0)
                allAggregatedWords = allAggregatedWords
                    .Where(aw => !kanaFormKeys.Contains(WordFormHelper.EncodeWordKey(aw.WordId, aw.ReadingIndex)))
                    .ToList();
        }

        // Apply displayFilter if authenticated and filter is not "all"
        if (userService.IsAuthenticated && !string.IsNullOrEmpty(displayFilter) && displayFilter != "all")
        {
            var wordKeys = allAggregatedWords.Select(aw => (aw.WordId, aw.ReadingIndex)).ToList();
            var filterKnownStates = await userService.GetKnownWordsState(wordKeys);

            var distinctWordIds = wordKeys.Select(k => k.WordId).Distinct().ToList();
            var fsrsStates = await userContext.FsrsCards
                                              .AsNoTracking()
                                              .Where(uk => uk.UserId == userService.UserId && distinctWordIds.Contains(uk.WordId))
                                              .Select(uk => new { uk.WordId, uk.ReadingIndex, uk.State })
                                              .ToDictionaryAsync(uk => (uk.WordId, uk.ReadingIndex), uk => uk.State);

            allAggregatedWords = allAggregatedWords.Where(aw =>
            {
                var key = (aw.WordId, aw.ReadingIndex);
                var knownState = filterKnownStates.GetValueOrDefault(key, [KnownState.New]);

                return displayFilter switch
                {
                    "known" => !knownState.Contains(KnownState.New),
                    "young" => knownState.Contains(KnownState.Young),
                    "mature" => knownState.Contains(KnownState.Mature),
                    "mastered" => knownState.Contains(KnownState.Mastered),
                    "blacklisted" => knownState.Contains(KnownState.Blacklisted),
                    "unknown" => !fsrsStates.ContainsKey(key) && knownState.Contains(KnownState.New),
                    _ => true
                };
            }).ToList();
        }

        int totalCount = allAggregatedWords.Count;

        // Apply sorting
        IEnumerable<AggregatedWord> sortedWords;
        if (sortBy.Equals("globalFreq", StringComparison.OrdinalIgnoreCase))
        {
            var freqWordIds = allAggregatedWords.Select(aw => aw.WordId).Distinct().ToList();
            var sortFormFreqs = await WordFormHelper.LoadWordFormFrequencies(jitenContext, freqWordIds);

            sortedWords = descending
                ? allAggregatedWords.OrderByDescending(aw =>
                                                           sortFormFreqs.TryGetValue((aw.WordId, (short)aw.ReadingIndex), out var wff)
                                                               ? wff.FrequencyRank
                                                               : 0)
                : allAggregatedWords.OrderBy(aw =>
                                                 sortFormFreqs.TryGetValue((aw.WordId, (short)aw.ReadingIndex), out var wff)
                                                     ? wff.FrequencyRank
                                                     : int.MaxValue);
        }
        else
        {
            // Default to occurrences
            sortedWords = descending
                ? allAggregatedWords.OrderByDescending(w => w.TotalOccurrences)
                : allAggregatedWords.OrderBy(w => w.TotalOccurrences);
        }

        // Apply pagination
        var pagedWords = sortedWords.Skip(offset).Take(pageSize).ToList();

        var wordIds = pagedWords.Select(w => w.WordId).ToList();

        var jmdictWords = await jitenContext.JMDictWords
                                            .AsNoTracking()
                                            .Where(w => wordIds.Contains(w.WordId))
                                            .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
                                            .ToListAsync();

        var jmdictLookup = jmdictWords.ToDictionary(w => w.WordId);

        var accForms2 = await WordFormHelper.LoadWordForms(jitenContext, wordIds);
        var accFormFreqs2 = await WordFormHelper.LoadWordFormFrequencies(jitenContext, wordIds);

        var wordDtos = new List<WordDto>();
        foreach (var pw in pagedWords)
        {
            if (!jmdictLookup.TryGetValue(pw.WordId, out var jmWord))
                continue;

            var mainForm = accForms2.GetValueOrDefault((pw.WordId, (short)pw.ReadingIndex));
            if (mainForm == null)
                continue;

            var allFormsForWord = accForms2.Where(f => f.Key.Item1 == pw.WordId)
                                           .OrderBy(f => f.Key.Item2)
                                           .Select(f => f.Value)
                                           .ToList();

            var alternativeReadings = allFormsForWord
                                            .Where(f => f.ReadingIndex != pw.ReadingIndex)
                                            .Select(f => WordFormHelper.ToPlainFormDto(f, accFormFreqs2.GetValueOrDefault((f.WordId, f.ReadingIndex))))
                                            .ToList();

            var mainReading = WordFormHelper.ToPlainFormDto(mainForm, accFormFreqs2.GetValueOrDefault((pw.WordId, (short)pw.ReadingIndex)));

            wordDtos.Add(new WordDto
                         {
                             WordId = jmWord.WordId, MainReading = mainReading, AlternativeReadings = alternativeReadings,
                             PartsOfSpeech = jmWord.PartsOfSpeech.ToHumanReadablePartsOfSpeech(),
                             Definitions = jmWord.Definitions.ToDefinitionDtos(), Occurrences = pw.TotalOccurrences,
                             PitchAccents = jmWord.PitchAccents
                         });
        }

        var knownStates = await userService.GetKnownWordsState(
                                                               wordDtos.Select(w => (w.WordId, w.MainReading.ReadingIndex)).ToList());
        wordDtos.ApplyKnownWordsState(knownStates);

        var dto = new AccomplishmentVocabularyDto { Words = wordDtos };

        return Ok(new PaginatedResponse<AccomplishmentVocabularyDto>(dto, totalCount, pageSize, offset));
    }

    #endregion

    #region Study Heatmap

    [HttpGet("profile/{username}/study-heatmap")]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "Get study activity heatmap and streak for a user profile")]
    public async Task<IResult> GetStudyHeatmapByUsername(string username, [FromQuery] int? year = null)
    {
        var user = await userContext.Users
                                    .AsNoTracking()
                                    .FirstOrDefaultAsync(u => u.NormalizedUserName == username.ToUpperInvariant());

        if (user == null)
            return Results.NotFound(new { message = "Profile not found" });

        var targetUserId = user.Id;
        var currentUserId = userService.UserId;
        var isOwnProfile = currentUserId == targetUserId;

        if (!isOwnProfile)
        {
            var profile = await userContext.UserProfiles
                                           .AsNoTracking()
                                           .FirstOrDefaultAsync(p => p.UserId == targetUserId);

            if (profile is not { IsPublic: true })
                return Results.NotFound(new { message = "Profile not found" });
        }

        var fsrsSettingsJson = await userContext.UserFsrsSettings.AsNoTracking()
                                               .Where(s => s.UserId == targetUserId)
                                               .Select(s => s.SettingsJson)
                                               .FirstOrDefaultAsync();

        string? timezone = null;
        if (!string.IsNullOrEmpty(fsrsSettingsJson) && fsrsSettingsJson != "{}")
        {
            try { timezone = JsonSerializer.Deserialize<StudySettingsDto>(fsrsSettingsJson)?.Timezone; }
            catch (JsonException) { }
        }

        double offsetHours = 0;
        if (!string.IsNullOrEmpty(timezone))
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
                offsetHours = tz.GetUtcOffset(DateTime.UtcNow).TotalHours;
            }
            catch (TimeZoneNotFoundException) { }
        }

        var targetYear = year ?? DateTime.UtcNow.AddHours(offsetHours).Year;

        List<HeatmapDayDto> days;
        List<DateTime> allReviewDates;

        var rollup = await ReviewRollupHelper.TryLoadAsync(userContext, targetUserId);
        if (rollup != null)
        {
            days = rollup
                   .Where(d => d.LocalDate.Year == targetYear)
                   .Select(d => new HeatmapDayDto
                                {
                                    Date = d.LocalDate,
                                    ReviewCount = d.ReviewCount,
                                    CorrectCount = d.CorrectCount
                                })
                   .ToList();

            allReviewDates = rollup.Select(d => d.LocalDate.ToDateTime(TimeOnly.MinValue))
                                   .OrderByDescending(d => d)
                                   .ToList();
        }
        else
        {
            var tz = FsrsSettingsHelper.ResolveTimeZone(timezone);
            var byDay = (await CardArchiveService.LoadAllReviewsAsync(userContext, targetUserId))
                        .GroupBy(r => ReviewRollupHelper.LocalDateOf(r.ReviewUtc, tz))
                        .ToList();

            days = byDay
                   .Where(g => g.Key.Year == targetYear)
                   .OrderBy(g => g.Key)
                   .Select(g => new HeatmapDayDto
                   {
                       Date = g.Key,
                       ReviewCount = g.Count(),
                       CorrectCount = g.Count(r => r.Rating != FsrsRating.Again)
                   })
                   .ToList();

            allReviewDates = byDay
                             .Select(g => g.Key.ToDateTime(TimeOnly.MinValue))
                             .OrderByDescending(d => d)
                             .ToList();
        }

        var today = DateTime.UtcNow.AddHours(offsetHours).Date;
        var (currentStreak, longestStreak) = ComputeStreaks(allReviewDates, today);

        return Results.Ok(new StudyHeatmapResponse
        {
            Year = targetYear,
            Days = days,
            CurrentStreak = currentStreak,
            LongestStreak = longestStreak,
            TotalReviewDays = allReviewDates.Count,
            TotalReviews = days.Sum(d => d.ReviewCount)
        });
    }

    private static (int currentStreak, int longestStreak) ComputeStreaks(List<DateTime> sortedDatesDesc, DateTime today)
    {
        if (sortedDatesDesc.Count == 0)
            return (0, 0);

        // Current streak: count consecutive days from today/yesterday backwards
        var currentStreak = 0;
        var checkDate = today;

        // Allow grace period — if no review today, start from yesterday
        if (sortedDatesDesc[0].Date != today)
        {
            if (sortedDatesDesc[0].Date == today.AddDays(-1))
                checkDate = today.AddDays(-1);
            else
                goto longestOnly;
        }

        foreach (var date in sortedDatesDesc)
        {
            if (date.Date == checkDate)
            {
                currentStreak++;
                checkDate = checkDate.AddDays(-1);
            }
            else if (date.Date < checkDate)
                break;
        }

        longestOnly:

        // Longest streak: walk all dates
        var longest = 0;
        var streak = 1;
        for (var i = 1; i < sortedDatesDesc.Count; i++)
        {
            if (sortedDatesDesc[i - 1].Date.AddDays(-1) == sortedDatesDesc[i].Date)
                streak++;
            else
            {
                longest = Math.Max(longest, streak);
                streak = 1;
            }
        }
        longest = Math.Max(longest, streak);

        return (currentStreak, Math.Max(longest, currentStreak));
    }

    #endregion

    #region Profile Vocabulary Stats

    /// <summary>
    /// Get aggregate known-word counts for a user profile.
    /// </summary>
    [HttpGet("profile/{username}/vocabulary-stats")]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "Get aggregate vocabulary knowledge counts for a user profile")]
    public async Task<IResult> GetProfileVocabularyStats(string username)
    {
        var user = await userContext.Users
                                    .AsNoTracking()
                                    .FirstOrDefaultAsync(u => u.NormalizedUserName == username.ToUpperInvariant());

        if (user == null)
            return Results.NotFound(new { message = "Profile not found" });

        var targetUserId = user.Id;
        var currentUserId = userService.UserId;
        var isOwnProfile = currentUserId == targetUserId;

        if (!isOwnProfile)
        {
            var profile = await userContext.UserProfiles
                                           .AsNoTracking()
                                           .FirstOrDefaultAsync(p => p.UserId == targetUserId);

            if (profile is not { IsPublic: true })
                return Results.NotFound(new { message = "Profile not found" });
        }

        // Counting loads every FSRS card for the user; this endpoint is anonymous and crawlable.
        var redisDb = redis.GetDatabase();
        var cacheKey = $"jiten:profile-vocab:{targetUserId}";

        var cached = await redisDb.StringGetAsync(cacheKey);
        if (!cached.IsNullOrEmpty)
        {
            var hit = JsonSerializer.Deserialize<ProfileVocabularyStatsDto>(cached!);
            if (hit != null) return Results.Ok(hit);
        }

        var amounts = await ComputeKnownWordAmountAsync(targetUserId);
        var dto = new ProfileVocabularyStatsDto
                  {
                      Young = amounts.Young,
                      Mature = amounts.Mature,
                      Mastered = amounts.Mastered,
                      WordSetMastered = amounts.WordSetMastered
                  };

        await redisDb.StringSetAsync(cacheKey, JsonSerializer.Serialize(dto), expiry: TimeSpan.FromMinutes(5));

        return Results.Ok(dto);
    }

    #endregion

    #region Kanji Grid

    /// <summary>
    /// Get kanji grid data for a user profile.
    /// Returns all kanji ordered by frequency with user's scores.
    /// </summary>
    [HttpGet("profile/{username}/kanji-grid")]
    [AllowAnonymous]
    public async Task<IResult> GetKanjiGridByUsername(
        string username,
        [FromQuery] bool onlySeen = false)
    {
        var user = await userContext.Users
                                    .AsNoTracking()
                                    .FirstOrDefaultAsync(u => u.NormalizedUserName == username.ToUpperInvariant());

        if (user == null)
            return Results.NotFound(new { message = "Profile not found" });

        var targetUserId = user.Id;
        var currentUserId = userService.UserId;
        var isOwnProfile = currentUserId == targetUserId;

        if (!isOwnProfile)
        {
            var profile = await userContext.UserProfiles
                                           .AsNoTracking()
                                           .FirstOrDefaultAsync(p => p.UserId == targetUserId);

            if (profile is not { IsPublic: true })
                return Results.NotFound(new { message = "Profile not found" });
        }

        // Get all kanji ordered by frequency
        var redisDb = redis.GetDatabase();
        const string cacheKey = "jiten:kanji-grid:all-kanji";

        var cached = await redisDb.StringGetAsync(cacheKey);
        List<CachedKanjiInfo>? allKanji;

        if (!cached.IsNullOrEmpty)
        {
            allKanji = JsonSerializer.Deserialize<List<CachedKanjiInfo>>(cached!);
        }
        else
        {
            allKanji = await jitenContext.Kanjis
                                         .AsNoTracking()
                                         .OrderBy(k => k.FrequencyRank ?? int.MaxValue)
                                         .Select(k => new CachedKanjiInfo(k.Character, k.FrequencyRank, k.JlptLevel, k.Grade, k.StrokeCount))
                                         .ToListAsync();

            var json = JsonSerializer.Serialize(allKanji);
            await redisDb.StringSetAsync(cacheKey, json, expiry: TimeSpan.FromHours(1));
        }

        var userGrid = await userContext.UserKanjiGrids
                                        .AsNoTracking()
                                        .FirstOrDefaultAsync(ukg => ukg.UserId == targetUserId);

        var userScores = userGrid?.GetKanjiScoresOnce() ?? new Dictionary<string, KanjiScoreEntry>();

        var kanjiData = allKanji!
                        .Where(k => !onlySeen || userScores.ContainsKey(k.Character))
                        .Select(k =>
                        {
                            userScores.TryGetValue(k.Character, out var entry);
                            var dto = new KanjiGridItemDto
                            {
                                Character = k.Character,
                                FrequencyRank = k.FrequencyRank,
                                JlptLevel = k.JlptLevel,
                                Grade = k.Grade,
                                StrokeCount = k.StrokeCount,
                                Score = entry?.Score ?? 0,
                                WordCount = entry?.WordCount ?? 0
                            };
                            if (entry?.Readings != null)
                            {
                                dto.Readings = entry.Readings
                                    .Select(r => new KanjiGridReadingDto
                                    {
                                        Reading = r.Reading, Known = r.Known,
                                        Required = r.Required, Weight = r.Weight
                                    })
                                    .ToList();
                            }
                            return dto;
                        })
                        .ToList();

        return Results.Ok(new KanjiGridResponseDto
                          {
                              Kanji = kanjiData, TotalKanjiCount = allKanji!.Count,
                              SeenKanjiCount = userScores.Count, LastComputedAt = userGrid?.LastComputedAt
                          });
    }

    private record CachedKanjiInfo(string Character, int? FrequencyRank, short? JlptLevel, short? Grade, short StrokeCount);
    private record ReadingFrequencyResult(int WordId, short ReadingIndex, int FrequencyRank);

    private static int StateRank(KnownState s) => s switch
    {
        KnownState.Mastered => 4,
        KnownState.Blacklisted => 3,
        KnownState.Mature => 2,
        KnownState.Young => 1,
        _ => 0
    };

    private static KnownState? ComputeEffectiveCategory(FsrsState state, DateTime due, DateTime? lastReview, DateTime now)
    {
        switch (state)
        {
            case FsrsState.Mastered: return KnownState.Mastered;
            case FsrsState.Blacklisted: return KnownState.Blacklisted;
        }

        if (lastReview == null)
            return null;

        var interval = (due - lastReview.Value).TotalDays;
        return interval < 21 ? KnownState.Young : KnownState.Mature;
    }

    #endregion

    #region Custom Example Sentences

    private static readonly System.Text.RegularExpressions.Regex MarkerRegex =
        new(@"\*\*[^*]+\*\*", System.Text.RegularExpressions.RegexOptions.Compiled);

    [HttpGet("example-sentences/{wordId}/{readingIndex}")]
    public async Task<IResult> GetCustomExampleSentences(int wordId, byte readingIndex)
    {
        var userId = userService.UserId;
        if (userId == null) return Results.Unauthorized();

        var sentences = await userContext.UserExampleSentences
            .AsNoTracking()
            .Where(e => e.UserId == userId && e.WordId == wordId && e.ReadingIndex == readingIndex)
            .OrderBy(e => e.SortOrder)
            .Select(e => new UserExampleSentenceDto
            {
                UserExampleSentenceId = e.UserExampleSentenceId,
                Text = e.Text,
                Source = e.Source,
                SortOrder = e.SortOrder
            })
            .ToListAsync();

        return Results.Ok(sentences);
    }

    [HttpPost("example-sentences/{wordId}/{readingIndex}")]
    public Task<IResult> AddCustomExampleSentence(int wordId, byte readingIndex, [FromBody] UpsertUserExampleSentenceRequest request)
        => CreateUserExampleSentence(wordId, readingIndex, request.Text, request.Source);

    [HttpPost("example-sentences/{wordId}/{readingIndex}/favourite")]
    public Task<IResult> FavouriteExampleSentence(int wordId, byte readingIndex, [FromBody] FavouriteExampleSentenceRequest request)
        => CreateUserExampleSentence(wordId, readingIndex, request.Text, request.Source);

    private async Task<IResult> CreateUserExampleSentence(int wordId, byte readingIndex, string text, string? source)
    {
        var userId = userService.UserId;
        if (userId == null) return Results.Unauthorized();
        if (!MarkerRegex.IsMatch(text)) return Results.BadRequest("Text must contain at least one **word** marker.");
        if (text.Length > 150) return Results.BadRequest("Text must be 150 characters or fewer.");
        if (source?.Length > 150) return Results.BadRequest("Source must be 150 characters or fewer.");

        var saved = await userContext.UserExampleSentences
            .Where(e => e.UserId == userId && e.WordId == wordId && e.ReadingIndex == readingIndex)
            .ToListAsync();

        // Clients only track the starred state in memory, so a reload re-offers a sentence that is
        // already saved; saving it again must not spend another slot.
        var sentence = saved.FirstOrDefault(e => e.Text == text);

        if (sentence == null)
        {
            var limits = await userLimits.GetLimitsAsync(userId);
            if (saved.Count >= limits.CustomSentencesPerWord) return Results.BadRequest(LimitMessages.CustomSentencesPerWord(limits));

            sentence = new UserExampleSentence
            {
                UserId = userId,
                WordId = wordId,
                ReadingIndex = readingIndex,
                Text = text,
                Source = source,
                SortOrder = (byte)saved.Count
            };

            userContext.UserExampleSentences.Add(sentence);
            await userContext.SaveChangesAsync();
        }

        return Results.Ok(new UserExampleSentenceDto
        {
            UserExampleSentenceId = sentence.UserExampleSentenceId,
            Text = sentence.Text,
            Source = sentence.Source,
            SortOrder = sentence.SortOrder
        });
    }

    [HttpPut("example-sentences/{id}")]
    public async Task<IResult> UpdateCustomExampleSentence(int id, [FromBody] UpsertUserExampleSentenceRequest request)
    {
        var userId = userService.UserId;
        if (userId == null) return Results.Unauthorized();
        if (!MarkerRegex.IsMatch(request.Text)) return Results.BadRequest("Text must contain at least one **word** marker.");
        if (request.Text.Length > 150) return Results.BadRequest("Text must be 150 characters or fewer.");
        if (request.Source?.Length > 150) return Results.BadRequest("Source must be 150 characters or fewer.");

        var sentence = await userContext.UserExampleSentences
            .FirstOrDefaultAsync(e => e.UserExampleSentenceId == id && e.UserId == userId);
        if (sentence == null) return Results.NotFound();

        sentence.Text = request.Text;
        sentence.Source = request.Source;
        await userContext.SaveChangesAsync();

        return Results.Ok(new UserExampleSentenceDto
        {
            UserExampleSentenceId = sentence.UserExampleSentenceId,
            Text = sentence.Text,
            Source = sentence.Source,
            SortOrder = sentence.SortOrder
        });
    }

    [HttpDelete("example-sentences/{id}")]
    public async Task<IResult> DeleteCustomExampleSentence(int id)
    {
        var userId = userService.UserId;
        if (userId == null) return Results.Unauthorized();

        var sentence = await userContext.UserExampleSentences
            .FirstOrDefaultAsync(e => e.UserExampleSentenceId == id && e.UserId == userId);
        if (sentence == null) return Results.NotFound();

        var wordId = sentence.WordId;
        var readingIndex = sentence.ReadingIndex;

        userContext.UserExampleSentences.Remove(sentence);
        await userContext.SaveChangesAsync();

        var remaining = await userContext.UserExampleSentences
            .Where(e => e.UserId == userId && e.WordId == wordId && e.ReadingIndex == readingIndex)
            .OrderBy(e => e.SortOrder)
            .ToListAsync();

        for (byte i = 0; i < remaining.Count; i++)
            remaining[i].SortOrder = i;

        await userContext.SaveChangesAsync();

        return Results.Ok(new { deleted = true });
    }

    [HttpPost("example-sentences/batch")]
    public async Task<IResult> GetCustomExampleSentencesBatch([FromBody] List<CardExamplesRequest.WordPair> pairs)
    {
        var userId = userService.UserId;
        if (userId == null) return Results.Unauthorized();
        if (pairs is not { Count: > 0 and <= 20 }) return Results.BadRequest();

        var wordIds = pairs.Select(p => p.WordId).Distinct().ToList();

        var sentences = await userContext.UserExampleSentences
            .AsNoTracking()
            .Where(e => e.UserId == userId && wordIds.Contains(e.WordId))
            .OrderBy(e => e.SortOrder)
            .ToListAsync();

        var result = new Dictionary<string, List<UserExampleSentenceDto>>();
        foreach (var pair in pairs)
        {
            var key = $"{pair.WordId}-{pair.ReadingIndex}";
            var matching = sentences
                .Where(s => s.WordId == pair.WordId && s.ReadingIndex == pair.ReadingIndex)
                .Select(s => new UserExampleSentenceDto
                {
                    UserExampleSentenceId = s.UserExampleSentenceId,
                    Text = s.Text,
                    Source = s.Source,
                    SortOrder = s.SortOrder
                })
                .ToList();
            if (matching.Count > 0)
                result[key] = matching;
        }

        return Results.Ok(result);
    }

    #endregion

    #region Custom Meanings

    private const int CustomMeaningMaxLength = 500;

    [HttpGet("custom-meanings/{wordId}")]
    public async Task<IResult> GetCustomMeaning(int wordId)
    {
        var userId = userService.UserId;
        if (userId == null) return Results.Unauthorized();

        var meaning = await userContext.UserCustomMeanings
            .AsNoTracking()
            .Where(e => e.UserId == userId && e.WordId == wordId)
            .Select(e => new UserCustomMeaningDto { WordId = e.WordId, Text = e.Text })
            .FirstOrDefaultAsync();

        return Results.Ok(meaning);
    }

    [HttpPut("custom-meanings/{wordId}")]
    public async Task<IResult> UpsertCustomMeaning(int wordId, [FromBody] UpsertUserCustomMeaningRequest request)
    {
        var userId = userService.UserId;
        if (userId == null) return Results.Unauthorized();

        var text = request.Text?.Trim() ?? "";
        if (text.Length == 0) return Results.BadRequest("Meaning must not be empty.");
        if (text.Length > CustomMeaningMaxLength) return Results.BadRequest($"Meaning must be {CustomMeaningMaxLength} characters or fewer.");

        var meaning = await userContext.UserCustomMeanings
            .FirstOrDefaultAsync(e => e.UserId == userId && e.WordId == wordId);

        if (meaning == null)
        {
            meaning = new UserCustomMeaning { UserId = userId, WordId = wordId, Text = text };
            userContext.UserCustomMeanings.Add(meaning);
        }
        else
        {
            meaning.Text = text;
            meaning.UpdatedAt = DateTime.UtcNow;
        }

        await userContext.SaveChangesAsync();

        return Results.Ok(new UserCustomMeaningDto { WordId = meaning.WordId, Text = meaning.Text });
    }

    [HttpDelete("custom-meanings/{wordId}")]
    public async Task<IResult> DeleteCustomMeaning(int wordId)
    {
        var userId = userService.UserId;
        if (userId == null) return Results.Unauthorized();

        var meaning = await userContext.UserCustomMeanings
            .FirstOrDefaultAsync(e => e.UserId == userId && e.WordId == wordId);
        if (meaning == null) return Results.NotFound();

        userContext.UserCustomMeanings.Remove(meaning);
        await userContext.SaveChangesAsync();

        return Results.Ok(new { deleted = true });
    }

    [HttpPost("custom-meanings/batch")]
    public async Task<IResult> GetCustomMeaningsBatch([FromBody] List<int> wordIds)
    {
        var userId = userService.UserId;
        if (userId == null) return Results.Unauthorized();
        if (wordIds is not { Count: > 0 and <= 50 }) return Results.BadRequest();

        var distinct = wordIds.Distinct().ToList();
        var meanings = await userContext.UserCustomMeanings
            .AsNoTracking()
            .Where(e => e.UserId == userId && distinct.Contains(e.WordId))
            .ToDictionaryAsync(e => e.WordId, e => e.Text);

        return Results.Ok(meanings);
    }

    #endregion

    #region Hidden Definitions

    [HttpGet("hidden-definitions/{wordId}")]
    public async Task<IResult> GetHiddenDefinitions(int wordId)
    {
        var userId = userService.UserId;
        if (userId == null) return Results.Unauthorized();

        var mask = await userContext.UserHiddenDefinitions
                                    .AsNoTracking()
                                    .Where(e => e.UserId == userId && e.WordId == wordId)
                                    .Select(e => e.HiddenMask)
                                    .FirstOrDefaultAsync();

        return Results.Ok(new UserHiddenDefinitionsDto { WordId = wordId, HiddenIndices = UserHiddenDefinition.ToIndices(mask) });
    }

    [HttpPut("hidden-definitions/{wordId}")]
    public async Task<IResult> UpdateHiddenDefinitions(int wordId, [FromBody] UpdateUserHiddenDefinitionsRequest request)
    {
        var userId = userService.UserId;
        if (userId == null) return Results.Unauthorized();

        var mask = UserHiddenDefinition.ToMask(request.HiddenIndices);

        var entry = await userContext.UserHiddenDefinitions
                                     .FirstOrDefaultAsync(e => e.UserId == userId && e.WordId == wordId);

        if (mask == 0)
        {
            if (entry != null) userContext.UserHiddenDefinitions.Remove(entry);
        }
        else if (entry == null)
        {
            userContext.UserHiddenDefinitions.Add(new UserHiddenDefinition { UserId = userId, WordId = wordId, HiddenMask = mask });
        }
        else
        {
            entry.HiddenMask = mask;
        }

        await userContext.SaveChangesAsync();

        return Results.Ok(new UserHiddenDefinitionsDto { WordId = wordId, HiddenIndices = UserHiddenDefinition.ToIndices(mask) });
    }

    [HttpPost("hidden-definitions/batch")]
    public async Task<IResult> GetHiddenDefinitionsBatch([FromBody] List<int> wordIds)
    {
        var userId = userService.UserId;
        if (userId == null) return Results.Unauthorized();
        if (wordIds is not { Count: > 0 and <= 200 }) return Results.BadRequest();

        var distinct = wordIds.Distinct().ToList();
        var entries = await userContext.UserHiddenDefinitions
                                       .AsNoTracking()
                                       .Where(e => e.UserId == userId && distinct.Contains(e.WordId))
                                       .Select(e => new { e.WordId, e.HiddenMask })
                                       .ToListAsync();

        return Results.Ok(entries.ToDictionary(e => e.WordId, e => UserHiddenDefinition.ToIndices(e.HiddenMask)));
    }

    #endregion
}
