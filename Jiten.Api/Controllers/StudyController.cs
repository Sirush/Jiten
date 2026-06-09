using System.Text.Json;
using System.Text.RegularExpressions;
using Jiten.Api.Dtos;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Enums;
using Jiten.Api.Helpers;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.User;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using Swashbuckle.AspNetCore.Annotations;
using WanaKanaShaapu;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/srs")]
[Authorize]
public class StudyController(
    JitenDbContext context,
    IDbContextFactory<JitenDbContext> contextFactory,
    IDbContextFactory<UserDbContext> userContextFactory,
    UserDbContext userContext,
    ICurrentUserService currentUserService,
    IHttpContextAccessor httpContextAccessor,
    IDeckWordResolver deckWordResolver,
    IStudyDeckMembershipService deckMembership,
    IDeckDownloadService downloadService,
    IDeckImportService importService,
    IWordFormSiblingCache wordFormCache,
    IStudySessionService sessionService,
    ILogger<StudyController> logger) : ControllerBase
{
    private static readonly Regex SentenceMarkerRegex =
        new(@"\*\*[^*]+\*\*", RegexOptions.Compiled);

    [HttpGet("overview-version")]
    [SwaggerOperation(Summary = "Get current overview version for cache validation")]
    public async Task<IResult> GetOverviewVersion()
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var version = await sessionService.GetStudyOverviewVersion(userId);
        return Results.Ok(new { version });
    }

    [HttpGet("study-decks")]
    [SwaggerOperation(Summary = "Get user's studied decks")]
    public async Task<IResult> GetStudyDecks()
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var studyDecks = await userContext.UserStudyDecks
            .AsNoTracking()
            .Where(sd => sd.UserId == userId)
            .OrderByDescending(sd => sd.IsActive)
            .ThenBy(sd => sd.SortOrder)
            .ToListAsync();

        if (studyDecks.Count == 0)
            return Results.Ok(new List<StudyDeckDto>());

        var deckIds = studyDecks.Where(sd => sd.DeckId.HasValue).Select(sd => sd.DeckId!.Value).ToList();

        var decksTask = context.Decks
            .AsNoTracking()
            .Where(d => deckIds.Contains(d.DeckId))
            .ToDictionaryAsync(d => d.DeckId);

        var cardsTask = userContext.FsrsCards
            .AsNoTracking()
            .Where(fc => fc.UserId == userId)
            .Select(fc => new { fc.WordId, fc.ReadingIndex, fc.State, fc.Due, fc.LastReview })
            .ToListAsync();

        await Task.WhenAll(decksTask, cardsTask);

        var decks = decksTask.Result;

        var parentDeckIds = decks.Values.Where(d => d.ParentDeckId.HasValue).Select(d => d.ParentDeckId!.Value).Distinct().ToList();
        var parentDecks = parentDeckIds.Count > 0
            ? await context.Decks.AsNoTracking()
                .Where(d => parentDeckIds.Contains(d.DeckId))
                .Select(d => new { d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.CoverName })
                .ToDictionaryAsync(d => d.DeckId)
            : new();

        var cardStateMap = new Dictionary<(int, byte), (FsrsState State, DateTime Due, DateTime? LastReview)>();
        foreach (var c in cardsTask.Result)
            cardStateMap[(c.WordId, c.ReadingIndex)] = (c.State, c.Due, c.LastReview);

        var wordSetStates = await currentUserService.GetWordSetDerivedStates();
        var wordSetEncodedKeys = new Dictionary<long, WordSetStateType>();
        foreach (var (key, setState) in wordSetStates)
        {
            if (cardStateMap.ContainsKey(key)) continue;
            wordSetEncodedKeys[WordFormHelper.EncodeWordKey(key.Item1, key.Item2)] = setState;
        }

        var redundantKanaKeys = new HashSet<long>();
        var redundantKanaPairs = new HashSet<(int, byte)>();
        foreach (var ((wordId, ri), (state, _, _)) in cardStateMap)
        {
            if (state is FsrsState.New) continue;
            var kanaIndexes = wordFormCache.GetKanaIndexesForKanji(wordId, ri);
            if (kanaIndexes == null) continue;
            foreach (var kanaRi in kanaIndexes)
            {
                if (cardStateMap.ContainsKey((wordId, kanaRi))) continue;
                redundantKanaKeys.Add(WordFormHelper.EncodeWordKey(wordId, kanaRi));
                redundantKanaPairs.Add((wordId, kanaRi));
            }
        }

        var settings = await LoadStudySettings(userId);
        var now = DateTime.UtcNow;
        var dueCutoff = GetDueCutoff(now, settings);
        var resolvedDecks = new List<(UserStudyDeck Sd, Deck? Deck)>();
        var countOnlyStats = new Dictionary<int, (int Total, int Unseen, int Learning, int Review, int Mastered, int Blacklisted, int Suspended, int Due, int Young, int Mature, bool WasTruncated)>();

        // Pre-compute shared GlobalDynamic data before the per-deck loop
        Dictionary<(int, byte), int>? userCardFreqRanks = null;
        HashSet<long>? kanaOnlyCardWords = null;
        List<int>? extraWordIds = null;
        var posFilterCache = new Dictionary<string, HashSet<int>?>();

        if (studyDecks.Any(sd => sd.DeckType == StudyDeckType.GlobalDynamic))
        {
            extraWordIds = wordSetStates.Keys.Select(k => k.Item1).Distinct().ToList();
            userCardFreqRanks = await BuildCardFrequencyRanks(cardStateMap, extraWordIds);

            if (studyDecks.Any(sd => sd.DeckType == StudyDeckType.GlobalDynamic && sd.ExcludeKana))
                kanaOnlyCardWords = await WordFormHelper.GetKanaFormKeys(context,
                    cardStateMap.Keys.Select(k => k.Item1).Distinct().Union(extraWordIds));

            foreach (var pf in studyDecks
                         .Where(sd => sd.DeckType == StudyDeckType.GlobalDynamic && !string.IsNullOrEmpty(sd.PosFilter))
                         .Select(sd => sd.PosFilter!)
                         .Distinct())
                posFilterCache[pf] = await GetPosMatchedWordIds(pf, cardStateMap, extraWordIds);
        }

        // Batch load all StaticWordList data in one query instead of per-deck
        Dictionary<int, HashSet<long>>? staticWordKeysByDeck = null;
        var staticDecks = studyDecks.Where(sd => sd.DeckType == StudyDeckType.StaticWordList).ToList();
        if (staticDecks.Count > 0)
        {
            var staticDeckIds = staticDecks.Select(sd => sd.UserStudyDeckId).ToList();
            var allStaticPairs = await userContext.UserStudyDeckWords.AsNoTracking()
                .Where(w => staticDeckIds.Contains(w.UserStudyDeckId))
                .Select(w => new { w.UserStudyDeckId, w.WordId, w.ReadingIndex })
                .ToListAsync();

            HashSet<long>? staticKanaKeys = null;
            if (staticDecks.Any(sd => sd.ExcludeKana))
            {
                var allStaticWordIds = allStaticPairs.Select(p => p.WordId).Distinct();
                staticKanaKeys = await WordFormHelper.GetKanaFormKeys(context, allStaticWordIds);
            }

            staticWordKeysByDeck = new();
            foreach (var g in allStaticPairs.GroupBy(p => p.UserStudyDeckId))
            {
                var sd = staticDecks.First(s => s.UserStudyDeckId == g.Key);
                var keySet = g.Select(p => WordFormHelper.EncodeWordKey(p.WordId, (byte)p.ReadingIndex)).ToHashSet();
                if (sd.ExcludeKana && staticKanaKeys is { Count: > 0 })
                    keySet.ExceptWith(staticKanaKeys);
                staticWordKeysByDeck[g.Key] = keySet;
            }
        }

        // Collect parallel tasks — each uses its own factory-created JitenDbContext
        var mediaDeckCountTasks = new List<(int StudyDeckId, Task<(int Count, HashSet<long> WordKeys)>)>();
        var globalDynamicCountTasks = new List<(int StudyDeckId, UserStudyDeck Sd, Task<(int Count, bool WasTruncated)>)>();

        foreach (var sd in studyDecks)
        {
            if (sd.DeckType == StudyDeckType.MediaDeck)
            {
                if (!sd.DeckId.HasValue || !decks.TryGetValue(sd.DeckId.Value, out var deck))
                {
                    resolvedDecks.Add((sd, null));
                    continue;
                }
                resolvedDecks.Add((sd, deck));

                if ((DeckDownloadType)sd.DownloadType == DeckDownloadType.TargetCoverage && sd.TargetPercentage.HasValue)
                {
                    mediaDeckCountTasks.Add((sd.UserStudyDeckId,
                        CountWithFactoryContext((ctx, uCtx, us) => new DeckWordResolver(ctx, uCtx, us, wordFormCache)
                            .CountTargetCoverageWords(sd.DeckId.Value, deck, sd.TargetPercentage.Value, sd.ExcludeKana, sd.PosFilter, sd.StartFromKnown))));
                }
                else
                {
                    var request = new DeckWordResolveRequest(
                        sd.DeckId.Value, deck,
                        (DeckDownloadType)sd.DownloadType, (DeckOrder)sd.Order,
                        sd.MinFrequency, sd.MaxFrequency,
                        false, false,
                        sd.TargetPercentage,
                        sd.MinOccurrences, sd.MaxOccurrences,
                        sd.PosFilter, sd.StartFromKnown);
                    mediaDeckCountTasks.Add((sd.UserStudyDeckId,
                        CountWithFactoryContext((ctx, uCtx, us) => new DeckWordResolver(ctx, uCtx, us, wordFormCache)
                            .CountDeckWords(request, sd.ExcludeKana))));
                }
            }
            else if (sd.DeckType == StudyDeckType.GlobalDynamic)
            {
                resolvedDecks.Add((sd, null));
                globalDynamicCountTasks.Add((sd.UserStudyDeckId, sd,
                    CountWithFactoryContext<(int, bool)>((ctx, uCtx, us) => new DeckWordResolver(ctx, uCtx, us, wordFormCache)
                        .CountGlobalDynamicWords(sd.MinGlobalFrequency, sd.MaxGlobalFrequency, sd.PosFilter, sd.ExcludeKana))));
            }
            else if (sd.DeckType == StudyDeckType.StaticWordList)
            {
                resolvedDecks.Add((sd, null));
                if (staticWordKeysByDeck != null && staticWordKeysByDeck.TryGetValue(sd.UserStudyDeckId, out var wordKeys))
                {
                    var stats = ComputeCardStatsFromWordKeys(wordKeys, cardStateMap, dueCutoff, wordSetEncodedKeys, redundantKanaKeys);
                    countOnlyStats[sd.UserStudyDeckId] = (wordKeys.Count, Math.Max(0, wordKeys.Count - stats.Tracked),
                        stats.Learning, stats.Review, stats.Mastered, stats.Blacklisted, stats.Suspended, stats.Due, stats.Young, stats.Mature, false);
                }
            }
            else
            {
                resolvedDecks.Add((sd, null));
            }
        }

        // Await all parallel deck queries
        var allParallelTasks = mediaDeckCountTasks.Select(t => (Task)t.Item2)
            .Concat(globalDynamicCountTasks.Select(t => (Task)t.Item3))
            .ToList();
        if (allParallelTasks.Count > 0)
            await Task.WhenAll(allParallelTasks);

        foreach (var (studyDeckId, task) in mediaDeckCountTasks)
        {
            var (total, wordKeys) = task.Result;
            var stats = ComputeCardStatsFromWordKeys(wordKeys, cardStateMap, dueCutoff, wordSetEncodedKeys, redundantKanaKeys);
            countOnlyStats[studyDeckId] = (total, Math.Max(0, total - stats.Tracked),
                stats.Learning, stats.Review, stats.Mastered, stats.Blacklisted, stats.Suspended, stats.Due, stats.Young, stats.Mature, false);
        }

        foreach (var (studyDeckId, sd, task) in globalDynamicCountTasks)
        {
            var (total, wasTruncated) = task.Result;
            var posMatchedWordIds = !string.IsNullOrEmpty(sd.PosFilter) && posFilterCache.TryGetValue(sd.PosFilter!, out var cached)
                ? cached : null;
            var stats = ComputeGlobalDynamicCardStats(
                sd, cardStateMap, userCardFreqRanks!, kanaOnlyCardWords, posMatchedWordIds, dueCutoff,
                wordSetStates, redundantKanaPairs);
            countOnlyStats[studyDeckId] = (total, Math.Max(0, total - stats.Tracked),
                stats.Learning, stats.Review, stats.Mastered, stats.Blacklisted, stats.Suspended, stats.Due, stats.Young, stats.Mature, wasTruncated);
        }

        var result = resolvedDecks.Select(entry =>
        {
            var (sd, deck) = entry;
            var dto = new StudyDeckDto
            {
                UserStudyDeckId = sd.UserStudyDeckId,
                DeckType = sd.DeckType,
                Name = sd.Name,
                Description = sd.Description,
                DeckId = sd.DeckId,
                Title = deck?.OriginalTitle ?? "",
                RomajiTitle = deck?.RomajiTitle,
                EnglishTitle = deck?.EnglishTitle,
                CoverName = deck?.CoverName,
                MediaType = (int)(deck?.MediaType ?? 0),
                SortOrder = sd.SortOrder,
                IsActive = sd.IsActive,
                DownloadType = sd.DownloadType,
                Order = sd.Order,
                MinFrequency = sd.MinFrequency,
                MaxFrequency = sd.MaxFrequency,
                TargetPercentage = sd.TargetPercentage,
                StartFromKnown = sd.StartFromKnown,
                MinOccurrences = sd.MinOccurrences,
                MaxOccurrences = sd.MaxOccurrences,
                ExcludeKana = sd.ExcludeKana,
                MinGlobalFrequency = sd.MinGlobalFrequency,
                MaxGlobalFrequency = sd.MaxGlobalFrequency,
                PosFilter = sd.PosFilter
            };

            if (deck?.ParentDeckId != null && parentDecks.TryGetValue(deck.ParentDeckId.Value, out var parent))
            {
                dto.ParentDeckId = parent.DeckId;
                dto.ParentTitle = parent.OriginalTitle;
                dto.ParentRomajiTitle = parent.RomajiTitle;
                dto.ParentEnglishTitle = parent.EnglishTitle;
                dto.ParentCoverName = parent.CoverName;
            }

            if (countOnlyStats.TryGetValue(sd.UserStudyDeckId, out var precomputed))
            {
                dto.TotalWords = precomputed.Total;
                dto.UnseenCount = precomputed.Unseen;
                dto.LearningCount = precomputed.Learning;
                dto.ReviewCount = precomputed.Review;
                dto.YoungCount = precomputed.Young;
                dto.MatureCount = precomputed.Mature;
                dto.MasteredCount = precomputed.Mastered;
                dto.BlacklistedCount = precomputed.Blacklisted;
                dto.SuspendedCount = precomputed.Suspended;
                dto.DueReviewCount = precomputed.Due;
                if (precomputed.WasTruncated)
                    dto.Warning = "Results were truncated at 500,000 words. Consider narrowing your frequency range.";
            }

            return dto;
        }).ToList();

        return Results.Ok(result);
    }

    [HttpPost("study-decks")]
    [SwaggerOperation(Summary = "Add a deck to study")]
    public async Task<IResult> AddStudyDeck(AddStudyDeckRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var deckCount = await userContext.UserStudyDecks.CountAsync(sd => sd.UserId == userId);
        if (deckCount >= 50)
            return Results.BadRequest("Maximum of 50 study decks reached.");

        if (request.DeckType == StudyDeckType.MediaDeck)
        {
            if (!request.DeckId.HasValue)
                return Results.BadRequest("DeckId is required for media decks.");
            if (request.MaxFrequency > 0 && request.MinFrequency > request.MaxFrequency)
                return Results.BadRequest("MinFrequency cannot exceed MaxFrequency.");

            var exists = await userContext.UserStudyDecks
                .AnyAsync(sd => sd.UserId == userId && sd.DeckId == request.DeckId);
            if (exists)
                return Results.BadRequest("This deck is already in your study list.");

            var deckExists = await context.Decks.AnyAsync(d => d.DeckId == request.DeckId);
            if (!deckExists)
                return Results.NotFound("Deck not found.");
        }
        else if (request.DeckType == StudyDeckType.GlobalDynamic)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest("Name is required for global frequency decks.");
            if (!request.MinGlobalFrequency.HasValue && !request.MaxGlobalFrequency.HasValue)
                return Results.BadRequest("At least one frequency bound is required.");
            if (request.MaxGlobalFrequency > 0 && request.MinGlobalFrequency > request.MaxGlobalFrequency)
                return Results.BadRequest("MinGlobalFrequency cannot exceed MaxGlobalFrequency.");
        }
        else if (request.DeckType == StudyDeckType.StaticWordList)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest("Name is required for word list decks.");
        }
        else
        {
            return Results.BadRequest("Invalid deck type.");
        }

        if (!IsValidPosFilter(request.PosFilter))
            return Results.BadRequest("PosFilter must be a valid JSON array of strings.");

        var maxOrder = await userContext.UserStudyDecks
            .Where(sd => sd.UserId == userId && sd.IsActive)
            .MaxAsync(sd => (int?)sd.SortOrder) ?? -1;

        var studyDeck = new UserStudyDeck
        {
            UserId = userId,
            DeckType = request.DeckType,
            Name = request.Name ?? "",
            Description = request.Description,
            DeckId = request.DeckId,
            SortOrder = maxOrder + 1,
            DownloadType = request.DownloadType,
            Order = request.Order,
            MinFrequency = request.MinFrequency,
            MaxFrequency = request.MaxFrequency,
            TargetPercentage = request.TargetPercentage,
            StartFromKnown = request.StartFromKnown,
            MinOccurrences = request.MinOccurrences,
            MaxOccurrences = request.MaxOccurrences,
            ExcludeKana = request.ExcludeKana,
            MinGlobalFrequency = request.MinGlobalFrequency,
            MaxGlobalFrequency = request.MaxGlobalFrequency,
            PosFilter = request.PosFilter,
            CreatedAt = DateTime.UtcNow
        };

        userContext.UserStudyDecks.Add(studyDeck);
        await userContext.SaveChangesAsync();
        await sessionService.BumpStudyOverviewVersion(userId);
        logger.LogInformation("User added study deck: DeckType={DeckType}, DeckId={DeckId}", request.DeckType, request.DeckId);
        return Results.Ok(new { studyDeck.UserStudyDeckId });
    }

    [HttpPut("study-decks/{id:int}")]
    [SwaggerOperation(Summary = "Update study deck filters")]
    public async Task<IResult> UpdateStudyDeck(int id, UpdateStudyDeckRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var studyDeck = await userContext.UserStudyDecks
            .FirstOrDefaultAsync(sd => sd.UserStudyDeckId == id && sd.UserId == userId);
        if (studyDeck == null) return Results.NotFound();

        if (studyDeck.DeckType == StudyDeckType.MediaDeck && request.MaxFrequency > 0 && request.MinFrequency > request.MaxFrequency)
            return Results.BadRequest("MinFrequency cannot exceed MaxFrequency.");

        if (studyDeck.DeckType == StudyDeckType.GlobalDynamic && request.MaxGlobalFrequency > 0 && request.MinGlobalFrequency > request.MaxGlobalFrequency)
            return Results.BadRequest("MinGlobalFrequency cannot exceed MaxGlobalFrequency.");

        if (!IsValidPosFilter(request.PosFilter))
            return Results.BadRequest("PosFilter must be a valid JSON array of strings.");

        if (request.Name != null) studyDeck.Name = request.Name;
        if (request.Description != null) studyDeck.Description = request.Description;

        if (studyDeck.DeckType == StudyDeckType.MediaDeck)
        {
            studyDeck.DownloadType = request.DownloadType;
            studyDeck.Order = request.Order;
            studyDeck.MinFrequency = request.MinFrequency;
            studyDeck.MaxFrequency = request.MaxFrequency;
            studyDeck.TargetPercentage = request.TargetPercentage;
            studyDeck.StartFromKnown = request.StartFromKnown;
            studyDeck.MinOccurrences = request.MinOccurrences;
            studyDeck.MaxOccurrences = request.MaxOccurrences;
            studyDeck.PosFilter = request.PosFilter;
            studyDeck.ExcludeKana = request.ExcludeKana;
        }
        else if (studyDeck.DeckType == StudyDeckType.GlobalDynamic)
        {
            studyDeck.Order = request.Order;
            studyDeck.MinGlobalFrequency = request.MinGlobalFrequency;
            studyDeck.MaxGlobalFrequency = request.MaxGlobalFrequency;
            studyDeck.ExcludeKana = request.ExcludeKana;
            studyDeck.PosFilter = request.PosFilter;
        }
        else if (studyDeck.DeckType == StudyDeckType.StaticWordList)
        {
            studyDeck.Order = request.Order;
            studyDeck.ExcludeKana = request.ExcludeKana;
        }

        await userContext.SaveChangesAsync();
        await sessionService.BumpStudyOverviewVersion(userId);
        await deckMembership.Invalidate(userId, studyDeck.UserStudyDeckId);

        return Results.Ok(new { success = true });
    }

    [HttpDelete("study-decks/{id:int}")]
    [SwaggerOperation(Summary = "Remove a study deck (keeps existing cards)")]
    public async Task<IResult> RemoveStudyDeck(int id)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var studyDeck = await userContext.UserStudyDecks
            .FirstOrDefaultAsync(sd => sd.UserStudyDeckId == id && sd.UserId == userId);
        if (studyDeck == null) return Results.NotFound();

        userContext.UserStudyDecks.Remove(studyDeck);
        await userContext.SaveChangesAsync();
        await sessionService.BumpStudyOverviewVersion(userId);
        await deckMembership.Invalidate(userId, studyDeck.UserStudyDeckId);

        logger.LogInformation("User removed study deck: UserStudyDeckId={UserStudyDeckId}", studyDeck.UserStudyDeckId);
        return Results.Ok(new { success = true });
    }

    [HttpPut("study-decks/reorder")]
    [SwaggerOperation(Summary = "Reorder study decks")]
    public async Task<IResult> ReorderStudyDecks(ReorderStudyDecksRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        if (request.Items.Count == 0)
            return Results.BadRequest("Items cannot be empty.");

        if (request.Items.Select(i => i.UserStudyDeckId).Distinct().Count() != request.Items.Count)
            return Results.BadRequest("Duplicate UserStudyDeckId values.");

        var activeItems = request.Items.Where(i => i.IsActive).ToList();
        var inactiveItems = request.Items.Where(i => !i.IsActive).ToList();
        if (activeItems.Select(i => i.SortOrder).Distinct().Count() != activeItems.Count)
            return Results.BadRequest("Duplicate SortOrder values in active decks.");
        if (inactiveItems.Select(i => i.SortOrder).Distinct().Count() != inactiveItems.Count)
            return Results.BadRequest("Duplicate SortOrder values in inactive decks.");

        var ids = request.Items.Select(i => i.UserStudyDeckId).ToList();
        var decks = await userContext.UserStudyDecks
            .Where(sd => sd.UserId == userId && ids.Contains(sd.UserStudyDeckId))
            .ToListAsync();

        var deckMap = decks.ToDictionary(d => d.UserStudyDeckId);
        foreach (var item in request.Items)
        {
            if (deckMap.TryGetValue(item.UserStudyDeckId, out var deck))
            {
                deck.SortOrder = item.SortOrder;
                deck.IsActive = item.IsActive;
            }
        }

        await userContext.SaveChangesAsync();
        await sessionService.BumpStudyOverviewVersion(userId);

        return Results.Ok(new { success = true });
    }

    [HttpPost("study-decks/{id:int}/words")]
    [SwaggerOperation(Summary = "Add a single word to a static deck")]
    public async Task<IResult> AddDeckWord(int id, AddDeckWordRequest request)
    {
        var (studyDeck, error) = await GetStaticDeckForUser(id, "added");
        if (error != null) return error;

        var userId = currentUserService.UserId!;
        await using var transaction = await userContext.Database.BeginTransactionAsync();

        var limitError = await ValidateWordLimits(userId, id, 1);
        if (limitError != null) return Results.BadRequest(limitError);

        var wordFormExists = await context.WordForms
            .AnyAsync(wf => wf.WordId == request.WordId && wf.ReadingIndex == request.ReadingIndex);
        if (!wordFormExists)
            return Results.BadRequest("The specified word and reading index do not exist.");

        var existing = await userContext.UserStudyDeckWords
            .FirstOrDefaultAsync(w => w.UserStudyDeckId == id && w.WordId == request.WordId && w.ReadingIndex == request.ReadingIndex);
        if (existing != null)
        {
            existing.Occurrences += Math.Max(1, request.Occurrences);
        }
        else
        {
            var maxSort = await userContext.UserStudyDeckWords
                .Where(w => w.UserStudyDeckId == id)
                .MaxAsync(w => (int?)w.SortOrder) ?? -1;

            userContext.UserStudyDeckWords.Add(new UserStudyDeckWord
            {
                UserStudyDeckId = id,
                WordId = request.WordId,
                ReadingIndex = request.ReadingIndex,
                Occurrences = Math.Max(1, request.Occurrences),
                SortOrder = maxSort + 1
            });
        }

        if (request.Sentence is { Length: > 0 and <= 150 }
            && SentenceMarkerRegex.IsMatch(request.Sentence))
        {
            var sentenceCount = await userContext.UserExampleSentences
                .CountAsync(e => e.UserId == userId && e.WordId == request.WordId && e.ReadingIndex == request.ReadingIndex);

            if (sentenceCount < 3)
            {
                userContext.UserExampleSentences.Add(new UserExampleSentence
                {
                    UserId = userId,
                    WordId = request.WordId,
                    ReadingIndex = (byte)request.ReadingIndex,
                    Text = request.Sentence,
                    Source = request.Source?.Length > 150 ? request.Source[..150] : request.Source,
                    SortOrder = (byte)sentenceCount
                });
            }
        }

        await userContext.SaveChangesAsync();
        await transaction.CommitAsync();
        await sessionService.BumpStudyOverviewVersion(userId);

        return Results.Ok(new { success = true });
    }

    [HttpPost("study-decks/{id:int}/words/batch")]
    [SwaggerOperation(Summary = "Add multiple words to a static deck")]
    public async Task<IResult> BatchAddDeckWords(int id, BatchAddDeckWordsRequest request)
    {
        if (request.Words.Count == 0)
            return Results.BadRequest("No words provided.");
        if (request.Words.Count > 10_000)
            return Results.BadRequest("Maximum of 10,000 words per batch.");

        var (studyDeck, error) = await GetStaticDeckForUser(id, "added");
        if (error != null) return error;

        var userId = currentUserService.UserId!;
        await using var transaction = await userContext.Database.BeginTransactionAsync();

        var limitError = await ValidateWordLimits(userId, id, request.Words.Count);
        if (limitError != null) return Results.BadRequest(limitError);

        var requestedWordIds = request.Words.Select(w => w.WordId).Distinct().ToList();
        var validKeys = await context.WordForms
            .Where(wf => requestedWordIds.Contains(wf.WordId))
            .Select(wf => WordFormHelper.EncodeWordKey(wf.WordId, wf.ReadingIndex))
            .ToListAsync();
        var validKeySet = validKeys.ToHashSet();

        var existingWords = await userContext.UserStudyDeckWords
            .Where(w => w.UserStudyDeckId == id && requestedWordIds.Contains(w.WordId))
            .ToListAsync();
        var existingMap = existingWords.ToDictionary(w => WordFormHelper.EncodeWordKey(w.WordId, w.ReadingIndex));

        var maxSort = await userContext.UserStudyDeckWords
            .Where(w => w.UserStudyDeckId == id)
            .MaxAsync(w => (int?)w.SortOrder) ?? -1;

        var added = 0;
        var updated = 0;
        var seen = new HashSet<long>();
        foreach (var word in request.Words)
        {
            var key = WordFormHelper.EncodeWordKey(word.WordId, word.ReadingIndex);
            if (!validKeySet.Contains(key)) continue;
            if (!seen.Add(key)) continue;

            if (existingMap.TryGetValue(key, out var existing))
            {
                existing.Occurrences += Math.Max(1, word.Occurrences);
                updated++;
            }
            else
            {
                existingMap[key] = new UserStudyDeckWord();
                userContext.UserStudyDeckWords.Add(new UserStudyDeckWord
                {
                    UserStudyDeckId = id,
                    WordId = word.WordId,
                    ReadingIndex = word.ReadingIndex,
                    Occurrences = Math.Max(1, word.Occurrences),
                    SortOrder = ++maxSort
                });
                added++;
            }
        }

        if (added > 0 || updated > 0)
            await userContext.SaveChangesAsync();
        await transaction.CommitAsync();
        if (added > 0 || updated > 0)
            await sessionService.BumpStudyOverviewVersion(userId);

        return Results.Ok(new { added, updated });
    }

    [HttpGet("study-decks/{id:int}/word-keys")]
    [SwaggerOperation(Summary = "Get all word keys (wordId + readingIndex) in a static deck")]
    public async Task<IResult> GetDeckWordKeys(int id)
    {
        var (_, error) = await GetStaticDeckForUser(id, "viewed");
        if (error != null) return error;

        var keys = await userContext.UserStudyDeckWords
            .Where(w => w.UserStudyDeckId == id)
            .Select(w => new { w.WordId, w.ReadingIndex })
            .ToListAsync();

        return Results.Ok(keys);
    }

    [HttpDelete("study-decks/{id:int}/words/{wordId:int}/{readingIndex:int}")]
    [SwaggerOperation(Summary = "Remove a word from a static deck")]
    public async Task<IResult> RemoveDeckWord(int id, int wordId, int readingIndex)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var (_, error) = await GetStaticDeckForUser(id, "removed from");
        if (error != null) return error;

        var word = await userContext.UserStudyDeckWords
            .FirstOrDefaultAsync(w => w.UserStudyDeckId == id && w.WordId == wordId && w.ReadingIndex == (short)readingIndex);
        if (word == null) return Results.NotFound("Word not found in deck.");

        userContext.UserStudyDeckWords.Remove(word);
        await userContext.SaveChangesAsync();
        await sessionService.BumpStudyOverviewVersion(userId);

        return Results.Ok(new { success = true });
    }

    [HttpPatch("study-decks/{id:int}/words/{wordId:int}/{readingIndex:int}")]
    [SwaggerOperation(Summary = "Update word occurrences in a static deck")]
    public async Task<IResult> UpdateDeckWord(int id, int wordId, int readingIndex, UpdateDeckWordRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var (_, error) = await GetStaticDeckForUser(id, "updated");
        if (error != null) return error;

        var word = await userContext.UserStudyDeckWords
            .FirstOrDefaultAsync(w => w.UserStudyDeckId == id && w.WordId == wordId && w.ReadingIndex == (short)readingIndex);
        if (word == null) return Results.NotFound("Word not found in deck.");

        word.Occurrences = Math.Max(1, request.Occurrences);
        await userContext.SaveChangesAsync();
        await sessionService.BumpStudyOverviewVersion(userId);

        return Results.Ok(new { success = true });
    }

    [HttpGet("study-decks/{id:int}/words")]
    [SwaggerOperation(Summary = "List words in a static deck with full JMDict details")]
    [ProducesResponseType(typeof(PaginatedResponse<StaticDeckWordsResponse>), StatusCodes.Status200OK)]
    public async Task<IResult> GetDeckWords(int id, int? offset = 0, string? sort = "importOrder", SortOrder sortOrder = SortOrder.Ascending, string? search = null)
    {
        const int pageSize = 100;
        if (offset < 0) return Results.BadRequest("Offset cannot be negative.");
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var studyDeck = await userContext.UserStudyDecks
            .AsNoTracking()
            .FirstOrDefaultAsync(sd => sd.UserStudyDeckId == id && sd.UserId == userId);
        if (studyDeck == null) return Results.NotFound();
        if (studyDeck.DeckType != StudyDeckType.StaticWordList)
            return Results.BadRequest("Only static word list decks support word listing.");

        var query = userContext.UserStudyDeckWords
            .AsNoTracking()
            .Where(w => w.UserStudyDeckId == id);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchTerm = search.Trim();
            var deckWordIds = await query.Select(w => w.WordId).Distinct().ToListAsync();

            var matchingWordIds = await context.WordForms
                .AsNoTracking()
                .Where(f => deckWordIds.Contains(f.WordId) && f.Text.Contains(searchTerm))
                .Select(f => f.WordId)
                .Distinct()
                .ToListAsync();

            if (!SearchHelper.ContainsJapanese(searchTerm))
            {
                var likePattern = $"%{searchTerm}%";
                var matchingByMeaning = await context.Definitions
                    .AsNoTracking()
                    .Where(d => deckWordIds.Contains(d.WordId)
                                && d.EnglishMeanings.Any(m => EF.Functions.ILike(m, likePattern)))
                    .Select(d => d.WordId)
                    .Distinct()
                    .ToListAsync();
                matchingWordIds = matchingWordIds.Union(matchingByMeaning).ToList();
            }

            var matchSet = matchingWordIds.ToHashSet();
            query = query.Where(w => matchSet.Contains(w.WordId));
        }

        var totalCount = await query.CountAsync();

        var desc = sortOrder == SortOrder.Descending;

        List<UserStudyDeckWord> pageWords;
        if (sort == "frequency")
        {
            var allWordIds = await query.Select(w => w.WordId).Distinct().ToListAsync();
            var freqLookup = await context.WordFormFrequencies
                .AsNoTracking()
                .Where(f => allWordIds.Contains(f.WordId))
                .GroupBy(f => f.WordId)
                .Select(g => new { WordId = g.Key, MinRank = g.Min(f => f.FrequencyRank) })
                .ToDictionaryAsync(x => x.WordId, x => x.MinRank);

            var allWords = await query.ToListAsync();
            var sorted = desc
                ? allWords.OrderByDescending(w => freqLookup.GetValueOrDefault(w.WordId, int.MaxValue)).ThenBy(w => w.SortOrder)
                : allWords.OrderBy(w => freqLookup.GetValueOrDefault(w.WordId, int.MaxValue)).ThenBy(w => w.SortOrder);

            pageWords = sorted
                .Skip(offset ?? 0)
                .Take(pageSize)
                .ToList();
        }
        else if (sort == "occurrences")
        {
            var ordered = desc
                ? query.OrderByDescending(w => w.Occurrences).ThenBy(w => w.SortOrder)
                : query.OrderBy(w => w.Occurrences).ThenBy(w => w.SortOrder);
            pageWords = await ordered
                .Skip(offset ?? 0)
                .Take(pageSize)
                .ToListAsync();
        }
        else
        {
            var ordered = desc
                ? query.OrderByDescending(w => w.SortOrder)
                : query.OrderBy(w => w.SortOrder);
            pageWords = await ordered
                .Skip(offset ?? 0)
                .Take(pageSize)
                .ToListAsync();
        }

        var pageWordIds = pageWords.Select(w => w.WordId).Distinct().ToList();

        var words = await context.JMDictWords
            .AsNoTracking()
            .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
            .Where(w => pageWordIds.Contains(w.WordId))
            .ToListAsync();

        var wordForms = await context.WordForms
            .AsNoTracking()
            .Where(wf => pageWordIds.Contains(wf.WordId))
            .ToListAsync();
        RubyTextHelper.EnrichForms(wordForms);

        var formsByWord = wordForms.GroupBy(f => f.WordId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var frequencies = await context.WordFormFrequencies
            .AsNoTracking()
            .Where(f => pageWordIds.Contains(f.WordId))
            .ToListAsync();

        var freqByWordReading = frequencies
            .ToDictionary(f => (f.WordId, f.ReadingIndex));

        var wordDict = words.ToDictionary(w => w.WordId);

        var dtos = pageWords.Select(pw =>
        {
            if (!wordDict.TryGetValue(pw.WordId, out var w)) return null;
            if (!formsByWord.TryGetValue(pw.WordId, out var forms) || forms.Count == 0) return null;

            var bestForm = forms.FirstOrDefault(f => f.ReadingIndex == pw.ReadingIndex)
                           ?? forms.OrderBy(f => f.ReadingIndex).First();

            var freq = freqByWordReading.GetValueOrDefault((pw.WordId, bestForm.ReadingIndex));
            var firstDef = w.Definitions
                .Where(d => d.EnglishMeanings.Count > 0)
                .OrderBy(d => d.SenseIndex)
                .FirstOrDefault();

            string? primaryKanjiText = null;
            if (bestForm.FormType == JmDictFormType.KanaForm)
            {
                var kanjiForm = forms
                    .Where(f => f.FormType == JmDictFormType.KanjiForm && !f.IsSearchOnly)
                    .OrderByDescending(f => freqByWordReading.GetValueOrDefault((w.WordId, f.ReadingIndex))?.FrequencyRank != null ? 1 : 0)
                    .ThenBy(f => freqByWordReading.GetValueOrDefault((w.WordId, f.ReadingIndex))?.FrequencyRank ?? int.MaxValue)
                    .ThenBy(f => f.ReadingIndex)
                    .FirstOrDefault();
                if (kanjiForm != null)
                    primaryKanjiText = kanjiForm.RubyText;
            }

            return new StaticDeckWordDto
            {
                WordId = w.WordId,
                ReadingIndex = (byte)bestForm.ReadingIndex,
                Text = bestForm.Text,
                RubyText = bestForm.RubyText,
                PrimaryKanjiText = primaryKanjiText,
                PartsOfSpeech = w.PartsOfSpeech,
                Meanings = firstDef?.EnglishMeanings ?? [],
                FrequencyRank = freq?.FrequencyRank ?? int.MaxValue,
                Occurrences = pw.Occurrences,
                DeckSortOrder = pw.SortOrder
            };
        }).Where(e => e != null).Cast<StaticDeckWordDto>().ToList();

        var response = new StaticDeckWordsResponse
        {
            DeckName = studyDeck.Name,
            Words = dtos
        };

        return Results.Ok(new PaginatedResponse<StaticDeckWordsResponse>(response, totalCount, pageSize, offset ?? 0));
    }

    [HttpGet("study-decks/{id:int}/vocabulary")]
    [SwaggerOperation(Summary = "Browse study deck vocabulary with full word details")]
    [ProducesResponseType(typeof(PaginatedResponse<List<WordDto>>), StatusCodes.Status200OK)]
    public async Task<IResult> GetStudyDeckVocabulary(
        int id,
        [FromQuery] int offset = 0,
        [FromQuery] string sortBy = "",
        [FromQuery] SortOrder sortOrder = SortOrder.Ascending,
        [FromQuery] string displayFilter = "all",
        [FromQuery] string? search = null,
        [FromQuery] string? pos = null,
        [FromQuery] string? excludePos = null,
        [FromQuery] bool hideKanaOnly = false)
    {
        const int pageSize = 100;
        if (offset < 0) return Results.BadRequest("Offset cannot be negative.");

        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var studyDeck = await userContext.UserStudyDecks
            .AsNoTracking()
            .FirstOrDefaultAsync(sd => sd.UserStudyDeckId == id && sd.UserId == userId);
        if (studyDeck == null) return Results.NotFound();

        List<(int WordId, short ReadingIndex, int Occurrences)> allItems;

        switch (studyDeck.DeckType)
        {
            case StudyDeckType.MediaDeck:
            {
                if (studyDeck.DeckId == null)
                    return Results.BadRequest("Media deck not linked.");

                var query = context.DeckWords.AsNoTracking()
                    .Where(dw => dw.DeckId == studyDeck.DeckId);

                if (!string.IsNullOrEmpty(studyDeck.PosFilter))
                {
                    var posTags = JsonSerializer.Deserialize<string[]>(studyDeck.PosFilter);
                    if (posTags is { Length: > 0 })
                    {
                        var wordIdsWithPos = context.JMDictWords.AsNoTracking()
                            .Where(w => w.PartsOfSpeech.Any(p => posTags.Contains(p)));
                        query = query.Where(dw => wordIdsWithPos.Any(w => w.WordId == dw.WordId));
                    }
                }

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var searchIds = await SearchHelper.ResolveSearchWordIds(context, search);
                    query = query.Where(dw => searchIds.Contains(dw.WordId));
                }

                switch ((DeckDownloadType)studyDeck.DownloadType)
                {
                    case DeckDownloadType.TopGlobalFrequency:
                        query = query.Where(dw => context.WordFormFrequencies
                            .Any(wff => wff.WordId == dw.WordId && wff.ReadingIndex == (short)dw.ReadingIndex &&
                                        wff.FrequencyRank >= studyDeck.MinFrequency && wff.FrequencyRank <= studyDeck.MaxFrequency));
                        break;
                    case DeckDownloadType.TopDeckFrequency:
                    {
                        var rangeIds = await context.DeckWords.AsNoTracking()
                            .Where(dw => dw.DeckId == studyDeck.DeckId)
                            .OrderByDescending(dw => dw.Occurrences)
                            .Skip(studyDeck.MinFrequency)
                            .Take(studyDeck.MaxFrequency - studyDeck.MinFrequency)
                            .Select(dw => dw.DeckWordId)
                            .ToListAsync();
                        query = query.Where(dw => rangeIds.Contains(dw.DeckWordId));
                        break;
                    }
                    case DeckDownloadType.TopChronological:
                    {
                        var rangeIds = await context.DeckWords.AsNoTracking()
                            .Where(dw => dw.DeckId == studyDeck.DeckId)
                            .OrderBy(dw => dw.DeckWordId)
                            .Skip(studyDeck.MinFrequency)
                            .Take(studyDeck.MaxFrequency - studyDeck.MinFrequency)
                            .Select(dw => dw.DeckWordId)
                            .ToListAsync();
                        query = query.Where(dw => rangeIds.Contains(dw.DeckWordId));
                        break;
                    }
                    case DeckDownloadType.OccurrenceCount:
                        if (studyDeck.MinOccurrences.HasValue)
                            query = query.Where(dw => dw.Occurrences >= studyDeck.MinOccurrences.Value);
                        if (studyDeck.MaxOccurrences.HasValue)
                            query = query.Where(dw => dw.Occurrences <= studyDeck.MaxOccurrences.Value);
                        break;
                }

                IOrderedQueryable<DeckWord> sorted = sortBy switch
                {
                    "deckFreq" => sortOrder == SortOrder.Ascending
                        ? query.OrderByDescending(d => d.Occurrences).ThenBy(d => d.DeckWordId)
                        : query.OrderBy(d => d.Occurrences).ThenBy(d => d.DeckWordId),
                    "globalFreq" => sortOrder == SortOrder.Ascending
                        ? query.OrderBy(d => context.WordFormFrequencies
                            .Where(wff => wff.WordId == d.WordId && wff.ReadingIndex == (short)d.ReadingIndex)
                            .Select(wff => wff.FrequencyRank)
                            .FirstOrDefault()).ThenBy(d => d.DeckWordId)
                        : query.OrderByDescending(d => context.WordFormFrequencies
                            .Where(wff => wff.WordId == d.WordId && wff.ReadingIndex == (short)d.ReadingIndex)
                            .Select(wff => wff.FrequencyRank)
                            .FirstOrDefault()).ThenBy(d => d.DeckWordId),
                    _ => sortOrder == SortOrder.Ascending
                        ? query.OrderBy(d => d.DeckWordId)
                        : query.OrderByDescending(d => d.DeckWordId),
                };

                var mediaDeckItems = await sorted
                    .Select(d => new { d.WordId, ReadingIndex = (short)d.ReadingIndex, d.Occurrences })
                    .ToListAsync();
                allItems = mediaDeckItems.Select(d => (d.WordId, d.ReadingIndex, d.Occurrences)).ToList();

                if ((DeckDownloadType)studyDeck.DownloadType == DeckDownloadType.TargetCoverage && studyDeck.TargetPercentage.HasValue)
                {
                    var deck = await context.Decks.AsNoTracking()
                        .FirstOrDefaultAsync(d => d.DeckId == studyDeck.DeckId);
                    if (deck != null)
                    {
                        var (_, targetKeys) = await deckWordResolver.CountTargetCoverageWords(
                            studyDeck.DeckId!.Value, deck, studyDeck.TargetPercentage.Value, false, studyDeck.PosFilter, studyDeck.StartFromKnown);
                        allItems = allItems.Where(i => targetKeys.Contains(WordFormHelper.EncodeWordKey(i.WordId, i.ReadingIndex))).ToList();
                    }
                }

                if (studyDeck.ExcludeKana)
                {
                    var kanaFormKeys = await WordFormHelper.GetKanaFormKeys(context, allItems.Select(i => i.WordId).Distinct());
                    if (kanaFormKeys.Count > 0)
                        allItems = allItems.Where(i => !kanaFormKeys.Contains(WordFormHelper.EncodeWordKey(i.WordId, i.ReadingIndex))).ToList();
                }

                break;
            }

            case StudyDeckType.GlobalDynamic:
            {
                var freqQuery = context.WordFormFrequencies.AsNoTracking().AsQueryable();
                if (studyDeck.MinGlobalFrequency.HasValue)
                    freqQuery = freqQuery.Where(wff => wff.FrequencyRank >= studyDeck.MinGlobalFrequency.Value);
                if (studyDeck.MaxGlobalFrequency.HasValue)
                    freqQuery = freqQuery.Where(wff => wff.FrequencyRank <= studyDeck.MaxGlobalFrequency.Value);

                if (!string.IsNullOrEmpty(studyDeck.PosFilter))
                {
                    var posTags = JsonSerializer.Deserialize<string[]>(studyDeck.PosFilter);
                    if (posTags is { Length: > 0 })
                    {
                        var wordIdsWithPos = context.JMDictWords.AsNoTracking()
                            .Where(w => w.PartsOfSpeech.Any(p => posTags.Contains(p)));
                        freqQuery = freqQuery.Where(wff => wordIdsWithPos.Any(w => w.WordId == wff.WordId));
                    }
                }

                if (studyDeck.ExcludeKana)
                    freqQuery = freqQuery.Where(wff => context.WordForms
                        .Any(wf => wf.WordId == wff.WordId && wf.ReadingIndex == wff.ReadingIndex && wf.FormType != JmDictFormType.KanaForm));

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var searchIds = await SearchHelper.ResolveSearchWordIds(context, search);
                    freqQuery = freqQuery.Where(wff => searchIds.Contains(wff.WordId));
                }

                var freqSorted = sortOrder == SortOrder.Ascending
                    ? freqQuery.OrderBy(wff => wff.FrequencyRank)
                    : freqQuery.OrderByDescending(wff => wff.FrequencyRank);

                var globalItems = await freqSorted
                    .Select(wff => new { wff.WordId, wff.ReadingIndex, Occurrences = 1 })
                    .ToListAsync();
                allItems = globalItems.Select(d => (d.WordId, d.ReadingIndex, d.Occurrences)).ToList();

                break;
            }

            case StudyDeckType.StaticWordList:
            {
                var query = userContext.UserStudyDeckWords.AsNoTracking()
                    .Where(w => w.UserStudyDeckId == id);

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var searchIds = await SearchHelper.ResolveSearchWordIds(context, search);
                    var searchSet = searchIds.ToHashSet();
                    query = query.Where(w => searchSet.Contains(w.WordId));
                }

                bool needsInMemorySort = sortBy == "globalFreq";

                var dbSorted = sortBy switch
                {
                    "occurrences" => sortOrder == SortOrder.Ascending
                        ? query.OrderBy(w => w.Occurrences).ThenBy(w => w.SortOrder)
                        : query.OrderByDescending(w => w.Occurrences).ThenBy(w => w.SortOrder),
                    _ => sortOrder == SortOrder.Ascending
                        ? query.OrderBy(w => w.SortOrder)
                        : query.OrderByDescending(w => w.SortOrder),
                };

                var rawItems = await dbSorted
                    .Select(w => new { w.WordId, w.ReadingIndex, w.Occurrences })
                    .ToListAsync();

                allItems = rawItems.Select(d => (d.WordId, d.ReadingIndex, d.Occurrences)).ToList();

                if (needsInMemorySort)
                {
                    var wordIds = allItems.Select(i => i.WordId).Distinct().ToList();
                    var freqMap = await WordFormHelper.LoadWordFormFrequencies(context, wordIds);
                    allItems = (sortOrder == SortOrder.Ascending
                        ? allItems.OrderBy(i => freqMap.TryGetValue((i.WordId, i.ReadingIndex), out var f) ? f.FrequencyRank : int.MaxValue)
                        : allItems.OrderByDescending(i => freqMap.TryGetValue((i.WordId, i.ReadingIndex), out var f) ? f.FrequencyRank : int.MaxValue))
                        .ToList();
                }

                if (studyDeck.ExcludeKana)
                {
                    var kanaFormKeys = await WordFormHelper.GetKanaFormKeys(context, allItems.Select(i => i.WordId).Distinct());
                    if (kanaFormKeys.Count > 0)
                        allItems = allItems.Where(i => !kanaFormKeys.Contains(WordFormHelper.EncodeWordKey(i.WordId, i.ReadingIndex))).ToList();
                }

                break;
            }

            default:
                return Results.BadRequest("Unknown deck type.");
        }

        var posMatchIds = await VocabularyFilterHelper.GetPosFilteredWordIds(
            context, pos, allItems.Select(i => i.WordId));
        if (posMatchIds != null)
            allItems = allItems.Where(i => posMatchIds.Contains(i.WordId)).ToList();

        var posExcludeIds = await VocabularyFilterHelper.GetPosFilteredWordIds(
            context, excludePos, allItems.Select(i => i.WordId));
        if (posExcludeIds != null)
            allItems = allItems.Where(i => !posExcludeIds.Contains(i.WordId)).ToList();

        if (hideKanaOnly)
        {
            var kanaFormKeys = await WordFormHelper.GetKanaFormKeys(context, allItems.Select(i => i.WordId).Distinct());
            if (kanaFormKeys.Count > 0)
                allItems = allItems
                    .Where(i => !kanaFormKeys.Contains(WordFormHelper.EncodeWordKey(i.WordId, i.ReadingIndex)))
                    .ToList();
        }

        bool needsKnownFilter = currentUserService.IsAuthenticated
            && !string.IsNullOrEmpty(displayFilter)
            && displayFilter != "all";

        if (needsKnownFilter)
        {
            var wordKeys = allItems.Select(i => (i.WordId, (byte)i.ReadingIndex)).ToList();
            var knownStates = await currentUserService.GetKnownWordsState(wordKeys);

            allItems = allItems.Where(i =>
            {
                var key = (i.WordId, (byte)i.ReadingIndex);
                var states = knownStates.GetValueOrDefault(key, [KnownState.New]);
                return displayFilter switch
                {
                    "known" => !states.Contains(KnownState.New),
                    "young" => states.Contains(KnownState.Young),
                    "mature" => states.Contains(KnownState.Mature),
                    "mastered" => states.Contains(KnownState.Mastered),
                    "blacklisted" => states.Contains(KnownState.Blacklisted),
                    "unknown" => states.Contains(KnownState.New),
                    _ => true
                };
            }).ToList();
        }

        int totalCount = allItems.Count;
        var pagedItems = allItems.Skip(offset).Take(pageSize).ToList();

        var pagedWordIds = pagedItems.Select(p => p.WordId).Distinct().ToList();
        var formDict = await WordFormHelper.LoadWordForms(context, pagedWordIds);
        var formFreqDict = await WordFormHelper.LoadWordFormFrequencies(context, pagedWordIds);

        var words = await context.JMDictWords
            .AsNoTracking()
            .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
            .Where(w => pagedWordIds.Contains(w.WordId))
            .ToDictionaryAsync(w => w.WordId);

        var knownWordStates = await currentUserService.GetKnownWordsState(
            pagedItems.Select(p => (p.WordId, (byte)p.ReadingIndex)));

        var vocabulary = pagedItems
            .Where(p => words.ContainsKey(p.WordId))
            .Select(p =>
            {
                var word = words[p.WordId];
                var readingIndex = (byte)p.ReadingIndex;
                var form = formDict.GetValueOrDefault((p.WordId, p.ReadingIndex));
                var formFreq = formFreqDict.GetValueOrDefault((p.WordId, p.ReadingIndex));

                var mainReading = form != null
                    ? WordFormHelper.ToFormDto(form, formFreq)
                    : new WordFormDto { ReadingIndex = readingIndex };

                return new WordDto
                {
                    WordId = p.WordId,
                    MainReading = mainReading,
                    AlternativeReadings = [],
                    Definitions = word.Definitions.ToDefinitionDtos(),
                    PartsOfSpeech = word.PartsOfSpeech.ToHumanReadablePartsOfSpeech(),
                    Occurrences = p.Occurrences,
                    PitchAccents = word.PitchAccents,
                    KnownStates = knownWordStates.GetValueOrDefault((p.WordId, readingIndex), [KnownState.New])
                };
            })
            .ToList();

        return Results.Ok(new PaginatedResponse<List<WordDto>>(vocabulary, totalCount, pageSize, offset));
    }

    [HttpPost("study-decks/import/preview")]
    [SwaggerOperation(Summary = "Parse file and preview import matches")]
    [RequestSizeLimit(50 * 1024 * 1024)]
    public async Task<IResult> ImportPreview(IFormFile file, [FromForm] bool parseFullText = false)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        if (file.Length == 0) return Results.BadRequest("File is empty.");
        if (file.Length > 50 * 1024 * 1024) return Results.BadRequest("File exceeds 50 MB limit.");

        await using var stream = file.OpenReadStream();
        var result = await importService.ParseAndPreview(stream, file.FileName, parseFullText);
        return Results.Ok(result);
    }

    [HttpPost("study-decks/import")]
    [SwaggerOperation(Summary = "Commit import as new static word list deck")]
    public async Task<IResult> ImportCommit(ImportCommitRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest("Name is required.");
        if (string.IsNullOrWhiteSpace(request.PreviewToken))
            return Results.BadRequest("Preview token is required.");

        var result = await importService.CommitImport(userId, request);
        if (result.Error != null) return Results.BadRequest(result.Error);
        await sessionService.BumpStudyOverviewVersion(userId);

        return Results.Ok(new { userStudyDeckId = result.DeckId });
    }

    [HttpPost("study-decks/import/preview-text")]
    [SwaggerOperation(Summary = "Parse text lines and preview import matches")]
    public async Task<IResult> ImportPreviewText([FromBody] ImportPreviewTextRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        if (request.Lines == null || request.Lines.Count == 0)
            return Results.BadRequest("No text provided.");
        if (request.Lines.Count > 50_000)
            return Results.BadRequest("Maximum of 50,000 lines.");

        var result = await importService.ParseAndPreviewText(request.Lines, request.ParseFullText);
        return Results.Ok(result);
    }

    [HttpPost("study-decks/{id:int}/import")]
    [SwaggerOperation(Summary = "Import words from preview token into an existing static deck")]
    public async Task<IResult> ImportToExistingDeck(int id, [FromBody] ImportToExistingRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var (studyDeck, error) = await GetStaticDeckForUser(id, "imported into");
        if (error != null) return error;

        if (string.IsNullOrWhiteSpace(request.PreviewToken))
            return Results.BadRequest("Preview token is required.");

        var result = await importService.ImportToExistingDeck(userId, id, request);
        if (result.Error != null) return Results.BadRequest(result.Error);
        await sessionService.BumpStudyOverviewVersion(userId);

        return Results.Ok(new { added = result.DeckId != null });
    }

    [HttpPost("study-decks/preview-count")]
    [SwaggerOperation(Summary = "Preview word count for study deck filters")]
    public async Task<IResult> PreviewStudyDeckCount(AddStudyDeckRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        List<(int WordId, byte ReadingIndex)> wordPairs;

        if (request.DeckType == StudyDeckType.GlobalDynamic)
        {
            var result = await deckWordResolver.ResolveGlobalDynamicWords(
                request.MinGlobalFrequency, request.MaxGlobalFrequency, request.PosFilter,
                request.ExcludeKana, false, false);
            wordPairs = result.Words.Select(w => (w.WordId, w.ReadingIndex)).ToList();
        }
        else if (request.DeckType == StudyDeckType.StaticWordList)
        {
            return Results.BadRequest("Preview count is not supported for static word list decks.");
        }
        else // MediaDeck (default)
        {
            if (!request.DeckId.HasValue)
                return Results.BadRequest("DeckId is required for media deck preview.");

            var deck = await context.Decks.AsNoTracking().FirstOrDefaultAsync(d => d.DeckId == request.DeckId.Value);
            if (deck == null) return Results.NotFound("Deck not found.");

            if ((DeckDownloadType)request.DownloadType == DeckDownloadType.TargetCoverage && request.TargetPercentage.HasValue)
            {
                var (_, targetKeys) = await deckWordResolver.CountTargetCoverageWords(
                    request.DeckId.Value, deck, request.TargetPercentage.Value, false, request.PosFilter, request.StartFromKnown);
                if (targetKeys.Count == 0) return Results.Ok(new { total = 0, unlearned = 0 });

                var allDeckWords = await context.DeckWords.AsNoTracking()
                    .Where(dw => dw.DeckId == request.DeckId.Value)
                    .Select(dw => new { dw.WordId, dw.ReadingIndex })
                    .ToListAsync();

                wordPairs = allDeckWords
                    .Where(dw => targetKeys.Contains(WordFormHelper.EncodeWordKey(dw.WordId, dw.ReadingIndex)))
                    .Select(dw => (dw.WordId, dw.ReadingIndex))
                    .ToList();
            }
            else
            {
                var (words, error) = await deckWordResolver.ResolveDeckWords(new DeckWordResolveRequest(
                    request.DeckId.Value, deck,
                    (DeckDownloadType)request.DownloadType, (DeckOrder)request.Order,
                    request.MinFrequency, request.MaxFrequency,
                    false, false,
                    request.TargetPercentage,
                    request.MinOccurrences, request.MaxOccurrences,
                    request.PosFilter, request.StartFromKnown));

                if (error != null) return error;
                if (words == null || words.Count == 0) return Results.Ok(0);

                wordPairs = words.Select(w => (w.WordId, w.ReadingIndex)).ToList();
            }
        }

        if (wordPairs.Count == 0) return Results.Ok(new { total = 0, unlearned = 0 });

        if (request.ExcludeKana)
        {
            var kanaFormKeys = await WordFormHelper.GetKanaFormKeys(context, wordPairs.Select(w => w.WordId));
            if (kanaFormKeys.Count > 0)
                wordPairs = wordPairs
                    .Where(w => !kanaFormKeys.Contains(WordFormHelper.EncodeWordKey(w.WordId, w.ReadingIndex)))
                    .ToList();
        }

        var total = wordPairs.Count;

        var wordIds = wordPairs.Select(w => w.WordId).Distinct().ToList();
        var learnedSet = await userContext.FsrsCards
            .Where(c => c.UserId == userId && wordIds.Contains(c.WordId))
            .Select(c => new { c.WordId, c.ReadingIndex, c.State })
            .ToListAsync();
        var cardStateMap = new Dictionary<(int, byte), FsrsState>();
        foreach (var c in learnedSet)
            cardStateMap[(c.WordId, c.ReadingIndex)] = c.State;

        var wordSetStates = await currentUserService.GetWordSetDerivedStates();
        var trackedKeys = new HashSet<(int, byte)>(cardStateMap.Keys);

        foreach (var (key, _) in wordSetStates)
        {
            if (!trackedKeys.Contains(key))
                trackedKeys.Add(key);
        }

        foreach (var ((wordId, ri), state) in cardStateMap)
        {
            if (state is FsrsState.New) continue;
            var kanaIndexes = wordFormCache.GetKanaIndexesForKanji(wordId, ri);
            if (kanaIndexes == null) continue;
            foreach (var kanaRi in kanaIndexes)
            {
                if (!trackedKeys.Contains((wordId, kanaRi)))
                    trackedKeys.Add((wordId, kanaRi));
            }
        }

        var unlearned = wordPairs.Count(w => !trackedKeys.Contains((w.WordId, (byte)w.ReadingIndex)));

        return Results.Ok(new { total, unlearned });
    }

    [HttpGet("study-batch")]
    [SwaggerOperation(Summary = "Get a batch of cards for study")]
    public async Task<IResult> GetStudyBatch(
        [FromQuery] int limit = 20,
        [FromQuery] string? sessionId = null,
        [FromQuery] int? extraNewCards = null,
        [FromQuery] int? extraReviews = null,
        [FromQuery] int? aheadMinutes = null,
        [FromQuery] int? mistakeDays = null)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        if (aheadMinutes.HasValue && mistakeDays.HasValue)
            return Results.BadRequest("Cannot combine aheadMinutes and mistakeDays");

        if (extraNewCards.HasValue) extraNewCards = Math.Clamp(extraNewCards.Value, 0, 200);
        if (extraReviews.HasValue) extraReviews = Math.Clamp(extraReviews.Value, 0, 500);
        if (aheadMinutes.HasValue) aheadMinutes = Math.Clamp(aheadMinutes.Value, 60, 10080);
        if (mistakeDays.HasValue) mistakeDays = Math.Clamp(mistakeDays.Value, 1, 7);

        if (!string.IsNullOrEmpty(sessionId) && await sessionService.ValidateSession(sessionId, userId))
            await sessionService.RefreshSession(sessionId);
        else
            sessionId = await sessionService.CreateSession(userId);

        var settings = await LoadStudySettings(userId);
        limit = Math.Clamp(limit, 1, settings.BatchSize);
        var now = DateTime.UtcNow;
        var (todayStart, _) = ResolveTimezone(now, settings.Timezone);

        var todayStats = await userContext.FsrsCards
            .Where(c => c.UserId == userId)
            .SelectMany(c => c.ReviewLogs)
            .Where(l => l.ReviewDateTime >= todayStart)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                ReviewsToday = g.Count(),
                UniqueCardsToday = g.Select(l => l.CardId).Distinct().Count(),
                NewCardsToday = g.Select(l => l.Card)
                    .Where(c => c.CreatedAt >= todayStart)
                    .Select(l => l.CardId)
                    .Distinct()
                    .Count()
            })
            .FirstOrDefaultAsync();

        var newCardsToday = todayStats?.NewCardsToday ?? 0;
        var reviewsToday = settings.CountFailedReviews
            ? todayStats?.ReviewsToday ?? 0
            : todayStats?.UniqueCardsToday ?? 0;


        var newCardBudget = extraNewCards ?? Math.Max(0, settings.NewCardsPerDay - newCardsToday);
        if (aheadMinutes.HasValue || mistakeDays.HasValue)
            newCardBudget = 0;

        // ── Phase 1: Build kanji knowledge set for kana redundancy ──
        var knownKanjiWordIds = new HashSet<int>();
        HashSet<long>? existingKeys = newCardBudget > 0 ? new HashSet<long>() : null;

        // Both collections are only consumed during new-card selection (Phase 4, gated on
        // newCardBudget > 0), so skip the full-table scan + word-set query on pure-review fetches.
        if (newCardBudget > 0)
        {
            await foreach (var c in userContext.FsrsCards
                .AsNoTracking()
                .Where(c => c.UserId == userId)
                .Select(c => new { c.WordId, c.ReadingIndex })
                .AsAsyncEnumerable())
            {
                if (wordFormCache.GetKanaIndexesForKanji(c.WordId, c.ReadingIndex) != null)
                    knownKanjiWordIds.Add(c.WordId);

                existingKeys?.Add(WordFormHelper.EncodeWordKey(c.WordId, c.ReadingIndex));
            }

            var wordSetStates = await userContext.UserWordSetStates
                .AsNoTracking()
                .Where(uwss => uwss.UserId == userId)
                .Select(uwss => new { uwss.SetId, uwss.State })
                .ToListAsync();

            if (wordSetStates.Count > 0)
            {
                var allSetIds = wordSetStates.Select(s => s.SetId).ToList();
                var masteredSetIdSet = wordSetStates
                    .Where(s => s.State == WordSetStateType.Mastered)
                    .Select(s => s.SetId)
                    .ToHashSet();

                var setMembers = await context.WordSetMembers
                    .AsNoTracking()
                    .Where(wsm => allSetIds.Contains(wsm.SetId))
                    .Select(wsm => new { wsm.SetId, wsm.WordId, wsm.ReadingIndex })
                    .ToListAsync();

                foreach (var m in setMembers)
                {
                    if (masteredSetIdSet.Contains(m.SetId)
                        && wordFormCache.GetKanaIndexesForKanji(m.WordId, (byte)m.ReadingIndex) != null)
                        knownKanjiWordIds.Add(m.WordId);
                    existingKeys?.Add(WordFormHelper.EncodeWordKey(m.WordId, (byte)m.ReadingIndex));
                }
            }
        }

        // ── Load study decks (used by both review filtering and new card selection) ──
        var studyDecks = await userContext.UserStudyDecks
            .AsNoTracking()
            .Where(sd => sd.UserId == userId)
            .OrderBy(sd => sd.SortOrder)
            .ToListAsync();

        // ── Phase 3: Collect due reviews ──
        var baseCutoff = GetDueCutoff(now, settings);
        var dueCutoff = aheadMinutes.HasValue ? baseCutoff.AddMinutes(aheadMinutes.Value) : baseCutoff;
        var batch = new List<(int WordId, byte ReadingIndex, long CardId, bool IsNew, int State)>();
        var dueCardLookup = new Dictionary<(int, byte), FsrsCard>();

        var reviewBudget = extraReviews ?? Math.Max(0, settings.MaxReviewsPerDay - reviewsToday);
        var totalDueCount = 0;
        if (reviewBudget > 0 && mistakeDays.HasValue)
        {
            // ── Study More: Recent Mistakes mode ──
            // Find cards rated Again in the last N days that have since graduated to Review and aren't due yet
            var mistakeCutoff = now.AddDays(-mistakeDays.Value);

            var mistakeCardIds = await userContext.FsrsReviewLogs
                .AsNoTracking()
                .Where(l => l.Card.UserId == userId
                            && l.Rating == FsrsRating.Again
                            && l.ReviewDateTime >= mistakeCutoff)
                .Select(l => l.CardId)
                .Distinct()
                .ToListAsync();

            if (mistakeCardIds.Count > 0)
            {
                var mistakeCards = await userContext.FsrsCards
                    .AsNoTracking()
                    .Where(c => mistakeCardIds.Contains(c.CardId)
                                && c.State == FsrsState.Review
                                && c.Due > now
                                && (!c.LastReview.HasValue || c.LastReview < todayStart))
                    .OrderBy(c => c.Due)
                    .Take(reviewBudget)
                    .ToListAsync();

                totalDueCount = mistakeCards.Count;
                foreach (var card in mistakeCards)
                {
                    batch.Add((card.WordId, card.ReadingIndex, card.CardId, false, (int)card.State));
                    dueCardLookup[(card.WordId, card.ReadingIndex)] = card;
                }
            }
        }
        else if (reviewBudget > 0)
        {
            var dueQuery = userContext.FsrsCards
                .AsNoTracking()
                .Where(c => c.UserId == userId
                            && c.State != FsrsState.Blacklisted
                            && c.State != FsrsState.Mastered
                            && c.State != FsrsState.Suspended
                            && c.Due <= dueCutoff);

            HashSet<long>? studyDeckWordKeys = null;
            var activeStudyDecks = studyDecks.Where(sd => sd.IsActive).ToList();
            var globalDynamicDecks = activeStudyDecks
                .Where(sd => sd.DeckType == StudyDeckType.GlobalDynamic).ToList();

            if (settings.ReviewFrom == StudyReviewFrom.StudyDecksOnly)
            {
                var mediaDecks = activeStudyDecks
                    .Where(sd => sd.DeckType == StudyDeckType.MediaDeck && sd.DeckId.HasValue)
                    .ToList();
                studyDeckWordKeys = await GetFilteredMediaWordKeys(mediaDecks);

                var staticDeckIds = activeStudyDecks
                    .Where(sd => sd.DeckType == StudyDeckType.StaticWordList)
                    .Select(sd => sd.UserStudyDeckId).ToList();
                if (staticDeckIds.Count > 0)
                    studyDeckWordKeys.UnionWith(await deckWordResolver.GetStaticDeckWordKeys(staticDeckIds));
            }

            List<FsrsCard> dueCards;

            if (studyDeckWordKeys != null)
            {
                var allDueCards = await dueQuery.ToListAsync();

                if (globalDynamicDecks.Count > 0)
                {
                    var unmatchedWordIds = allDueCards
                        .Where(c => !studyDeckWordKeys.Contains(WordFormHelper.EncodeWordKey(c.WordId, c.ReadingIndex)))
                        .Select(c => (int)c.WordId)
                        .Distinct()
                        .ToList();

                    if (unmatchedWordIds.Count > 0)
                    {
                        foreach (var gd in globalDynamicDecks)
                        {
                            studyDeckWordKeys.UnionWith(await deckWordResolver.GetGlobalDynamicWordKeysForWordIds(
                                gd.MinGlobalFrequency, gd.MaxGlobalFrequency, gd.PosFilter, unmatchedWordIds, gd.ExcludeKana));
                        }
                    }
                }

                allDueCards = allDueCards
                    .Where(c => studyDeckWordKeys.Contains(WordFormHelper.EncodeWordKey(c.WordId, c.ReadingIndex)))
                    .ToList();

                totalDueCount = allDueCards.Count;
                dueCards = allDueCards.OrderBy(c => c.Due).Take(reviewBudget).ToList();
            }
            else
            {
                totalDueCount = await dueQuery.CountAsync();
                dueCards = await dueQuery
                    .OrderBy(c => c.Due)
                    .Take(reviewBudget)
                    .ToListAsync();
            }

            // Sort: learning/relearning steps first (by due), then reviews by relative overdueness
            // Add slight randomness so batches don't always come in the exact same order
            var rng = Random.Shared;
            dueCards = dueCards
                .OrderBy(c => c.State is FsrsState.Learning or FsrsState.Relearning ? 0 : 1)
                .ThenByDescending(c =>
                {
                    double overdueness;
                    if (c.Stability is > 0 && c.LastReview.HasValue)
                    {
                        var elapsed = (now - c.LastReview.Value).TotalDays;
                        overdueness = elapsed / c.Stability.Value;
                    }
                    else
                    {
                        overdueness = (now - c.Due).TotalDays;
                    }
                    // ±10% jitter so similarly-overdue cards shuffle around between sessions
                    return overdueness * (0.9 + rng.NextDouble() * 0.2);
                })
                .ToList();

            foreach (var card in dueCards)
            {
                batch.Add((card.WordId, card.ReadingIndex, card.CardId, false, (int)card.State));
                dueCardLookup[(card.WordId, card.ReadingIndex)] = card;
            }
        }

        // ── Phase 4: Resolve new word candidates from study decks ──
        var sourceDeckNames = new Dictionary<long, string>();
        if (newCardBudget > 0)
        {
            var activeDecks = studyDecks.Where(sd => sd.IsActive).ToList();
            var mediaDeckIds = activeDecks.Where(sd => sd.DeckType == StudyDeckType.MediaDeck && sd.DeckId.HasValue).Select(sd => sd.DeckId!.Value).ToList();
            var deckMap = await context.Decks.AsNoTracking()
                .Where(d => mediaDeckIds.Contains(d.DeckId))
                .ToDictionaryAsync(d => d.DeckId);

            var isCrossDeck = settings.NewCardGathering == StudyNewCardGathering.CrossDeckFrequency;
            var isRoundRobin = settings.NewCardGathering == StudyNewCardGathering.RoundRobin;
            var perDeckCandidates = new List<List<(int WordId, byte ReadingIndex)>>();
            var totalCandidates = 0;
            var allEligibleMediaKeys = isCrossDeck ? new HashSet<long>() : null;
            var nonMediaCandidates = isCrossDeck ? new List<(int WordId, byte ReadingIndex)>() : null;

            foreach (var studyDeck in activeDecks)
            {
                List<(int WordId, byte ReadingIndex)>? wordPairs = null;

                if (studyDeck.DeckType == StudyDeckType.MediaDeck)
                {
                    if (!studyDeck.DeckId.HasValue || !deckMap.TryGetValue(studyDeck.DeckId.Value, out var deck)) continue;

                    var (words, error) = await deckWordResolver.ResolveDeckWords(new DeckWordResolveRequest(
                        studyDeck.DeckId.Value, deck,
                        (DeckDownloadType)studyDeck.DownloadType, (DeckOrder)studyDeck.Order,
                        studyDeck.MinFrequency, studyDeck.MaxFrequency,
                        false, false,
                        studyDeck.TargetPercentage,
                        studyDeck.MinOccurrences, studyDeck.MaxOccurrences,
                        studyDeck.PosFilter, studyDeck.StartFromKnown));

                    if (error != null || words == null) continue;
                    wordPairs = words.Select(w => (w.WordId, w.ReadingIndex)).ToList();
                }
                else if (studyDeck.DeckType == StudyDeckType.GlobalDynamic)
                {
                    var gdResult = await deckWordResolver.ResolveGlobalDynamicWords(
                        studyDeck.MinGlobalFrequency, studyDeck.MaxGlobalFrequency, studyDeck.PosFilter,
                        studyDeck.ExcludeKana, false, false);
                    wordPairs = gdResult.Words.Select(w => (w.WordId, w.ReadingIndex)).ToList();
                }
                else if (studyDeck.DeckType == StudyDeckType.StaticWordList)
                {
                    var resolved = await deckWordResolver.ResolveStaticDeckWords(studyDeck.UserStudyDeckId, studyDeck.Order);
                    wordPairs = resolved.Select(w => (w.WordId, w.ReadingIndex)).ToList();
                }

                if (wordPairs == null) continue;

                // Filter candidates inline: exclude tracked, redundant kana
                IEnumerable<(int WordId, byte ReadingIndex)> filtered = wordPairs;

                if (studyDeck.ExcludeKana)
                {
                    var kanaFormKeys = await WordFormHelper.GetKanaFormKeys(context, wordPairs.Select(w => w.WordId).Distinct());
                    if (kanaFormKeys.Count > 0)
                        filtered = wordPairs.Where(w => !kanaFormKeys.Contains(WordFormHelper.EncodeWordKey(w.WordId, w.ReadingIndex)));
                }

                var deckName = studyDeck.DeckType == StudyDeckType.MediaDeck && studyDeck.DeckId.HasValue && deckMap.TryGetValue(studyDeck.DeckId.Value, out var deckForName)
                    ? deckForName.OriginalTitle
                    : studyDeck.Name;

                var deckCandidates = new List<(int WordId, byte ReadingIndex)>();
                foreach (var word in filtered)
                {
                    var key = WordFormHelper.EncodeWordKey(word.WordId, word.ReadingIndex);
                    if (existingKeys!.Contains(key)) continue;

                    if (knownKanjiWordIds.Contains(word.WordId)
                        && wordFormCache.GetKanjiIndexesForKana(word.WordId, word.ReadingIndex) != null)
                        continue;

                    existingKeys!.Add(key);
                    deckCandidates.Add((word.WordId, word.ReadingIndex));
                    sourceDeckNames.TryAdd(key, deckName);
                }

                if (isCrossDeck)
                {
                    if (studyDeck.DeckType == StudyDeckType.MediaDeck)
                    {
                        foreach (var c in deckCandidates)
                            allEligibleMediaKeys!.Add(WordFormHelper.EncodeWordKey(c.WordId, c.ReadingIndex));
                    }
                    else
                    {
                        nonMediaCandidates!.AddRange(deckCandidates);
                    }
                }
                else
                {
                    if (deckCandidates.Count > 0)
                        perDeckCandidates.Add(deckCandidates);

                    totalCandidates += deckCandidates.Count;

                    if (!isRoundRobin && totalCandidates >= newCardBudget)
                        break;
                }
            }

            var candidates = new List<(int WordId, byte ReadingIndex)>();

            if (isCrossDeck)
            {
                if (allEligibleMediaKeys!.Count > 0)
                {
                    var crossDeckOccurrences = await context.DeckWords
                        .AsNoTracking()
                        .Where(dw => mediaDeckIds.Contains(dw.DeckId))
                        .GroupBy(dw => new { dw.WordId, dw.ReadingIndex })
                        .Select(g => new
                        {
                            g.Key.WordId,
                            g.Key.ReadingIndex,
                            TotalOccurrences = g.Sum(x => x.Occurrences)
                        })
                        .OrderByDescending(x => x.TotalOccurrences)
                        .ToListAsync();

                    foreach (var item in crossDeckOccurrences)
                    {
                        var key = WordFormHelper.EncodeWordKey(item.WordId, (byte)item.ReadingIndex);
                        if (allEligibleMediaKeys.Contains(key))
                            candidates.Add((item.WordId, (byte)item.ReadingIndex));
                    }
                }

                candidates.AddRange(nonMediaCandidates!);
            }
            else if (isRoundRobin && perDeckCandidates.Count > 1)
            {
                var indexes = new int[perDeckCandidates.Count];
                var exhausted = 0;
                while (exhausted < perDeckCandidates.Count)
                {
                    for (var d = 0; d < perDeckCandidates.Count; d++)
                    {
                        if (indexes[d] >= perDeckCandidates[d].Count) continue;
                        candidates.Add(perDeckCandidates[d][indexes[d]++]);
                        if (indexes[d] >= perDeckCandidates[d].Count) exhausted++;
                    }
                }
            }
            else
            {
                foreach (var deckCandidates in perDeckCandidates)
                    candidates.AddRange(deckCandidates);
            }

            var taken = candidates.Take(newCardBudget).ToList();
            foreach (var c in taken)
                batch.Add((c.WordId, c.ReadingIndex, 0, true, (int)FsrsState.New));
        }

        if (batch.Count == 0)
        {
            return Results.Ok(new StudyBatchResponse
            {
                SessionId = sessionId,
                NewCardsRemaining = Math.Max(0, settings.NewCardsPerDay - newCardsToday),
                ReviewsRemaining = 0,
                NewCardsToday = newCardsToday,
                ReviewsToday = reviewsToday
            });
        }

        // ── Phase 5: Interleave and build response ──
        var ordered = settings.Interleaving switch
        {
            StudyInterleaving.NewFirst => batch.OrderBy(c => !c.IsNew).ThenBy(c => c.CardId).ToList(),
            StudyInterleaving.ReviewsFirst => batch.OrderBy(c => c.IsNew).ThenBy(c => c.CardId).ToList(),
            _ => InterleaveMixed(batch)
        };

        ordered = ordered.Take(limit).ToList();

        var wordIds = ordered.Select(c => c.WordId).Distinct().ToList();

        var wordsDataTask = context.JMDictWords
            .AsNoTracking()
            .Include(w => w.Definitions)
            .Where(w => wordIds.Contains(w.WordId))
            .ToDictionaryAsync(w => w.WordId);
        var confusablePairs = ordered.Select(c => (c.WordId, c.ReadingIndex)).Distinct().ToList();
        var confusablesTask = ConfusableReadingsHelper.LoadBatchConfusableReadings(contextFactory, confusablePairs);

        var wordsData = await wordsDataTask;
        var wordForms = await context.WordForms
            .AsNoTracking()
            .Where(wf => wordIds.Contains(wf.WordId))
            .ToListAsync();
        RubyTextHelper.EnrichForms(wordForms);
        var wordFormsMap = wordForms.GroupBy(wf => wf.WordId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var freqs = await WordFormHelper.LoadWordFormFrequencies(context, wordIds);
        var confusables = await confusablesTask;

        var occDeckIds = studyDecks.Where(sd => sd.DeckId.HasValue).Select(sd => sd.DeckId!.Value).ToList();
        var deckOccurrences = occDeckIds.Count > 0
            ? await context.DeckWords
                .AsNoTracking()
                .Where(dw => occDeckIds.Contains(dw.DeckId) && wordIds.Contains(dw.WordId))
                .Select(dw => new { dw.DeckId, dw.WordId, dw.ReadingIndex, dw.Occurrences })
                .ToListAsync()
            : new();
        var occurrenceMap = deckOccurrences
            .GroupBy(dw => WordFormHelper.EncodeWordKey(dw.WordId, dw.ReadingIndex))
            .ToDictionary(g => g.Key, g => g.ToList());

        var occurrenceDeckIds = deckOccurrences.Select(dw => dw.DeckId).Distinct().ToList();
        var occurrenceDecks = occurrenceDeckIds.Count > 0
            ? await context.Decks.AsNoTracking()
                .Where(d => occurrenceDeckIds.Contains(d.DeckId))
                .Select(d => new { d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.ParentDeckId })
                .ToDictionaryAsync(d => d.DeckId)
            : new();

        var occParentDeckIds = occurrenceDecks.Values.Where(d => d.ParentDeckId.HasValue).Select(d => d.ParentDeckId!.Value).Distinct().ToList();
        var occParentDecks = occParentDeckIds.Count > 0
            ? await context.Decks.AsNoTracking()
                .Where(d => occParentDeckIds.Contains(d.DeckId))
                .Select(d => new { d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle })
                .ToDictionaryAsync(d => d.DeckId)
            : new();

        var cards = new List<StudyCardDto>();
        foreach (var item in ordered)
        {
            wordsData.TryGetValue(item.WordId, out var word);
            wordFormsMap.TryGetValue(item.WordId, out var forms);
            freqs.TryGetValue((item.WordId, (short)item.ReadingIndex), out var freq);

            var mainForm = forms?.FirstOrDefault(f => f.ReadingIndex == item.ReadingIndex);
            var exKey = WordFormHelper.EncodeWordKey(item.WordId, item.ReadingIndex);

            cards.Add(new StudyCardDto
            {
                CardId = item.CardId,
                WordId = item.WordId,
                ReadingIndex = item.ReadingIndex,
                State = item.State,
                IsNewCard = item.IsNew,
                WordText = mainForm?.RubyText ?? mainForm?.Text ?? "",
                WordTextPlain = mainForm?.Text ?? "",
                Readings = forms?.Select(f => new StudyReadingDto
                {
                    Text = f.Text,
                    RubyText = f.RubyText,
                    ReadingIndex = f.ReadingIndex,
                    FormType = (int)f.FormType
                }).ToList() ?? new(),
                Definitions = word?.Definitions
                    .Where(d => d.EnglishMeanings.Count > 0)
                    .OrderBy(d => d.SenseIndex)
                    .Select((d, i) => new StudyDefinitionDto
                    {
                        Index = i + 1,
                        Meanings = d.EnglishMeanings.ToArray(),
                        PartsOfSpeech = d.PartsOfSpeech.ToHumanReadablePartsOfSpeech().ToArray()
                    }).ToList() ?? new(),
                PartsOfSpeech = (word?.PartsOfSpeech.ToHumanReadablePartsOfSpeech() ?? []).ToArray(),
                PitchAccents = word?.PitchAccents?.ToArray(),
                FrequencyRank = freq?.FrequencyRank ?? 0,
                DeckOccurrences = occurrenceMap.TryGetValue(exKey, out var occs)
                    ? occs
                        .OrderByDescending(o => o.Occurrences)
                        .Where(o => occurrenceDecks.ContainsKey(o.DeckId))
                        .Select(o =>
                        {
                            var occDeck = occurrenceDecks[o.DeckId];
                            var dto = new StudyDeckOccurrenceDto
                            {
                                DeckId = o.DeckId,
                                OriginalTitle = occDeck.OriginalTitle,
                                RomajiTitle = occDeck.RomajiTitle,
                                EnglishTitle = occDeck.EnglishTitle,
                                Occurrences = o.Occurrences
                            };
                            if (occDeck.ParentDeckId != null && occParentDecks.TryGetValue(occDeck.ParentDeckId.Value, out var occParent))
                            {
                                dto.ParentOriginalTitle = occParent.OriginalTitle;
                                dto.ParentRomajiTitle = occParent.RomajiTitle;
                                dto.ParentEnglishTitle = occParent.EnglishTitle;
                            }
                            return dto;
                        }).ToList()
                    : null,
                SourceDeckName = item.IsNew && sourceDeckNames.TryGetValue(exKey, out var srcName) ? srcName : null,
                ConfusableReadings = confusables.GetValueOrDefault((item.WordId, item.ReadingIndex))
            });
        }

        if (settings.ShowNextInterval)
        {
            var userSettings = await userContext.UserFsrsSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.UserId == userId);
            var fsrsParams = userSettings?.Parameters is { Length: > 0 } p ? p : FsrsConstants.DefaultParameters;
            var desiredRetention = userSettings?.DesiredRetention is double dr and > 0 and < 1 ? dr : FsrsConstants.DefaultDesiredRetention;
            var previewScheduler = new FsrsScheduler(desiredRetention: desiredRetention, parameters: fsrsParams, enableFuzzing: false);

            var now2 = DateTime.UtcNow;
            foreach (var dto in cards)
            {
                FsrsCard fsrsCard;
                if (dueCardLookup.TryGetValue((dto.WordId, (byte)dto.ReadingIndex), out var existing))
                    fsrsCard = existing;
                else
                    fsrsCard = new FsrsCard(userId, dto.WordId, (byte)dto.ReadingIndex);

                var intervals = previewScheduler.PreviewIntervals(fsrsCard, now2);
                dto.IntervalPreview = new IntervalPreviewDto
                {
                    AgainSeconds = (int)intervals[FsrsRating.Again].TotalSeconds,
                    HardSeconds = (int)intervals[FsrsRating.Hard].TotalSeconds,
                    GoodSeconds = (int)intervals[FsrsRating.Good].TotalSeconds,
                    EasySeconds = (int)intervals[FsrsRating.Easy].TotalSeconds,
                };
            }
        }

        var remainingReviews = totalDueCount - ordered.Count(k => !k.IsNew);

        return Results.Ok(new StudyBatchResponse
        {
            SessionId = sessionId,
            Cards = cards,
            NewCardsRemaining = Math.Max(0, settings.NewCardsPerDay - newCardsToday),
            ReviewsRemaining = Math.Max(0, remainingReviews),
            NewCardsToday = newCardsToday,
            ReviewsToday = reviewsToday,
        });
    }

    [HttpGet("enrolled")]
    [SwaggerOperation(Summary = "Check if user has enrolled in SRS preview")]
    public async Task<IResult> GetEnrolled()
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var fsrsSettings = await userContext.UserFsrsSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId);

        var enrolled = fsrsSettings != null
            && !string.IsNullOrEmpty(fsrsSettings.SettingsJson)
            && fsrsSettings.SettingsJson != "{}";

        return Results.Ok(new { enrolled });
    }

    [HttpPost("enroll")]
    [SwaggerOperation(Summary = "Enroll in SRS preview by creating default settings")]
    public async Task<IResult> Enroll()
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var fsrsSettings = await userContext.UserFsrsSettings
            .FirstOrDefaultAsync(s => s.UserId == userId);

        if (fsrsSettings != null
            && !string.IsNullOrEmpty(fsrsSettings.SettingsJson)
            && fsrsSettings.SettingsJson != "{}")
        {
            return Results.Ok(new { enrolled = true });
        }

        if (fsrsSettings == null)
        {
            fsrsSettings = new UserFsrsSettings { UserId = userId };
            userContext.UserFsrsSettings.Add(fsrsSettings);
        }

        fsrsSettings.SettingsJson = JsonSerializer.Serialize(new StudySettingsDto());
        await userContext.SaveChangesAsync();

        return Results.Ok(new { enrolled = true });
    }

    [HttpGet("study-settings")]
    [SwaggerOperation(Summary = "Get study experience settings")]
    public async Task<IResult> GetStudySettings()
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var settings = await LoadStudySettings(userId);
        return Results.Ok(settings);
    }

    [HttpPut("study-settings")]
    [SwaggerOperation(Summary = "Update study experience settings")]
    public async Task<IResult> UpdateStudySettings(StudySettingsDto request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        request.NewCardsPerDay = Math.Clamp(request.NewCardsPerDay, 0, 9999);
        request.MaxReviewsPerDay = Math.Clamp(request.MaxReviewsPerDay, 0, 9999);
        request.BatchSize = Math.Clamp(request.BatchSize, 1, 999);
        request.GradingButtons = request.GradingButtons is 2 or 4 ? request.GradingButtons : 4;

        var defaultKeybinds = new StudyKeybindsDto();
        var kb = request.Keybinds;
        kb.Grade1 = SanitizeKeybind(kb.Grade1, defaultKeybinds.Grade1);
        kb.Grade2 = SanitizeKeybind(kb.Grade2, defaultKeybinds.Grade2);
        kb.Grade3 = SanitizeKeybind(kb.Grade3, defaultKeybinds.Grade3);
        kb.Grade4 = SanitizeKeybind(kb.Grade4, defaultKeybinds.Grade4);
        kb.FlipCard = SanitizeKeybind(kb.FlipCard, defaultKeybinds.FlipCard);
        kb.Blacklist = SanitizeKeybind(kb.Blacklist, defaultKeybinds.Blacklist);
        kb.Forget = SanitizeKeybind(kb.Forget, defaultKeybinds.Forget);
        kb.Master = SanitizeKeybind(kb.Master, defaultKeybinds.Master);
        kb.Suspend = SanitizeKeybind(kb.Suspend, defaultKeybinds.Suspend);
        kb.Undo = SanitizeKeybind(kb.Undo, defaultKeybinds.Undo);
        kb.WrapUp = SanitizeKeybind(kb.WrapUp, defaultKeybinds.WrapUp);

        if (!string.IsNullOrEmpty(request.Timezone))
        {
            try { TimeZoneInfo.FindSystemTimeZoneById(request.Timezone); }
            catch (TimeZoneNotFoundException) { request.Timezone = null; }
        }

        var fsrsSettings = await userContext.UserFsrsSettings
            .FirstOrDefaultAsync(s => s.UserId == userId);

        if (fsrsSettings == null)
        {
            fsrsSettings = new UserFsrsSettings { UserId = userId };
            userContext.UserFsrsSettings.Add(fsrsSettings);
        }

        fsrsSettings.SettingsJson = JsonSerializer.Serialize(request);
        await userContext.SaveChangesAsync();
        await sessionService.BumpStudyOverviewVersion(userId);

        return Results.Ok(request);
    }

    private async Task<StudySettingsDto> LoadStudySettings(string userId)
    {
        var fsrsSettings = await userContext.UserFsrsSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId);

        if (fsrsSettings == null || string.IsNullOrEmpty(fsrsSettings.SettingsJson) || fsrsSettings.SettingsJson == "{}")
            return new StudySettingsDto();

        try
        {
            return JsonSerializer.Deserialize<StudySettingsDto>(fsrsSettings.SettingsJson) ?? new StudySettingsDto();
        }
        catch (JsonException)
        {
            return new StudySettingsDto();
        }
    }

    private static string SanitizeKeybind(string value, string fallback)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 20) return fallback;
        return value;
    }

    private static List<(int WordId, byte ReadingIndex, long CardId, bool IsNew, int State)> InterleaveMixed(
        List<(int WordId, byte ReadingIndex, long CardId, bool IsNew, int State)> items)
    {
        var reviews = items.Where(i => !i.IsNew).ToList();
        var newCards = items.Where(i => i.IsNew).ToList();

        if (newCards.Count == 0) return reviews;
        if (reviews.Count == 0) return newCards;

        var result = new List<(int, byte, long, bool, int)>();
        var ratio = Math.Max(1, reviews.Count / Math.Max(1, newCards.Count));
        var ri = 0;
        var ni = 0;

        while (ri < reviews.Count || ni < newCards.Count)
        {
            for (var i = 0; i < ratio && ri < reviews.Count; i++)
                result.Add(reviews[ri++]);

            if (ni < newCards.Count)
                result.Add(newCards[ni++]);
        }

        return result;
    }

    [HttpGet("due-summary")]
    [SwaggerOperation(Summary = "Get due card counts for decks overview")]
    public async Task<IResult> GetDueSummary()
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var now = DateTime.UtcNow;
        var settings = await LoadStudySettings(userId);
        var (todayStart, _) = ResolveTimezone(now, settings.Timezone);

        var dueCutoff = GetDueCutoff(now, settings);

        var dueBaseQuery = userContext.FsrsCards
            .AsNoTracking()
            .Where(c => c.UserId == userId
                        && c.State != FsrsState.New
                        && c.State != FsrsState.Blacklisted
                        && c.State != FsrsState.Mastered
                        && c.State != FsrsState.Suspended);

        HashSet<long>? deckFilter = null;
        int reviewsDue;

        if (settings.ReviewFrom == StudyReviewFrom.StudyDecksOnly)
        {
            var dueCardKeys = await dueBaseQuery
                .Where(c => c.Due <= dueCutoff)
                .Select(c => new { c.WordId, c.ReadingIndex })
                .ToListAsync();
            deckFilter = await BuildDeckReviewFilter(userId,
                dueCardKeys.Select(c => (c.WordId, c.ReadingIndex)).ToList());
            reviewsDue = dueCardKeys
                .Count(c => deckFilter.Contains(WordFormHelper.EncodeWordKey(c.WordId, c.ReadingIndex)));
        }
        else
        {
            reviewsDue = await dueBaseQuery.Where(c => c.Due <= dueCutoff).CountAsync();
        }

        var userCardIds = userContext.FsrsCards
            .Where(c => c.UserId == userId)
            .Select(c => c.CardId);

        var todayLogs = userContext.FsrsReviewLogs
            .AsNoTracking()
            .Where(rl => userCardIds.Contains(rl.CardId) && rl.ReviewDateTime >= todayStart);

        // Mirror the study-batch path exactly so "done today" matches the new-card budget logic.
        var todayStats = await todayLogs
            .GroupBy(_ => 1)
            .Select(g => new
            {
                ReviewsToday = g.Count(),
                UniqueCardsToday = g.Select(l => l.CardId).Distinct().Count(),
                NewCardsToday = g.Where(l => l.Card.CreatedAt >= todayStart).Select(l => l.CardId).Distinct().Count(),
            })
            .FirstOrDefaultAsync();

        var reviewsToday = settings.CountFailedReviews
            ? todayStats?.ReviewsToday ?? 0
            : todayStats?.UniqueCardsToday ?? 0;
        var newCardsToday = todayStats?.NewCardsToday ?? 0;

        var newCardBudget = Math.Max(0, settings.NewCardsPerDay - newCardsToday);
        var newCardsAvailable = 0;
        if (newCardBudget > 0)
        {
            var existingKeys = new HashSet<long>();
            var cards = await userContext.FsrsCards.AsNoTracking()
                .Where(c => c.UserId == userId)
                .Select(c => new { c.WordId, c.ReadingIndex, c.State })
                .ToListAsync();
            foreach (var c in cards)
                existingKeys.Add(WordFormHelper.EncodeWordKey(c.WordId, c.ReadingIndex));

            WordFormHelper.ExpandKanaRedundancyKeys(wordFormCache,
                cards.Where(c => c.State != FsrsState.New).Select(c => (c.WordId, c.ReadingIndex)),
                existingKeys);

            var wordSetIds = await userContext.UserWordSetStates
                .AsNoTracking()
                .Where(uwss => uwss.UserId == userId)
                .Select(uwss => uwss.SetId)
                .ToListAsync();

            if (wordSetIds.Count > 0)
            {
                var setMembers = await context.WordSetMembers
                    .AsNoTracking()
                    .Where(wsm => wordSetIds.Contains(wsm.SetId))
                    .Select(wsm => new { wsm.WordId, wsm.ReadingIndex })
                    .ToListAsync();
                foreach (var m in setMembers)
                    existingKeys.Add(WordFormHelper.EncodeWordKey(m.WordId, (byte)m.ReadingIndex));
            }

            var studyDecks = await userContext.UserStudyDecks
                .AsNoTracking()
                .Where(sd => sd.UserId == userId && sd.IsActive)
                .ToListAsync();

            var candidateKeys = new HashSet<long>();

            var mediaDecks = studyDecks
                .Where(sd => sd.DeckType == StudyDeckType.MediaDeck && sd.DeckId.HasValue)
                .ToList();
            if (mediaDecks.Count > 0)
                candidateKeys.UnionWith(await GetFilteredMediaWordKeys(mediaDecks));

            var staticDeckIds = studyDecks
                .Where(sd => sd.DeckType == StudyDeckType.StaticWordList)
                .Select(sd => sd.UserStudyDeckId).ToList();
            if (staticDeckIds.Count > 0)
                candidateKeys.UnionWith(await deckWordResolver.GetStaticDeckWordKeys(staticDeckIds));

            foreach (var sd in studyDecks.Where(sd => sd.DeckType == StudyDeckType.GlobalDynamic))
                candidateKeys.UnionWith(await deckWordResolver.GetGlobalDynamicWordKeys(sd.MinGlobalFrequency, sd.MaxGlobalFrequency, sd.PosFilter));

            candidateKeys.ExceptWith(existingKeys);
            newCardsAvailable = Math.Min(candidateKeys.Count, newCardBudget);
        }

        var reviewBudgetLeft = Math.Max(0, settings.MaxReviewsPerDay - reviewsToday);

        DateTime? nextReviewAt = null;
        if (reviewsDue == 0)
        {
            if (deckFilter != null)
            {
                var ordered = dueBaseQuery
                    .Where(c => c.Due > dueCutoff)
                    .OrderBy(c => c.Due)
                    .Select(c => new { c.WordId, c.ReadingIndex, c.Due });
                var skip = 0;
                const int pageSize = 200;
                while (true)
                {
                    var page = await ordered.Skip(skip).Take(pageSize).ToListAsync();
                    if (page.Count == 0) break;
                    var match = page.FirstOrDefault(c =>
                        deckFilter.Contains(WordFormHelper.EncodeWordKey(c.WordId, c.ReadingIndex)));
                    if (match != null) { nextReviewAt = match.Due; break; }
                    skip += pageSize;
                }
            }
            else
            {
                nextReviewAt = await dueBaseQuery
                    .Where(c => c.Due > dueCutoff)
                    .OrderBy(c => c.Due)
                    .Select(c => (DateTime?)c.Due)
                    .FirstOrDefaultAsync();
            }
        }

        nextReviewAt = SnapToLocalDayStart(nextReviewAt, settings);

        return Results.Ok(new
        {
            reviewsDue,
            newCardsAvailable,
            reviewsToday,
            newCardsToday,
            reviewBudgetLeft,
            nextReviewAt,
        });
    }

    [HttpGet("study-more-count")]
    [SwaggerOperation(Summary = "Preview card count for study-more options")]
    public async Task<IResult> GetStudyMoreCount(
        [FromQuery] string mode,
        [FromQuery] int? extraNewCards = null,
        [FromQuery] int? extraReviews = null,
        [FromQuery] int? aheadMinutes = null,
        [FromQuery] int? mistakeDays = null)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var now = DateTime.UtcNow;
        var settings = await LoadStudySettings(userId);
        var (todayStart, _) = ResolveTimezone(now, settings.Timezone);
        var dueCutoff = GetDueCutoff(now, settings);
        int count = 0;

        switch (mode)
        {
            case "extraNew":
            {
                var newCardIds = userContext.FsrsCards
                    .Where(c => c.UserId == userId && c.CreatedAt >= todayStart)
                    .Select(c => c.CardId);
                var newCardsToday = await userContext.FsrsReviewLogs
                    .AsNoTracking()
                    .Where(rl => newCardIds.Contains(rl.CardId) && rl.ReviewDateTime >= todayStart)
                    .Select(rl => rl.CardId)
                    .Distinct()
                    .CountAsync();

                var budget = extraNewCards ?? Math.Max(0, settings.NewCardsPerDay - newCardsToday);

                var studyDecks = await userContext.UserStudyDecks
                    .AsNoTracking()
                    .Where(sd => sd.UserId == userId && sd.IsActive)
                    .ToListAsync();

                var existingKeys = new HashSet<long>();
                var userCards = await userContext.FsrsCards.AsNoTracking()
                    .Where(c => c.UserId == userId)
                    .Select(c => new { c.WordId, c.ReadingIndex, c.State })
                    .ToListAsync();
                foreach (var c in userCards)
                    existingKeys.Add(WordFormHelper.EncodeWordKey(c.WordId, c.ReadingIndex));

                WordFormHelper.ExpandKanaRedundancyKeys(wordFormCache,
                    userCards.Where(c => c.State != FsrsState.New).Select(c => (c.WordId, c.ReadingIndex)),
                    existingKeys);

                var smWordSetIds = await userContext.UserWordSetStates
                    .AsNoTracking()
                    .Where(uwss => uwss.UserId == userId)
                    .Select(uwss => uwss.SetId)
                    .ToListAsync();
                if (smWordSetIds.Count > 0)
                {
                    var setMembers = await context.WordSetMembers
                        .AsNoTracking()
                        .Where(wsm => smWordSetIds.Contains(wsm.SetId))
                        .Select(wsm => new { wsm.WordId, wsm.ReadingIndex })
                        .ToListAsync();
                    foreach (var m in setMembers)
                        existingKeys.Add(WordFormHelper.EncodeWordKey(m.WordId, (byte)m.ReadingIndex));
                }

                var mediaDecks = studyDecks
                    .Where(sd => sd.DeckType == StudyDeckType.MediaDeck && sd.DeckId.HasValue)
                    .ToList();

                var allCandidateKeys = new HashSet<long>();

                if (mediaDecks.Count > 0)
                    allCandidateKeys.UnionWith(await GetFilteredMediaWordKeys(mediaDecks));

                var staticDeckIds = studyDecks
                    .Where(sd => sd.DeckType == StudyDeckType.StaticWordList)
                    .Select(sd => sd.UserStudyDeckId).ToList();
                if (staticDeckIds.Count > 0)
                    allCandidateKeys.UnionWith(await deckWordResolver.GetStaticDeckWordKeys(staticDeckIds));

                foreach (var sd in studyDecks.Where(sd => sd.DeckType == StudyDeckType.GlobalDynamic))
                    allCandidateKeys.UnionWith(await deckWordResolver.GetGlobalDynamicWordKeys(sd.MinGlobalFrequency, sd.MaxGlobalFrequency, sd.PosFilter));

                allCandidateKeys.ExceptWith(existingKeys);
                count = Math.Min(allCandidateKeys.Count, budget);
                break;
            }

            case "extraReview":
            {
                var extraReviewQuery = userContext.FsrsCards
                    .AsNoTracking()
                    .Where(c => c.UserId == userId
                                && c.State != FsrsState.Blacklisted
                                && c.State != FsrsState.Mastered
                                && c.State != FsrsState.Suspended
                                && c.Due <= dueCutoff);

                int totalDue;
                if (settings.ReviewFrom == StudyReviewFrom.StudyDecksOnly)
                {
                    var keys = await extraReviewQuery.Select(c => new { c.WordId, c.ReadingIndex }).ToListAsync();
                    var filter = await BuildDeckReviewFilter(userId, keys.Select(c => (c.WordId, c.ReadingIndex)).ToList());
                    totalDue = keys.Count(c => filter.Contains(WordFormHelper.EncodeWordKey(c.WordId, c.ReadingIndex)));
                }
                else
                {
                    totalDue = await extraReviewQuery.CountAsync();
                }

                count = Math.Min(totalDue, extraReviews ?? 0);
                break;
            }

            case "ahead":
            {
                var minutes = Math.Clamp(aheadMinutes ?? 1440, 60, 10080);
                var aheadCutoff = GetDueCutoff(now, settings).AddMinutes(minutes);
                var aheadQuery = userContext.FsrsCards
                    .AsNoTracking()
                    .Where(c => c.UserId == userId
                                && c.State != FsrsState.New
                                && c.State != FsrsState.Blacklisted
                                && c.State != FsrsState.Mastered
                                && c.State != FsrsState.Suspended
                                && c.Due <= aheadCutoff);

                if (settings.ReviewFrom == StudyReviewFrom.StudyDecksOnly)
                {
                    var keys = await aheadQuery.Select(c => new { c.WordId, c.ReadingIndex }).ToListAsync();
                    var filter = await BuildDeckReviewFilter(userId, keys.Select(c => (c.WordId, c.ReadingIndex)).ToList());
                    count = keys.Count(c => filter.Contains(WordFormHelper.EncodeWordKey(c.WordId, c.ReadingIndex)));
                }
                else
                {
                    count = await aheadQuery.CountAsync();
                }
                break;
            }

            case "mistakes":
            {
                var days = Math.Clamp(mistakeDays ?? 3, 1, 7);
                var mistakeCutoff = now.AddDays(-days);

                var userCardIds = userContext.FsrsCards
                    .Where(c => c.UserId == userId)
                    .Select(c => c.CardId);
                var mistakeCardIds = await userContext.FsrsReviewLogs
                    .AsNoTracking()
                    .Where(l => userCardIds.Contains(l.CardId)
                                && l.Rating == FsrsRating.Again
                                && l.ReviewDateTime >= mistakeCutoff)
                    .Select(l => l.CardId)
                    .Distinct()
                    .ToListAsync();

                if (mistakeCardIds.Count > 0)
                {
                    count = await userContext.FsrsCards
                        .CountAsync(c => mistakeCardIds.Contains(c.CardId)
                                         && c.State == FsrsState.Review
                                         && c.Due > now);
                }
                break;
            }
        }

        return Results.Ok(new { count });
    }

    [HttpGet("review-forecast")]
    [SwaggerOperation(Summary = "Get upcoming review forecast")]
    public async Task<IResult> GetReviewForecast()
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var now = DateTime.UtcNow;
        var oneHour = now.AddHours(1);
        var settings = await LoadStudySettings(userId);
        var (todayStart, _) = ResolveTimezone(now, settings.Timezone);
        var tomorrowUtc = todayStart.AddHours(24);
        var dayAfterUtc = todayStart.AddHours(48);
        var dayBoundary = settings.DayBoundaryScheduling;

        DateTime forecastBase, bucket0End, bucket1End, bucket2End;
        if (dayBoundary)
        {
            forecastBase = LocalDayStartUtc(now, settings.Timezone, 1);
            bucket0End = LocalDayStartUtc(now, settings.Timezone, 2);
            bucket1End = LocalDayStartUtc(now, settings.Timezone, 3);
            bucket2End = LocalDayStartUtc(now, settings.Timezone, 4);
        }
        else
        {
            forecastBase = now;
            bucket0End = oneHour;
            bucket1End = tomorrowUtc;
            bucket2End = dayAfterUtc;
        }

        var baseQuery = userContext.FsrsCards
            .AsNoTracking()
            .Where(c => c.UserId == userId
                        && c.State != FsrsState.New
                        && c.State != FsrsState.Blacklisted
                        && c.State != FsrsState.Mastered
                        && c.State != FsrsState.Suspended
                        && c.Due > forecastBase);

        int dueWithinHour, dueToday, dueTomorrow;
        DateTime? nextReviewAt = null;

        if (settings.ReviewFrom == StudyReviewFrom.StudyDecksOnly)
        {
            var upcomingCards = await baseQuery
                .Where(c => c.Due <= bucket2End)
                .Select(c => new { c.WordId, c.ReadingIndex, c.Due })
                .ToListAsync();
            var filter = await BuildDeckReviewFilter(userId, upcomingCards.Select(c => (c.WordId, c.ReadingIndex)).ToList());
            var filtered = upcomingCards.Where(c => filter.Contains(WordFormHelper.EncodeWordKey(c.WordId, c.ReadingIndex))).ToList();

            dueWithinHour = filtered.Count(c => c.Due <= bucket0End);
            dueToday = filtered.Count(c => c.Due > bucket0End && c.Due <= bucket1End);
            dueTomorrow = filtered.Count(c => c.Due > bucket1End && c.Due <= bucket2End);

            if (dueWithinHour == 0 && dueToday == 0 && (!dayBoundary || dueTomorrow == 0))
            {
                var ordered = baseQuery.OrderBy(c => c.Due)
                    .Select(c => new { c.WordId, c.ReadingIndex, c.Due });
                var skip = 0;
                const int pageSize = 200;
                while (true)
                {
                    var page = await ordered.Skip(skip).Take(pageSize).ToListAsync();
                    if (page.Count == 0) break;
                    var match = page.FirstOrDefault(c =>
                        filter.Contains(WordFormHelper.EncodeWordKey(c.WordId, c.ReadingIndex)));
                    if (match != null) { nextReviewAt = match.Due; break; }
                    skip += pageSize;
                }
            }
        }
        else
        {
            var forecast = await baseQuery
                .Where(c => c.Due <= bucket2End)
                .GroupBy(c => c.Due <= bucket0End ? 0 : c.Due <= bucket1End ? 1 : 2)
                .Select(g => new { Bucket = g.Key, Count = g.Count() })
                .ToListAsync();

            dueWithinHour = forecast.FirstOrDefault(f => f.Bucket == 0)?.Count ?? 0;
            dueToday = forecast.FirstOrDefault(f => f.Bucket == 1)?.Count ?? 0;
            dueTomorrow = forecast.FirstOrDefault(f => f.Bucket == 2)?.Count ?? 0;

            if (dueWithinHour == 0 && dueToday == 0 && (!dayBoundary || dueTomorrow == 0))
            {
                nextReviewAt = await baseQuery
                    .OrderBy(c => c.Due)
                    .Select(c => (DateTime?)c.Due)
                    .FirstOrDefaultAsync();
            }
        }

        nextReviewAt = SnapToLocalDayStart(nextReviewAt, settings);

        return Results.Ok(new
        {
            dueWithinHour,
            dueToday,
            dueTomorrow,
            nextReviewAt,
            dayBoundaryScheduling = dayBoundary,
        });
    }

    [HttpGet("review-forecast-30d")]
    [SwaggerOperation(Summary = "Get 30-day review forecast bucketed by user-local day")]
    public async Task<IResult> GetReviewForecast30d()
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var now = DateTime.UtcNow;
        var settings = await LoadStudySettings(userId);
        var (_, offsetHours) = ResolveTimezone(now, settings.Timezone);

        var localToday = now.AddHours(offsetHours).Date;
        var windowStartUtc = localToday.AddHours(-offsetHours);
        var windowEndUtc = localToday.AddDays(30).AddHours(-offsetHours);

        var baseQuery = userContext.FsrsCards
            .AsNoTracking()
            .Where(c => c.UserId == userId
                        && c.State != FsrsState.New
                        && c.State != FsrsState.Blacklisted
                        && c.State != FsrsState.Mastered
                        && c.State != FsrsState.Suspended
                        && c.Due <= windowEndUtc);

        var counts = new int[30];

        if (settings.ReviewFrom == StudyReviewFrom.StudyDecksOnly)
        {
            var cards = await baseQuery
                .Select(c => new { c.WordId, c.ReadingIndex, c.Due })
                .ToListAsync();
            var filter = await BuildDeckReviewFilter(userId, cards.Select(c => (c.WordId, c.ReadingIndex)).ToList());
            foreach (var c in cards)
            {
                if (!filter.Contains(WordFormHelper.EncodeWordKey(c.WordId, c.ReadingIndex))) continue;
                var dayIndex = c.Due <= windowStartUtc ? 0 : (int)(c.Due.AddHours(offsetHours).Date - localToday).TotalDays;
                if (dayIndex >= 0 && dayIndex < 30) counts[dayIndex]++;
            }
        }
        else
        {
            var dues = await baseQuery.Select(c => c.Due).ToListAsync();
            foreach (var due in dues)
            {
                var dayIndex = due <= windowStartUtc ? 0 : (int)(due.AddHours(offsetHours).Date - localToday).TotalDays;
                if (dayIndex >= 0 && dayIndex < 30) counts[dayIndex]++;
            }
        }

        var days = new object[30];
        for (var i = 0; i < 30; i++)
            days[i] = new { date = localToday.AddDays(i).ToString("yyyy-MM-dd"), count = counts[i] };

        return Results.Ok(new { days });
    }

    [HttpGet("deck-streak")]
    [SwaggerOperation(Summary = "Get streak info and recent activity for the decks page")]
    public async Task<IResult> GetDeckStreak()
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var settings = await LoadStudySettings(userId);
        var now = DateTime.UtcNow;
        var (todayStart, offsetHours) = ResolveTimezone(now, settings.Timezone);
        var today = now.AddHours(offsetHours).Date;
        var windowStart = today.AddDays(-83);
        var windowStartUtc = windowStart.AddHours(-offsetHours);

        var userCardIds = userContext.FsrsCards
            .Where(c => c.UserId == userId)
            .Select(c => c.CardId);

        var allTimestamps = await userContext.FsrsReviewLogs
            .AsNoTracking()
            .Where(rl => userCardIds.Contains(rl.CardId))
            .Select(rl => rl.ReviewDateTime)
            .ToListAsync();

        var totalReviewDays = allTimestamps
            .Select(dt => dt.AddHours(offsetHours).Date)
            .Distinct()
            .Count();

        var allDates = allTimestamps
            .Select(dt => dt.AddHours(offsetHours).Date)
            .Distinct()
            .OrderByDescending(d => d)
            .ToList();

        var (currentStreak, longestStreak) = ComputeStreaks(allDates, today);

        var dailyStats = allTimestamps
            .Where(dt => dt >= windowStartUtc)
            .GroupBy(dt => dt.AddHours(offsetHours).Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .OrderBy(g => g.Date)
            .ToList();

        return Results.Ok(new
        {
            currentStreak,
            longestStreak,
            isNewRecord = currentStreak > 0 && currentStreak >= longestStreak,
            totalReviewDays,
            recentDays = dailyStats.Select(d => new { date = DateOnly.FromDateTime(d.Date).ToString("yyyy-MM-dd"), count = d.Count }),
        });
    }

    [HttpGet("session-streak")]
    [SwaggerOperation(Summary = "Get current streak info for session summary")]
    public async Task<IResult> GetSessionStreak()
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var settings = await LoadStudySettings(userId);
        var now = DateTime.UtcNow;
        var (_, offsetHours) = ResolveTimezone(now, settings.Timezone);
        var today = now.AddHours(offsetHours).Date;

        var userCardIds = userContext.FsrsCards
            .Where(c => c.UserId == userId)
            .Select(c => c.CardId);

        var rawTimestamps = await userContext.FsrsReviewLogs
            .AsNoTracking()
            .Where(rl => userCardIds.Contains(rl.CardId))
            .Select(rl => rl.ReviewDateTime)
            .ToListAsync();

        var recentDates = rawTimestamps
            .Select(dt => dt.AddHours(offsetHours).Date)
            .Distinct()
            .OrderByDescending(d => d)
            .ToList();

        var (currentStreak, longestStreak) = ComputeStreaks(recentDates, today);

        return Results.Ok(new
        {
            currentStreak,
            longestStreak,
            isNewRecord = currentStreak > 0 && currentStreak >= longestStreak,
        });
    }

    [HttpGet("retention")]
    [SwaggerOperation(Summary = "Get measured true retention (overall / young / mature) over 30d, 90d, all-time and a monthly series")]
    public async Task<IResult> GetRetention()
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var settings = await LoadStudySettings(userId);
        var now = DateTime.UtcNow;
        var (_, offsetHours) = ResolveTimezone(now, settings.Timezone);

        var userSettings = await userContext.UserFsrsSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId);
        var desiredRetention = userSettings?.DesiredRetention is double dr and > 0 and < 1 ? dr : FsrsConstants.DefaultDesiredRetention;

        var userCardIds = userContext.FsrsCards
            .Where(c => c.UserId == userId)
            .Select(c => c.CardId);

        var rawLogs = await userContext.FsrsReviewLogs
            .AsNoTracking()
            .Where(l => userCardIds.Contains(l.CardId))
            .Select(l => new { l.CardId, l.ReviewDateTime, IsAgain = l.Rating == FsrsRating.Again })
            .ToListAsync();

        var result = RetentionCalculator.Compute(
            rawLogs.Select(l => new RetentionCalculator.ReviewEntry(l.CardId, l.ReviewDateTime, l.IsAgain)),
            offsetHours,
            now);

        return Results.Ok(new
        {
            desiredRetention,
            matureThresholdDays = RetentionCalculator.MatureThresholdDays,
            windows = new
            {
                last30 = MapWindow(result.Last30),
                last90 = MapWindow(result.Last90),
                all = MapWindow(result.AllTime),
            },
            weekly = result.Weekly.Select(MapPeriod),
            monthly = result.Monthly.Select(MapPeriod),
        });
    }

    private static object MapPeriod(RetentionCalculator.PeriodRetention p) => new
    {
        period = p.Period,
        overall = MapBucket(p.Overall),
        young = MapBucket(p.Young),
        mature = MapBucket(p.Mature),
    };

    private static object MapWindow(RetentionCalculator.RetentionWindow w) => new
    {
        overall = MapBucket(w.Overall),
        young = MapBucket(w.Young),
        mature = MapBucket(w.Mature),
    };

    private static object MapBucket(RetentionCalculator.RetentionBucket b) => new
    {
        total = b.Total,
        passed = b.Passed,
        retention = b.Retention,
    };

    private static (int currentStreak, int longestStreak) ComputeStreaks(List<DateTime> sortedDatesDesc, DateTime today)
    {
        if (sortedDatesDesc.Count == 0)
            return (0, 0);

        var currentStreak = 0;
        var checkDate = today;

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

    private static (DateTime dayStart, double offsetHours) ResolveTimezone(DateTime utcNow, string? timezone)
    {
        if (string.IsNullOrEmpty(timezone))
            return (utcNow.Date, 0);

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            var offsetHours = tz.GetUtcOffset(utcNow).TotalHours;
            var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
            var localMidnight = localNow.Date;
            var dayStart = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localMidnight, DateTimeKind.Unspecified), tz);
            return (dayStart, offsetHours);
        }
        catch (TimeZoneNotFoundException)
        {
            return (utcNow.Date, 0);
        }
    }

    private static DateTime LocalDayStartUtc(DateTime utcNow, string? timezone, int daysOffset)
    {
        if (string.IsNullOrEmpty(timezone))
            return utcNow.Date.AddDays(daysOffset);
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            var localDay = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz).Date.AddDays(daysOffset);
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDay, DateTimeKind.Unspecified), tz);
        }
        catch (TimeZoneNotFoundException)
        {
            return utcNow.Date.AddDays(daysOffset);
        }
    }

    private static DateTime GetDueCutoff(DateTime utcNow, StudySettingsDto settings)
        => settings.DayBoundaryScheduling ? LocalDayStartUtc(utcNow, settings.Timezone, 1) : utcNow;

    private static DateTime? SnapToLocalDayStart(DateTime? utc, StudySettingsDto settings)
    {
        if (!utc.HasValue || !settings.DayBoundaryScheduling) return utc;
        var (dayStart, _) = ResolveTimezone(utc.Value, settings.Timezone);
        return dayStart;
    }

    [HttpPost("card-examples")]
    [SwaggerOperation(Summary = "Get example sentences for specific word pairs")]
    public async Task<IResult> GetCardExamples([FromBody] CardExamplesRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();
        if (request.Pairs is not { Count: > 0 and <= 20 }) return Results.BadRequest();

        var pairWordIds = request.Pairs.Select(p => p.WordId).Distinct().ToList();

        var customSentences = await userContext.UserExampleSentences
            .AsNoTracking()
            .Where(e => e.UserId == userId && pairWordIds.Contains(e.WordId))
            .ToListAsync();

        var customByKey = customSentences
            .GroupBy(e => $"{e.WordId}-{e.ReadingIndex}")
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new Dictionary<string, StudyExampleSentenceDto>();
        var remainingPairs = new List<CardExamplesRequest.WordPair>();

        foreach (var pair in request.Pairs)
        {
            var key = $"{pair.WordId}-{pair.ReadingIndex}";
            if (customByKey.TryGetValue(key, out var customs) && customs.Count > 0)
            {
                var picked = customs[Random.Shared.Next(customs.Count)];
                result[key] = BuildCustomStudyExample(picked);
            }
            else
            {
                remainingPairs.Add(pair);
            }
        }

        if (remainingPairs.Count == 0)
            return Results.Ok(new CardExamplesResponse { Examples = result });

        var studyDecks = await userContext.UserStudyDecks
            .AsNoTracking()
            .Where(sd => sd.UserId == userId)
            .ToListAsync();
        var studyDeckIdArray = studyDecks.Where(sd => sd.DeckId.HasValue).Select(sd => sd.DeckId!.Value).ToArray();

        var remainingWordIds = remainingPairs.Select(p => p.WordId).ToArray();
        var remainingReadingIndexes = remainingPairs.Select(p => (short)p.ReadingIndex).ToArray();
        var wordIds = remainingWordIds.Distinct().ToList();

        var isNpgsql = context.Database.ProviderName?.Contains("Npgsql") == true;

        List<int> studyExampleIds;
        List<int> fallbackExampleIds;
        if (isNpgsql)
        {
            studyExampleIds = studyDeckIdArray.Length > 0
                ? await context.Database
                    .SqlQueryRaw<int>(@"
                        SELECT esw.""ExampleSentenceId""
                        FROM unnest({0}::int[], {1}::smallint[]) AS v(wid, ri)
                        CROSS JOIN LATERAL (
                            SELECT esw2.""ExampleSentenceId""
                            FROM jiten.""ExampleSentenceWords"" esw2
                            JOIN jiten.""ExampleSentences"" es ON es.""SentenceId"" = esw2.""ExampleSentenceId""
                            WHERE esw2.""WordId"" = v.wid AND esw2.""ReadingIndex"" = v.ri
                              AND es.""DeckId"" = ANY({2})
                            LIMIT 1
                        ) esw
                    ", remainingWordIds, remainingReadingIndexes, studyDeckIdArray)
                    .ToListAsync()
                : [];

            if (studyExampleIds.Count >= remainingPairs.Count)
            {
                fallbackExampleIds = [];
            }
            else
            {
                fallbackExampleIds = await context.Database
                    .SqlQueryRaw<int>(@"
                        SELECT esw.""ExampleSentenceId""
                        FROM unnest({0}::int[], {1}::smallint[]) AS v(wid, ri)
                        CROSS JOIN LATERAL (
                            SELECT esw2.""ExampleSentenceId""
                            FROM jiten.""ExampleSentenceWords"" esw2
                            WHERE esw2.""WordId"" = v.wid AND esw2.""ReadingIndex"" = v.ri
                            ORDER BY esw2.""ExampleSentenceId""
                            LIMIT 1
                        ) esw
                    ", remainingWordIds, remainingReadingIndexes)
                    .ToListAsync();

            }
        }
        else
        {
            studyExampleIds = [];
            fallbackExampleIds = await context.ExampleSentenceWords
                .AsNoTracking()
                .Where(esw => wordIds.Contains(esw.WordId))
                .GroupBy(esw => new { esw.WordId, esw.ReadingIndex })
                .Select(g => g.Min(esw => esw.ExampleSentenceId))
                .ToListAsync();
        }

        var exampleIds = studyExampleIds.Union(fallbackExampleIds).Distinct().ToList();

        if (exampleIds.Count == 0)
            return Results.Ok(new CardExamplesResponse { Examples = result });

        var sentences = await context.ExampleSentences
            .AsNoTracking()
            .Where(es => exampleIds.Contains(es.SentenceId))
            .ToDictionaryAsync(es => es.SentenceId);

        var exampleWords = await context.ExampleSentenceWords
            .AsNoTracking()
            .Where(esw => exampleIds.Contains(esw.ExampleSentenceId)
                        && wordIds.Contains(esw.WordId))
            .ToListAsync();


        var exampleDeckIds = sentences.Values.Select(s => s.DeckId).Distinct().ToList();
        var exampleDecks = exampleDeckIds.Count > 0
            ? await context.Decks.AsNoTracking()
                .Where(d => exampleDeckIds.Contains(d.DeckId))
                .Select(d => new DeckProjection(d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.MediaType, d.ParentDeckId))
                .ToDictionaryAsync(d => d.DeckId)
            : new();
        var exampleParentIds = exampleDecks.Values
            .Where(d => d.ParentDeckId.HasValue)
            .Select(d => d.ParentDeckId!.Value)
            .Distinct().ToList();
        var exampleParentDecks = exampleParentIds.Count > 0
            ? await context.Decks.AsNoTracking()
                .Where(d => exampleParentIds.Contains(d.DeckId))
                .Select(d => new DeckProjection(d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.MediaType, d.ParentDeckId))
                .ToDictionaryAsync(d => d.DeckId)
            : new();


        var studyIdSet = studyExampleIds.ToHashSet();
        foreach (var esw in exampleWords.OrderByDescending(e => studyIdSet.Contains(e.ExampleSentenceId)))
        {
            if (!sentences.TryGetValue(esw.ExampleSentenceId, out var sentence)) continue;
            var key = $"{esw.WordId}-{esw.ReadingIndex}";
            result.TryAdd(key, BuildStudyExampleSentence(esw, sentence, exampleDecks, exampleParentDecks));
        }

        return Results.Ok(new CardExamplesResponse { Examples = result });
    }

    private record DeckProjection(int DeckId, string OriginalTitle, string? RomajiTitle, string? EnglishTitle, MediaType MediaType, int? ParentDeckId);

    private static StudyExampleSentenceDto BuildStudyExampleSentence(
        ExampleSentenceWord exWord,
        ExampleSentence sentence,
        Dictionary<int, DeckProjection> decks,
        Dictionary<int, DeckProjection> parentDecks)
    {
        var dto = new StudyExampleSentenceDto
        {
            SentenceId = sentence.SentenceId,
            Text = sentence.Text,
            WordPosition = exWord.Position,
            WordLength = exWord.Length
        };

        if (decks.TryGetValue(sentence.DeckId, out var deck))
        {
            dto.SourceDeck = new StudyExampleSourceDto
            {
                DeckId = deck.DeckId,
                OriginalTitle = deck.OriginalTitle,
                RomajiTitle = deck.RomajiTitle,
                EnglishTitle = deck.EnglishTitle,
                MediaType = (int)deck.MediaType
            };

            if (deck.ParentDeckId != null && parentDecks.TryGetValue(deck.ParentDeckId.Value, out var parent))
            {
                dto.SourceParent = new StudyExampleSourceDto
                {
                    DeckId = parent.DeckId,
                    OriginalTitle = parent.OriginalTitle,
                    RomajiTitle = parent.RomajiTitle,
                    EnglishTitle = parent.EnglishTitle,
                    MediaType = (int)parent.MediaType
                };
            }
        }

        return dto;
    }

    private static StudyExampleSentenceDto BuildCustomStudyExample(UserExampleSentence custom)
    {
        var plainText = custom.Text.Replace("**", "");

        return new StudyExampleSentenceDto
        {
            SentenceId = -custom.UserExampleSentenceId,
            Text = plainText,
            WordPosition = -1,
            WordLength = 0,
            IsCustom = true,
            CustomSource = custom.Source,
            CustomText = custom.Text
        };
    }

    private async Task<T> CountWithFactoryContext<T>(Func<JitenDbContext, UserDbContext, ICurrentUserService, Task<T>> query)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync();
        await using var uCtx = await userContextFactory.CreateDbContextAsync();
        var userService = new CurrentUserService(httpContextAccessor, ctx, uCtx, wordFormCache);
        return await query(ctx, uCtx, userService);
    }

    private static bool IsValidPosFilter(string? posFilter)
    {
        if (string.IsNullOrEmpty(posFilter)) return true;
        try
        {
            var arr = JsonSerializer.Deserialize<string[]>(posFilter);
            return arr != null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<Dictionary<(int, byte), int>> BuildCardFrequencyRanks(
        Dictionary<(int, byte), (FsrsState State, DateTime Due, DateTime? LastReview)> cardStateMap,
        IEnumerable<int>? additionalWordIds = null)
    {
        var wordIds = cardStateMap.Keys.Select(k => k.Item1).Distinct();
        if (additionalWordIds != null)
            wordIds = wordIds.Union(additionalWordIds);
        var wordIdList = wordIds.ToList();
        var freqs = await context.WordFormFrequencies.AsNoTracking()
            .Where(wff => wordIdList.Contains(wff.WordId))
            .Select(wff => new { wff.WordId, wff.ReadingIndex, wff.FrequencyRank })
            .ToListAsync();
        var result = new Dictionary<(int, byte), int>();
        foreach (var f in freqs)
            result[(f.WordId, (byte)f.ReadingIndex)] = f.FrequencyRank;
        return result;
    }

    private async Task<HashSet<int>?> GetPosMatchedWordIds(
        string? posFilter, Dictionary<(int, byte), (FsrsState, DateTime, DateTime?)> cardStateMap,
        IEnumerable<int>? additionalWordIds = null)
    {
        if (string.IsNullOrEmpty(posFilter)) return null;

        var posTags = JsonSerializer.Deserialize<string[]>(posFilter);
        if (posTags is not { Length: > 0 }) return null;

        var wordIds = cardStateMap.Keys.Select(k => k.Item1).Distinct();
        if (additionalWordIds != null)
            wordIds = wordIds.Union(additionalWordIds);
        var wordIdList = wordIds.ToList();
        return (await context.JMDictWords.AsNoTracking()
            .Where(w => wordIdList.Contains(w.WordId) && w.PartsOfSpeech.Any(p => posTags.Contains(p)))
            .Select(w => w.WordId)
            .ToListAsync())
            .ToHashSet();
    }

    private static (int Tracked, int Learning, int Review, int Mastered, int Blacklisted, int Suspended, int Due, int Young, int Mature)
        CountCardStats(
            IEnumerable<(FsrsState State, DateTime Due, DateTime? LastReview)> cards,
            DateTime dueCutoff)
    {
        int learning = 0, review = 0, mastered = 0, blacklisted = 0, suspended = 0, dueCount = 0, tracked = 0, young = 0, mature = 0;
        foreach (var (state, due, lastReview) in cards)
        {
            tracked++;
            if (state is FsrsState.New or FsrsState.Learning or FsrsState.Relearning)
            {
                learning++;
                if (state is FsrsState.Learning or FsrsState.Relearning && due <= dueCutoff)
                    dueCount++;
            }
            else if (state == FsrsState.Review)
            {
                review++;
                if (due <= dueCutoff) dueCount++;
                // Maturity mirrors UserController.ComputeEffectiveCategory: interval = due - lastReview, mature at >= 21 days.
                if (lastReview.HasValue && (due - lastReview.Value).TotalDays >= 21) mature++;
                else young++;
            }
            else if (state == FsrsState.Mastered) mastered++;
            else if (state == FsrsState.Blacklisted) blacklisted++;
            else if (state == FsrsState.Suspended) suspended++;
        }
        return (tracked, learning, review, mastered, blacklisted, suspended, dueCount, young, mature);
    }

    private static (int Tracked, int Learning, int Review, int Mastered, int Blacklisted, int Suspended, int Due, int Young, int Mature)
        ComputeGlobalDynamicCardStats(
            UserStudyDeck sd,
            Dictionary<(int, byte), (FsrsState State, DateTime Due, DateTime? LastReview)> cardStateMap,
            Dictionary<(int, byte), int> freqRanks,
            HashSet<long>? kanaFormKeys,
            HashSet<int>? posMatchedWordIds,
            DateTime dueCutoff,
            Dictionary<(int, byte), WordSetStateType>? wordSetStates = null,
            HashSet<(int, byte)>? redundantKanaPairs = null)
    {
        bool MatchesFreqRange((int wordId, byte ri) key)
        {
            if (!freqRanks.TryGetValue(key, out var freq)) return false;
            if (sd.MinGlobalFrequency.HasValue && freq < sd.MinGlobalFrequency.Value) return false;
            if (sd.MaxGlobalFrequency.HasValue && freq > sd.MaxGlobalFrequency.Value) return false;
            if (sd.ExcludeKana && kanaFormKeys != null && kanaFormKeys.Contains(WordFormHelper.EncodeWordKey(key.wordId, key.ri))) return false;
            if (posMatchedWordIds != null && !posMatchedWordIds.Contains(key.wordId)) return false;
            return true;
        }

        var filtered = cardStateMap
            .Where(entry => MatchesFreqRange(entry.Key))
            .Select(e => e.Value);

        var stats = CountCardStats(filtered, dueCutoff);

        if (wordSetStates != null)
        {
            foreach (var (key, setState) in wordSetStates)
            {
                if (cardStateMap.ContainsKey(key)) continue;
                if (!MatchesFreqRange(key)) continue;
                stats.Tracked++;
                if (setState == WordSetStateType.Mastered) stats.Mastered++;
                else stats.Blacklisted++;
            }
        }

        if (redundantKanaPairs != null)
        {
            foreach (var key in redundantKanaPairs)
            {
                if (wordSetStates != null && wordSetStates.ContainsKey(key)) continue;
                if (!MatchesFreqRange(key)) continue;
                stats.Tracked++;
                stats.Mastered++;
            }
        }

        return stats;
    }

    private static (int Tracked, int Learning, int Review, int Mastered, int Blacklisted, int Suspended, int Due, int Young, int Mature)
        ComputeCardStatsFromWordKeys(
            HashSet<long> wordKeys,
            Dictionary<(int, byte), (FsrsState State, DateTime Due, DateTime? LastReview)> cardStateMap,
            DateTime dueCutoff,
            Dictionary<long, WordSetStateType>? wordSetKeys = null,
            HashSet<long>? redundantKanaKeys = null)
    {
        var filtered = cardStateMap
            .Where(e => wordKeys.Contains(WordFormHelper.EncodeWordKey(e.Key.Item1, e.Key.Item2)))
            .Select(e => e.Value);

        var stats = CountCardStats(filtered, dueCutoff);
        if (wordSetKeys != null || redundantKanaKeys != null)
            EnrichStatsWithWordSetAndKana(ref stats, wordKeys, wordSetKeys, redundantKanaKeys);
        return stats;
    }

    private static void EnrichStatsWithWordSetAndKana(
        ref (int Tracked, int Learning, int Review, int Mastered, int Blacklisted, int Suspended, int Due, int Young, int Mature) stats,
        HashSet<long> deckKeys,
        Dictionary<long, WordSetStateType>? wordSetKeys,
        HashSet<long>? redundantKanaKeys)
    {
        if (wordSetKeys != null)
        {
            foreach (var (key, setState) in wordSetKeys)
            {
                if (!deckKeys.Contains(key)) continue;
                stats.Tracked++;
                if (setState == WordSetStateType.Mastered) stats.Mastered++;
                else stats.Blacklisted++;
            }
        }

        if (redundantKanaKeys != null)
        {
            foreach (var key in redundantKanaKeys)
            {
                if (!deckKeys.Contains(key)) continue;
                if (wordSetKeys != null && wordSetKeys.ContainsKey(key)) continue;
                stats.Tracked++;
                stats.Mastered++;
            }
        }
    }

    private static void ApplyWordPairCardStats(
        StudyDeckDto dto,
        List<(int WordId, byte ReadingIndex)> wordPairs,
        Dictionary<(int, byte), (FsrsState State, DateTime Due, DateTime? LastReview)> cardStateMap,
        DateTime dueCutoff,
        Dictionary<long, WordSetStateType>? wordSetKeys = null,
        HashSet<long>? redundantKanaKeys = null)
    {
        var matched = wordPairs
            .Where(w => cardStateMap.ContainsKey((w.WordId, w.ReadingIndex)))
            .Select(w => cardStateMap[(w.WordId, w.ReadingIndex)]);

        var stats = CountCardStats(matched, dueCutoff);
        if (wordSetKeys != null || redundantKanaKeys != null)
        {
            var encodedKeys = wordPairs.Select(w => WordFormHelper.EncodeWordKey(w.WordId, w.ReadingIndex)).ToHashSet();
            EnrichStatsWithWordSetAndKana(ref stats, encodedKeys, wordSetKeys, redundantKanaKeys);
        }
        dto.LearningCount = stats.Learning;
        dto.ReviewCount = stats.Review;
        dto.YoungCount = stats.Young;
        dto.MatureCount = stats.Mature;
        dto.MasteredCount = stats.Mastered;
        dto.BlacklistedCount = stats.Blacklisted;
        dto.SuspendedCount = stats.Suspended;
        dto.DueReviewCount = stats.Due;
        dto.UnseenCount = Math.Max(0, wordPairs.Count - stats.Tracked);
    }

    private async Task<(UserStudyDeck? Deck, IResult? Error)> GetStaticDeckForUser(int id, string action)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return (null, Results.Unauthorized());

        var studyDeck = await userContext.UserStudyDecks
            .FirstOrDefaultAsync(sd => sd.UserStudyDeckId == id && sd.UserId == userId);
        if (studyDeck == null) return (null, Results.NotFound());
        if (studyDeck.DeckType != StudyDeckType.StaticWordList)
            return (null, Results.BadRequest($"Words can only be {action} in static word list decks."));

        return (studyDeck, null);
    }

    [HttpPost("study-decks/{id:int}/download")]
    [EnableRateLimiting("download")]
    [SwaggerOperation(Summary = "Download a study deck")]
    public async Task<IResult> DownloadStudyDeck(int id, [FromBody] DeckDownloadRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var studyDeck = await userContext.UserStudyDecks
            .AsNoTracking()
            .FirstOrDefaultAsync(sd => sd.UserStudyDeckId == id && sd.UserId == userId);

        if (studyDeck == null) return Results.NotFound();

        if (request.Format == DeckFormat.Learn)
            return Results.BadRequest("Learn format is not supported for study decks.");

        List<(int WordId, byte ReadingIndex, int Occurrences)> deckWords;
        List<int>? sentenceDeckIds = null;
        string deckTitle;

        switch (studyDeck.DeckType)
        {
            case StudyDeckType.MediaDeck:
            {
                if (!studyDeck.DeckId.HasValue) return Results.BadRequest("Study deck has no linked media deck.");

                var deck = await context.Decks.AsNoTracking()
                    .Include(d => d.Children)
                    .FirstOrDefaultAsync(d => d.DeckId == studyDeck.DeckId.Value);
                if (deck == null) return Results.NotFound("Linked media deck not found.");

                if (request.Format == DeckFormat.Yomitan)
                {
                    var yomitanBytes = await YomitanHelper.GenerateYomitanFrequencyDeckFromDeck(contextFactory, deck);
                    return Results.File(yomitanBytes, "application/zip", $"freq_{deck.OriginalTitle}.zip");
                }

                var (words, error) = await deckWordResolver.ResolveDeckWords(new DeckWordResolveRequest(
                    studyDeck.DeckId.Value, deck,
                    request.DownloadType, request.Order,
                    request.MinFrequency, request.MaxFrequency,
                    request.ExcludeMatureMasteredBlacklisted, request.ExcludeAllTrackedWords,
                    request.TargetPercentage, request.MinOccurrences, request.MaxOccurrences,
                    studyDeck.PosFilter, studyDeck.StartFromKnown));
                if (error != null) return error;

                deckWords = words!.Select(dw => (dw.WordId, dw.ReadingIndex, dw.Occurrences)).ToList();
                sentenceDeckIds = deck.Children.Count != 0
                    ? deck.Children.Select(c => c.DeckId).ToList()
                    : [studyDeck.DeckId.Value];
                deckTitle = deck.OriginalTitle;
                break;
            }
            case StudyDeckType.GlobalDynamic:
            {
                if (request.Format == DeckFormat.Yomitan)
                    return Results.BadRequest("Yomitan format is not supported for global dynamic decks.");

                var result = await deckWordResolver.ResolveGlobalDynamicWords(
                    studyDeck.MinGlobalFrequency, studyDeck.MaxGlobalFrequency, studyDeck.PosFilter,
                    request.ExcludeKana, request.ExcludeMatureMasteredBlacklisted, request.ExcludeAllTrackedWords);
                deckWords = result.Words.Select(w => (w.WordId, w.ReadingIndex, Math.Max(1, w.Occurrences))).ToList();
                deckTitle = studyDeck.Name;
                break;
            }
            case StudyDeckType.StaticWordList:
            {
                if (request.Format == DeckFormat.Yomitan)
                    return Results.BadRequest("Yomitan format is not supported for static word list decks.");

                var words = await deckWordResolver.ResolveStaticDeckWords(id, (int)request.Order,
                    request.ExcludeMatureMasteredBlacklisted, request.ExcludeAllTrackedWords);
                deckWords = words.Select(w => (w.WordId, w.ReadingIndex, Math.Max(1, w.Occurrences))).ToList();
                deckTitle = studyDeck.Name;
                break;
            }
            default:
                return Results.BadRequest("Unsupported deck type.");
        }

        var wordIds = deckWords.Select(dw => (long)dw.WordId).ToList();
        var bytes = await downloadService.GenerateDownload(request, wordIds, deckTitle, deckWords, sentenceDeckIds);

        if (bytes == null) return Results.BadRequest();

        logger.LogInformation(
            "User downloaded study deck: StudyDeckId={StudyDeckId}, DeckType={DeckType}, Format={Format}, WordCount={WordCount}",
            id, studyDeck.DeckType, request.Format, deckWords.Count);

        return request.Format switch
        {
            DeckFormat.Anki => Results.File(bytes, "application/x-binary", $"{deckTitle}.apkg"),
            DeckFormat.Csv => Results.File(bytes, "text/csv", $"{deckTitle}.csv"),
            DeckFormat.Txt or DeckFormat.TxtRepeated => Results.File(bytes, "text/plain", $"{deckTitle}.txt"),
            _ => Results.BadRequest()
        };
    }

    [HttpPost("study-decks/{id:int}/vocabulary-count")]
    [SwaggerOperation(Summary = "Count vocabulary for study deck download options")]
    public async Task<IResult> GetStudyDeckVocabularyCount(int id, [FromBody] DeckDownloadRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var studyDeck = await userContext.UserStudyDecks
            .AsNoTracking()
            .FirstOrDefaultAsync(sd => sd.UserStudyDeckId == id && sd.UserId == userId);
        if (studyDeck == null) return Results.NotFound();

        int count;
        switch (studyDeck.DeckType)
        {
            case StudyDeckType.MediaDeck:
            {
                if (!studyDeck.DeckId.HasValue) return Results.BadRequest();
                var deck = await context.Decks.AsNoTracking().FirstOrDefaultAsync(d => d.DeckId == studyDeck.DeckId.Value);
                if (deck == null) return Results.NotFound();

                var (words, error) = await deckWordResolver.ResolveDeckWords(new DeckWordResolveRequest(
                    studyDeck.DeckId.Value, deck,
                    request.DownloadType, DeckOrder.DeckFrequency,
                    request.MinFrequency, request.MaxFrequency,
                    request.ExcludeMatureMasteredBlacklisted, request.ExcludeAllTrackedWords,
                    request.TargetPercentage, request.MinOccurrences, request.MaxOccurrences,
                    studyDeck.PosFilter, studyDeck.StartFromKnown));
                if (error != null) return error;
                if (words == null || words.Count == 0) return Results.Ok(0);

                if (request.ExcludeKana)
                {
                    var wordIds = words.Select(dw => dw.WordId).Distinct().ToList();
                    var forms = await WordFormHelper.LoadWordForms(context, wordIds);
                    words = words.Where(dw =>
                    {
                        var form = forms.GetValueOrDefault((dw.WordId, (short)dw.ReadingIndex));
                        return form == null || !WanaKana.IsKana(form.Text);
                    }).ToList();
                }

                count = words.Count;
                break;
            }
            case StudyDeckType.GlobalDynamic:
            {
                var result = await deckWordResolver.ResolveGlobalDynamicWords(
                    studyDeck.MinGlobalFrequency, studyDeck.MaxGlobalFrequency, studyDeck.PosFilter,
                    request.ExcludeKana, request.ExcludeMatureMasteredBlacklisted, request.ExcludeAllTrackedWords);
                count = result.Words.Count;
                break;
            }
            case StudyDeckType.StaticWordList:
            {
                var (staticCount, _) = await deckWordResolver.CountStaticDeckWords(id, request.ExcludeKana,
                    request.ExcludeMatureMasteredBlacklisted, request.ExcludeAllTrackedWords);
                count = staticCount;
                break;
            }
            default:
                return Results.BadRequest();
        }

        return Results.Ok(count);
    }

    [HttpGet("study-decks/{id:int}/vocabulary-count-frequency")]
    [SwaggerOperation(Summary = "Count study deck vocabulary in frequency range")]
    public async Task<IResult> GetStudyDeckVocabularyCountByFrequency(int id, int minFrequency, int maxFrequency)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var studyDeck = await userContext.UserStudyDecks
            .AsNoTracking()
            .FirstOrDefaultAsync(sd => sd.UserStudyDeckId == id && sd.UserId == userId);
        if (studyDeck == null) return Results.NotFound();

        if (studyDeck.DeckType != StudyDeckType.MediaDeck || !studyDeck.DeckId.HasValue)
        {
            var totalWords = await userContext.UserStudyDeckWords
                .CountAsync(w => w.UserStudyDeckId == id);
            return Results.Ok(totalWords);
        }

        var count = await context.DeckWords.AsNoTracking()
            .Where(dw => dw.DeckId == studyDeck.DeckId.Value &&
                         context.WordFormFrequencies
                             .Any(wff => wff.WordId == dw.WordId &&
                                         wff.ReadingIndex == (short)dw.ReadingIndex &&
                                         wff.FrequencyRank >= minFrequency &&
                                         wff.FrequencyRank <= maxFrequency))
            .CountAsync();

        return Results.Ok(count);
    }

    [HttpGet("study-decks/{id:int}/vocabulary-count-occurrences")]
    [SwaggerOperation(Summary = "Count study deck vocabulary by occurrence count")]
    public async Task<IResult> GetStudyDeckVocabularyCountByOccurrences(int id, int? minOccurrences = null, int? maxOccurrences = null)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var studyDeck = await userContext.UserStudyDecks
            .AsNoTracking()
            .FirstOrDefaultAsync(sd => sd.UserStudyDeckId == id && sd.UserId == userId);
        if (studyDeck == null) return Results.NotFound();

        if (studyDeck.DeckType != StudyDeckType.MediaDeck || !studyDeck.DeckId.HasValue)
        {
            var totalWords = await userContext.UserStudyDeckWords
                .CountAsync(w => w.UserStudyDeckId == id);
            return Results.Ok(totalWords);
        }

        var query = context.DeckWords.AsNoTracking().Where(dw => dw.DeckId == studyDeck.DeckId.Value);
        if (minOccurrences.HasValue)
            query = query.Where(dw => dw.Occurrences >= minOccurrences.Value);
        if (maxOccurrences.HasValue)
            query = query.Where(dw => dw.Occurrences <= maxOccurrences.Value);

        return Results.Ok(await query.CountAsync());
    }

    private async Task<HashSet<long>> BuildDeckReviewFilter(
        string userId,
        List<(int WordId, byte ReadingIndex)>? cardKeys = null)
    {
        var studyDecks = await userContext.UserStudyDecks
            .AsNoTracking()
            .Where(sd => sd.UserId == userId && sd.IsActive)
            .ToListAsync();

        var mediaDecks = studyDecks
            .Where(sd => sd.DeckType == StudyDeckType.MediaDeck && sd.DeckId.HasValue)
            .ToList();
        var wordKeys = await GetFilteredMediaWordKeys(mediaDecks);

        var staticDeckIds = studyDecks
            .Where(sd => sd.DeckType == StudyDeckType.StaticWordList)
            .Select(sd => sd.UserStudyDeckId).ToList();
        if (staticDeckIds.Count > 0)
            wordKeys.UnionWith(await deckWordResolver.GetStaticDeckWordKeys(staticDeckIds));

        if (cardKeys != null)
        {
            var globalDynamicDecks = studyDecks.Where(sd => sd.DeckType == StudyDeckType.GlobalDynamic).ToList();
            if (globalDynamicDecks.Count > 0)
            {
                var unmatchedWordIds = cardKeys
                    .Where(k => !wordKeys.Contains(WordFormHelper.EncodeWordKey(k.WordId, k.ReadingIndex)))
                    .Select(k => k.WordId)
                    .Distinct()
                    .ToList();

                if (unmatchedWordIds.Count > 0)
                {
                    foreach (var gd in globalDynamicDecks)
                    {
                        wordKeys.UnionWith(await deckWordResolver.GetGlobalDynamicWordKeysForWordIds(
                            gd.MinGlobalFrequency, gd.MaxGlobalFrequency, gd.PosFilter, unmatchedWordIds, gd.ExcludeKana));
                    }
                }
            }
        }

        return wordKeys;
    }

    private async Task<HashSet<long>> GetFilteredMediaWordKeys(List<UserStudyDeck> mediaStudyDecks)
    {
        var wordKeys = new HashSet<long>();
        if (mediaStudyDecks.Count == 0) return wordKeys;

        var deckIds = mediaStudyDecks.Select(sd => sd.DeckId!.Value).Distinct().ToList();
        var deckMap = await context.Decks.AsNoTracking()
            .Where(d => deckIds.Contains(d.DeckId))
            .ToDictionaryAsync(d => d.DeckId);

        foreach (var sd in mediaStudyDecks)
        {
            if (!deckMap.TryGetValue(sd.DeckId!.Value, out var deck)) continue;

            if ((DeckDownloadType)sd.DownloadType == DeckDownloadType.TargetCoverage && sd.TargetPercentage.HasValue)
            {
                var (_, keys) = await deckWordResolver.CountTargetCoverageWords(
                    sd.DeckId!.Value, deck, sd.TargetPercentage.Value, sd.ExcludeKana, sd.PosFilter, sd.StartFromKnown);
                wordKeys.UnionWith(keys);
            }
            else
            {
                var request = new DeckWordResolveRequest(
                    sd.DeckId!.Value, deck,
                    (DeckDownloadType)sd.DownloadType, (DeckOrder)sd.Order,
                    sd.MinFrequency, sd.MaxFrequency,
                    false, false,
                    sd.TargetPercentage,
                    sd.MinOccurrences, sd.MaxOccurrences,
                    sd.PosFilter, sd.StartFromKnown);
                var (_, keys) = await deckWordResolver.CountDeckWords(request, sd.ExcludeKana);
                wordKeys.UnionWith(keys);
            }
        }

        return wordKeys;
    }

    private async Task<string?> ValidateWordLimits(string userId, int deckId, int wordsToAdd)
    {
        var userDeckIds = await userContext.UserStudyDecks
            .Where(sd => sd.UserId == userId)
            .Select(sd => sd.UserStudyDeckId)
            .ToListAsync();
        var totalUserWords = await userContext.UserStudyDeckWords
            .CountAsync(w => userDeckIds.Contains(w.UserStudyDeckId));
        if (totalUserWords + wordsToAdd > 200_000)
            return wordsToAdd == 1
                ? "Maximum of 200,000 total static deck words reached."
                : $"Adding {wordsToAdd} words would exceed the 200,000 total limit.";

        return null;
    }
}
