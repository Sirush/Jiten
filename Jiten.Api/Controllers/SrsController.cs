using System.Globalization;
using System.Text.Json;
using Jiten.Api.Dtos;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Helpers;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.User;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/srs")]
[Authorize]
public class SrsController(
    JitenDbContext context,
    UserDbContext userContext,
    ICurrentUserService currentUserService,
    ISrsDebounceService debounceService,
    IStudySessionService sessionService,
    IWordFormSiblingCache wordFormCache,
    SrsRecomputeJob recomputeJob,
    ILogger<SrsController> logger) : ControllerBase
{
    [HttpPost("undo-review")]
    [SwaggerOperation(Summary = "Undo the last review for a card",
                      Description = "Removes the most recent review log and reconstructs the card state by replaying remaining reviews.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IResult> UndoReview(UndoReviewRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        if (!debounceService.TryAcquire("undo", userId, request.WordId, request.ReadingIndex))
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);

        await using var transaction = await userContext.Database.BeginTransactionAsync();

        var card = await userContext.FsrsCards
            .Include(c => c.ReviewLogs)
            .FirstOrDefaultAsync(c => c.UserId == userId
                                      && c.WordId == request.WordId
                                      && c.ReadingIndex == request.ReadingIndex);

        if (card == null)
            return Results.NotFound("Card not found");

        var lastLog = card.ReviewLogs
            .OrderByDescending(l => l.ReviewDateTime)
            .FirstOrDefault();

        if (lastLog == null)
            return Results.BadRequest("No review to undo");

        userContext.FsrsReviewLogs.Remove(lastLog);

        var remainingLogs = card.ReviewLogs
            .Where(l => l.ReviewLogId != lastLog.ReviewLogId)
            .OrderBy(l => l.ReviewDateTime)
            .ToList();

        var cardDeleted = false;

        if (remainingLogs.Count == 0)
        {
            userContext.FsrsCards.Remove(card);
            cardDeleted = true;
        }
        else
        {
            var userSettings = await LoadUserSettings(userId);
            var parameters = GetParameters(userSettings);
            var desiredRetention = GetDesiredRetention(userSettings);
            var scheduler = new FsrsScheduler(desiredRetention: desiredRetention, parameters: parameters, enableFuzzing: false);

            var replayCard = new FsrsCard(userId, card.WordId, card.ReadingIndex);
            var lapses = 0;
            foreach (var log in remainingLogs)
            {
                var prevState = replayCard.State;
                var reviewDt = DateTime.SpecifyKind(log.ReviewDateTime, DateTimeKind.Utc);
                var result = scheduler.ReviewCard(replayCard, log.Rating, reviewDt);
                if (prevState == FsrsState.Review && log.Rating == FsrsRating.Again)
                    lapses++;
                replayCard = result.UpdatedCard;
            }

            card.State = replayCard.State;
            card.Step = replayCard.Step;
            card.Stability = replayCard.Stability;
            card.Difficulty = replayCard.Difficulty;
            card.Due = replayCard.Due;
            card.LastReview = replayCard.LastReview;
            card.Lapses = lapses;
        }

        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
        await userContext.SaveChangesAsync();
        await transaction.CommitAsync();
        await sessionService.BumpStudyOverviewVersion(userId);

        logger.LogInformation("User undid SRS review: WordId={WordId}, ReadingIndex={ReadingIndex}, CardDeleted={CardDeleted}",
                              request.WordId, request.ReadingIndex, cardDeleted);

        return Results.Json(new { success = true, cardDeleted });
    }

    /// <summary>
    /// Rate (review) a word using the FSRS scheduler.
    /// </summary>
    /// <param name="request">A request containing the word to review and a rating</param>
    /// <returns>Status.</returns>
    [HttpPost("review")]
    [SwaggerOperation(Summary = "Review a FSRS card",
                      Description = "Rate (review) a word using the FSRS scheduler.")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IResult> Review(SrsReviewRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        if (!Enum.IsDefined(request.Rating))
            return Results.BadRequest($"Invalid rating: {(int)request.Rating}. Must be 1 (Again), 2 (Hard), 3 (Good), or 4 (Easy).");

        var hasIdempotency = !string.IsNullOrEmpty(request.SessionId) && !string.IsNullOrEmpty(request.ClientRequestId);

        if (hasIdempotency)
        {
            if (!await sessionService.ValidateSession(request.SessionId!, userId))
                return Results.Unauthorized();

            var cached = await sessionService.GetCachedReviewResult(request.SessionId!, request.ClientRequestId!);
            if (cached != null)
                return Results.Content(cached, "application/json");
        }

        if (!debounceService.TryAcquire("review", userId, request.WordId, request.ReadingIndex))
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        await using var transaction = await userContext.Database.BeginTransactionAsync();

        var card = await userContext.FsrsCards.FirstOrDefaultAsync(c => c.UserId == userId &&
                                                                        c.WordId == request.WordId &&
                                                                        c.ReadingIndex == request.ReadingIndex);

        var userSettings = await LoadUserSettings(userId);
        var parameters = GetParameters(userSettings);
        var desiredRetention = GetDesiredRetention(userSettings);
        var studySettings = GetStudySettings(userSettings);
        var loadBalancer = await BuildLoadBalancer(userId, studySettings.LoadBalancing);
        var easyDays = BuildEasyDaysPolicy(studySettings);
        var scheduler = new FsrsScheduler(desiredRetention: desiredRetention, parameters: parameters,
                                          loadBalancer: loadBalancer, easyDays: easyDays);
        if (card == null)
        {
            card = new FsrsCard(userId, request.WordId, request.ReadingIndex);
        }

        var previousState = card.State;
        var cardAndLog = scheduler.ReviewCard(card, request.Rating, DateTime.UtcNow, request.ReviewDuration);

        var leechDetected = false;
        var leechSuspended = false;
        var isLapse = previousState == FsrsState.Review && request.Rating == FsrsRating.Again;

        if (isLapse)
        {
            cardAndLog.UpdatedCard.Lapses = card.Lapses + 1;

            var threshold = studySettings.LeechThreshold;

            if (threshold > 0)
            {
                var lapseCount = cardAndLog.UpdatedCard.Lapses;
                var halfThreshold = Math.Max(threshold / 2, 1);
                if (lapseCount == threshold || (lapseCount > threshold && (lapseCount - threshold) % halfThreshold == 0))
                {
                    leechDetected = true;
                    if (studySettings.LeechAction == LeechAction.Suspend)
                    {
                        cardAndLog.UpdatedCard.State = FsrsState.Suspended;
                        leechSuspended = true;
                    }
                }
            }
        }
        else
        {
            cardAndLog.UpdatedCard.Lapses = card.Lapses;
        }

        if (card.CardId == 0)
        {
            await userContext.FsrsCards.AddAsync(cardAndLog.UpdatedCard);
            await userContext.SaveChangesAsync();

            cardAndLog.ReviewLog.CardId = cardAndLog.UpdatedCard.CardId;
        }
        else
        {
            userContext.Entry(card).CurrentValues.SetValues(cardAndLog.UpdatedCard);
            cardAndLog.ReviewLog.CardId = card.CardId;
        }

        await userContext.FsrsReviewLogs.AddAsync(cardAndLog.ReviewLog);
        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
        await userContext.SaveChangesAsync();
        await transaction.CommitAsync();
        await sessionService.BumpStudyOverviewVersion(userId);

        var previewScheduler = new FsrsScheduler(desiredRetention: desiredRetention, parameters: parameters, enableFuzzing: false);
        var intervals = previewScheduler.PreviewIntervals(cardAndLog.UpdatedCard, DateTime.UtcNow);

        var resultObj = new
        {
            success = true,
            nextDue = cardAndLog.UpdatedCard.Due,
            newState = (int)cardAndLog.UpdatedCard.State,
            stability = cardAndLog.UpdatedCard.Stability,
            difficulty = cardAndLog.UpdatedCard.Difficulty,
            leechDetected,
            leechSuspended,
            intervalPreview = new
            {
                againSeconds = (int)intervals[FsrsRating.Again].TotalSeconds,
                hardSeconds = (int)intervals[FsrsRating.Hard].TotalSeconds,
                goodSeconds = (int)intervals[FsrsRating.Good].TotalSeconds,
                easySeconds = (int)intervals[FsrsRating.Easy].TotalSeconds,
            }
        };

        if (hasIdempotency)
        {
            var resultJson = System.Text.Json.JsonSerializer.Serialize(resultObj);
            _ = sessionService.StoreCachedReviewResult(request.SessionId!, request.ClientRequestId!, resultJson);
        }

        logger.LogInformation("User reviewed SRS card: WordId={WordId}, ReadingIndex={ReadingIndex}, Rating={Rating}, NewState={NewState}",
                              request.WordId, request.ReadingIndex, request.Rating, cardAndLog.UpdatedCard.State);
        return Results.Json(resultObj);
    }

    /// <summary>
    /// Review multiple words at once using the FSRS scheduler. Used by the reader extension's
    /// "review everything on screen" action.
    /// </summary>
    /// <param name="request">A request containing the words to review and their ratings.</param>
    /// <returns>Per-card new states and the list of words suspended as leeches.</returns>
    [HttpPost("batch-review")]
    [SwaggerOperation(Summary = "Batch review FSRS cards",
                      Description = "Rate (review) multiple words at once using the FSRS scheduler, in a single transaction.")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IResult> BatchReview(SrsBatchReviewRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        if (request.Reviews.Count == 0)
            return Results.BadRequest("No reviews provided.");
        if (request.Reviews.Count > 500)
            return Results.BadRequest("Maximum of 500 reviews per batch.");

        // Dedupe by (WordId, ReadingIndex), keeping the last rating for each word.
        var deduped = new Dictionary<(int WordId, byte ReadingIndex), FsrsRating>();
        foreach (var item in request.Reviews)
        {
            if (!Enum.IsDefined(item.Rating))
                return Results.BadRequest($"Invalid rating: {(int)item.Rating}. Must be 1 (Again), 2 (Hard), 3 (Good), or 4 (Easy).");

            deduped[(item.WordId, item.ReadingIndex)] = item.Rating;
        }

        var now = DateTime.UtcNow;

        var userSettings = await LoadUserSettings(userId);
        var parameters = GetParameters(userSettings);
        var desiredRetention = GetDesiredRetention(userSettings);
        var studySettings = GetStudySettings(userSettings);
        var loadBalancer = await BuildLoadBalancer(userId, studySettings.LoadBalancing);
        var easyDays = BuildEasyDaysPolicy(studySettings);
        var scheduler = new FsrsScheduler(desiredRetention: desiredRetention, parameters: parameters,
                                          loadBalancer: loadBalancer, easyDays: easyDays);

        await using var transaction = await userContext.Database.BeginTransactionAsync();

        var wordIds = deduped.Keys.Select(k => k.WordId).Distinct().ToList();
        var existingCards = await userContext.FsrsCards
            .Where(c => c.UserId == userId && wordIds.Contains(c.WordId))
            .ToListAsync();
        var existingMap = existingCards.ToDictionary(c => (c.WordId, c.ReadingIndex));

        var results = new List<object>(deduped.Count);
        var leechSuspended = new List<int>();
        // New cards must be inserted before their review logs can reference a CardId.
        var pendingNewLogs = new List<(FsrsCard Card, FsrsReviewLog Log)>();

        foreach (var (key, rating) in deduped)
        {
            existingMap.TryGetValue(key, out var card);
            var isNew = card == null;
            card ??= new FsrsCard(userId, key.WordId, key.ReadingIndex);

            var previousState = card.State;
            var cardAndLog = scheduler.ReviewCard(card, rating, now);

            var isLapse = previousState == FsrsState.Review && rating == FsrsRating.Again;
            if (isLapse)
            {
                cardAndLog.UpdatedCard.Lapses = card.Lapses + 1;

                var threshold = studySettings.LeechThreshold;
                if (threshold > 0)
                {
                    var lapseCount = cardAndLog.UpdatedCard.Lapses;
                    var halfThreshold = Math.Max(threshold / 2, 1);
                    if (lapseCount == threshold || (lapseCount > threshold && (lapseCount - threshold) % halfThreshold == 0))
                    {
                        if (studySettings.LeechAction == LeechAction.Suspend)
                        {
                            cardAndLog.UpdatedCard.State = FsrsState.Suspended;
                            leechSuspended.Add(key.WordId);
                        }
                    }
                }
            }
            else
            {
                cardAndLog.UpdatedCard.Lapses = card.Lapses;
            }

            if (isNew)
            {
                await userContext.FsrsCards.AddAsync(cardAndLog.UpdatedCard);
                pendingNewLogs.Add((cardAndLog.UpdatedCard, cardAndLog.ReviewLog));
            }
            else
            {
                userContext.Entry(card).CurrentValues.SetValues(cardAndLog.UpdatedCard);
                cardAndLog.ReviewLog.CardId = card.CardId;
                await userContext.FsrsReviewLogs.AddAsync(cardAndLog.ReviewLog);
            }

            results.Add(new { wordId = key.WordId, readingIndex = key.ReadingIndex, newState = (int)cardAndLog.UpdatedCard.State });
        }

        // Persist new cards first so EF assigns their CardIds, then attach their logs.
        if (pendingNewLogs.Count > 0)
        {
            await userContext.SaveChangesAsync();
            foreach (var (card, log) in pendingNewLogs)
            {
                log.CardId = card.CardId;
                await userContext.FsrsReviewLogs.AddAsync(log);
            }
        }

        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
        await userContext.SaveChangesAsync();
        await transaction.CommitAsync();
        await sessionService.BumpStudyOverviewVersion(userId);

        logger.LogInformation("User batch-reviewed {Count} SRS cards ({Suspended} suspended as leeches).",
                              deduped.Count, leechSuspended.Count);

        return Results.Json(new
        {
            success = true,
            processed = deduped.Count,
            leechSuspended,
            results
        });
    }

    [HttpGet("review-history/{wordId}/{readingIndex}")]
    [SwaggerOperation(Summary = "Get review history for a word")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IResult> GetReviewHistory(int wordId, byte readingIndex)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var card = await userContext.FsrsCards
            .Include(c => c.ReviewLogs)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.UserId == userId
                                      && c.WordId == wordId
                                      && c.ReadingIndex == readingIndex);

        if (card == null)
            return Results.Ok(new ReviewHistoryDto());

        return Results.Ok(new ReviewHistoryDto
        {
            Card = new CardStateDto
            {
                State = (int)card.State,
                Stability = card.Stability,
                Difficulty = card.Difficulty,
                Due = card.Due,
                LastReview = card.LastReview,
                CreatedAt = card.CreatedAt,
                Lapses = card.Lapses,
            },
            Reviews = card.ReviewLogs
                .OrderByDescending(l => l.ReviewDateTime)
                .Select(l => new ReviewLogDto
                {
                    Rating = (int)l.Rating,
                    ReviewDateTime = l.ReviewDateTime,
                    ReviewDuration = l.ReviewDuration,
                })
                .ToList()
        });
    }

    [HttpGet("review-history")]
    [SwaggerOperation(Summary = "Get paginated review history across all cards")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IResult> GetRecentReviews([FromQuery] int offset = 0, [FromQuery] int limit = 100)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 100);

        var totalCount = await userContext.FsrsReviewLogs
            .Where(l => l.Card.UserId == userId)
            .CountAsync();

        var logs = await userContext.FsrsReviewLogs
            .AsNoTracking()
            .Where(l => l.Card.UserId == userId)
            .OrderByDescending(l => l.ReviewDateTime)
            .Skip(offset)
            .Take(limit)
            .Select(l => new
            {
                l.Card.WordId,
                l.Card.ReadingIndex,
                l.Rating,
                l.ReviewDateTime,
                l.ReviewDuration,
                CardState = (int)l.Card.State
            })
            .ToListAsync();

        var wordIds = logs.Select(l => l.WordId).Distinct().ToList();
        var formDict = await WordFormHelper.LoadWordForms(context, wordIds);

        var reviews = logs.Select(l =>
        {
            var form = formDict.GetValueOrDefault((l.WordId, l.ReadingIndex));
            return new RecentReviewDto
            {
                WordId = l.WordId,
                ReadingIndex = l.ReadingIndex,
                WordText = form?.RubyText ?? form?.Text ?? "",
                Rating = (int)l.Rating,
                ReviewDateTime = l.ReviewDateTime,
                ReviewDuration = l.ReviewDuration,
                CardState = l.CardState
            };
        }).ToList();

        return Results.Ok(new PaginatedResponse<List<RecentReviewDto>>(reviews, totalCount, limit, offset));
    }

    [HttpGet("settings")]
    [SwaggerOperation(Summary = "Get FSRS settings",
                      Description = "Get the user's FSRS settings.")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IResult> GetParameters()
    {
        var userId = currentUserService.UserId;
        if (userId == null)
        {
            return Results.Unauthorized();
        }

        var userSettings = await LoadUserSettings(userId);
        var parameters = GetParameters(userSettings);
        var desiredRetention = GetDesiredRetention(userSettings);
        var isDefault = IsSettingsDefault(parameters, desiredRetention);
        var reviewCount = await userContext.FsrsReviewLogs
            .CountAsync(r => r.Card.UserId == userId);
        var response = new FsrsParametersResponse
        {
            Parameters = SerializeParametersCsv(parameters),
            IsDefault = isDefault,
            DesiredRetention = desiredRetention,
            ReviewCount = reviewCount,
            MinimumReviewsForOptimize = FsrsOptimizer.MinimumReviews
        };

        return Results.Ok(response);
    }

    [HttpPut("settings")]
    [SwaggerOperation(Summary = "Update FSRS settings",
                      Description = "Update the user's FSRS settings.")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IResult> UpdateParameters(UpdateFsrsParametersRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null)
        {
            return Results.Unauthorized();
        }

        var settings = await userContext.UserFsrsSettings.FirstOrDefaultAsync(s => s.UserId == userId);
        var currentParameters = GetParameters(settings);
        var currentDesiredRetention = GetDesiredRetention(settings);

        var nextParameters = currentParameters;
        var nextDesiredRetention = currentDesiredRetention;

        if (request.Parameters != null)
        {
            if (string.IsNullOrWhiteSpace(request.Parameters))
            {
                nextParameters = FsrsConstants.DefaultParameters;
            }
            else if (!TryParseParametersCsv(request.Parameters, out var parsedParameters, out var error))
            {
                return Results.BadRequest(error);
            }
            else
            {
                nextParameters = parsedParameters;
            }
        }

        if (request.DesiredRetention.HasValue)
        {
            if (!IsDesiredRetentionValid(request.DesiredRetention.Value))
            {
                return Results.BadRequest("Desired retention must be between 0 and 1.");
            }

            nextDesiredRetention = request.DesiredRetention.Value;
        }

        var parametersAreDefault = AreParametersDefault(nextParameters);
        var desiredRetentionIsDefault = IsDesiredRetentionDefault(nextDesiredRetention);
        var shouldRemoveSettings = parametersAreDefault && desiredRetentionIsDefault;

        if (shouldRemoveSettings)
        {
            if (settings != null)
            {
                settings.Parameters = Array.Empty<double>();
                settings.DesiredRetention = null;
                await userContext.SaveChangesAsync();
            }
        }
        else
        {
            if (settings == null)
            {
                settings = new UserFsrsSettings { UserId = userId };
                userContext.UserFsrsSettings.Add(settings);
            }

            settings.Parameters = nextParameters;
            settings.DesiredRetention = desiredRetentionIsDefault ? null : nextDesiredRetention;
            await userContext.SaveChangesAsync();
        }

        await sessionService.BumpStudyOverviewVersion(userId);
        return Results.Ok(new FsrsParametersResponse
        {
            Parameters = SerializeParametersCsv(nextParameters),
            IsDefault = shouldRemoveSettings,
            DesiredRetention = nextDesiredRetention
        });
    }

    [HttpPost("settings/optimize")]
    [EnableRateLimiting("compute")]
    [SwaggerOperation(Summary = "Optimize FSRS parameters",
                      Description = "Optimize FSRS parameters based on the user's review history, save them, and recompute scheduling.")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IResult> OptimizeParameters([FromQuery] bool reschedule = true)
    {
        var userId = currentUserService.UserId;
        if (userId == null)
            return Results.Unauthorized();

        var reviewLogs = await userContext.FsrsReviewLogs
            .Where(r => r.Card.UserId == userId)
            .OrderBy(r => r.CardId)
            .ThenBy(r => r.ReviewDateTime)
            .Select(r => new { r.CardId, r.Rating, r.ReviewDateTime })
            .ToListAsync();

        var totalReviews = reviewLogs.Count;
        if (totalReviews < FsrsOptimizer.MinimumReviews)
            return Results.BadRequest(new
            {
                error = $"At least {FsrsOptimizer.MinimumReviews} reviews are required to optimise parameters. You have {totalReviews}."
            });

        var grouped = reviewLogs
            .GroupBy(r => r.CardId)
            .Where(g => g.Count() >= 2)
            .ToList();

        var items = new List<FsrsTrainingItem>();
        foreach (var group in grouped)
        {
            var logs = group.ToList();
            var reviews = new FsrsTrainingReview[logs.Count];
            reviews[0] = new FsrsTrainingReview((int)logs[0].Rating, 0);
            for (var i = 1; i < logs.Count; i++)
            {
                var deltaT = Math.Max(0, (logs[i].ReviewDateTime - logs[i - 1].ReviewDateTime).TotalDays);
                reviews[i] = new FsrsTrainingReview((int)logs[i].Rating, deltaT);
            }
            items.Add(new FsrsTrainingItem(reviews));
        }

        if (items.Count == 0)
            return Results.BadRequest(new { error = "Not enough review data to optimise." });

        var result = FsrsOptimizer.Optimize(items);

        var settings = await userContext.UserFsrsSettings.FirstOrDefaultAsync(s => s.UserId == userId);
        var desiredRetention = GetDesiredRetention(settings);

        if (settings == null)
        {
            settings = new UserFsrsSettings { UserId = userId };
            userContext.UserFsrsSettings.Add(settings);
        }
        settings.Parameters = result.Parameters;
        await userContext.SaveChangesAsync();

        if (reschedule)
        {
            var studySettings = GetStudySettings(settings);
            await recomputeJob.RecomputeUserSrs(userId, result.Parameters, desiredRetention,
                                                studySettings.LoadBalancing, BuildEasyDaysPolicy(studySettings));
        }
        await sessionService.BumpStudyOverviewVersion(userId);

        return Results.Ok(new
        {
            parameters = SerializeParametersCsv(result.Parameters),
            loss = Math.Round(result.Loss, 6),
            reviewCount = result.ReviewCount,
            isDefault = false,
            desiredRetention,
            rescheduled = reschedule
        });
    }

    [HttpGet("settings/health")]
    [SwaggerOperation(Summary = "FSRS review-history health check",
                      Description = "Pre-optimize sanity report: review count, grade distribution, same-day reviews, "
                                    + "imported-history breakdown, and grade-usage patterns that corrupt parameter training.")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IResult> GetHealth()
    {
        var userId = currentUserService.UserId;
        if (userId == null)
            return Results.Unauthorized();

        // Loaded into memory (like the optimizer) so the same-day grouping stays provider-agnostic
        // across Postgres and the SQLite integration harness.
        var logs = await userContext.FsrsReviewLogs
            .Where(r => r.Card.UserId == userId)
            .OrderBy(r => r.CardId)
            .ThenBy(r => r.ReviewDateTime)
            .Select(r => new { r.CardId, r.Rating, r.ReviewDateTime })
            .ToListAsync();

        var total = logs.Count;
        var ratingCounts = new int[4];
        var sameDay = 0;
        long prevCardId = -1;
        var prevDay = default(DateTime);

        foreach (var log in logs)
        {
            var idx = (int)log.Rating - 1;
            if (idx >= 0 && idx < 4)
                ratingCounts[idx]++;

            var day = log.ReviewDateTime.Date;
            if (log.CardId == prevCardId && day == prevDay)
                sameDay++;
            prevCardId = log.CardId;
            prevDay = day;
        }

        var min = FsrsOptimizer.MinimumReviews;
        var hard = ratingCounts[1];
        var easy = ratingCounts[3];
        var again = ratingCounts[0];

        // "Hard as fail": only flag once there is enough graded history, when Hard is used heavily yet
        // Again is used almost never — the tell-tale of a user pressing Hard for cards they actually failed.
        var likelyHardAsFail = total >= min
                               && hard >= total * 0.18
                               && again <= total * 0.02;

        var response = new FsrsHealthResponse
        {
            TotalReviews = total,
            RatingCounts = ratingCounts,
            SameDayReviews = sameDay,
            MinimumReviewsForOptimize = min,
            MeetsMinimum = total >= min,
            NeverUsesHard = total >= min && hard == 0,
            NeverUsesEasy = total >= min && easy == 0,
            LikelyHardAsFail = likelyHardAsFail,
        };

        return Results.Ok(response);
    }

    [HttpPost("settings/remap-hard")]
    [EnableRateLimiting("compute")]
    [SwaggerOperation(Summary = "Remap historical Hard ratings to Again",
                      Description = "Repair for the 'Hard means I failed' pattern: rewrites Hard reviews to Again "
                                    + "(optionally within a date range) and reschedules so parameter training is not biased.")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IResult> RemapHardReviews(RemapHardReviewsRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null)
            return Results.Unauthorized();

        var query = userContext.FsrsReviewLogs
            .Where(r => r.Card.UserId == userId && r.Rating == FsrsRating.Hard);

        if (request.From.HasValue)
        {
            var from = DateTime.SpecifyKind(request.From.Value, DateTimeKind.Utc);
            query = query.Where(r => r.ReviewDateTime >= from);
        }
        if (request.To.HasValue)
        {
            var to = DateTime.SpecifyKind(request.To.Value, DateTimeKind.Utc);
            query = query.Where(r => r.ReviewDateTime <= to);
        }

        var toRemap = await query.ToListAsync();
        foreach (var log in toRemap)
            log.Rating = FsrsRating.Again;

        if (toRemap.Count > 0)
            await userContext.SaveChangesAsync();

        if (request.Reschedule && toRemap.Count > 0)
        {
            var userSettings = await LoadUserSettings(userId);
            var parameters = GetParameters(userSettings);
            var desiredRetention = GetDesiredRetention(userSettings);
            var studySettings = GetStudySettings(userSettings);
            await recomputeJob.RecomputeUserSrs(userId, parameters, desiredRetention,
                                                studySettings.LoadBalancing, BuildEasyDaysPolicy(studySettings));
        }
        await sessionService.BumpStudyOverviewVersion(userId);

        logger.LogInformation("User remapped Hard→Again reviews: UserId={UserId}, Count={Count}, Rescheduled={Rescheduled}",
                              userId, toRemap.Count, request.Reschedule && toRemap.Count > 0);

        return Results.Ok(new { remapped = toRemap.Count, rescheduled = request.Reschedule && toRemap.Count > 0 });
    }

    [HttpPost("settings/recompute")]
    [SwaggerOperation(Summary = "Recompute FSRS scheduling",
                      Description = "Recompute scheduling for all cards using the stored settings (or defaults).")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IResult> RecomputeParameters()
    {
        var userId = currentUserService.UserId;
        if (userId == null)
        {
            return Results.Unauthorized();
        }

        var userSettings = await LoadUserSettings(userId);
        var parameters = GetParameters(userSettings);
        var desiredRetention = GetDesiredRetention(userSettings);
        var studySettings = GetStudySettings(userSettings);
        await recomputeJob.RecomputeUserSrs(userId, parameters, desiredRetention,
                                            studySettings.LoadBalancing, BuildEasyDaysPolicy(studySettings));
        await sessionService.BumpStudyOverviewVersion(userId);

        return Results.Ok(new { success = true });
    }

    [HttpPost("settings/recompute-batch")]
    [EnableRateLimiting("compute")]
    [SwaggerOperation(Summary = "Recompute FSRS scheduling batch",
                      Description = "Recompute scheduling in batches for progress tracking.")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IResult> RecomputeParametersBatch([FromQuery] long lastCardId = 0, [FromQuery] int batchSize = 500)
    {
        var userId = currentUserService.UserId;
        if (userId == null)
        {
            return Results.Unauthorized();
        }

        if (batchSize < 1 || batchSize > 2000)
        {
            batchSize = 500;
        }

        var userSettings = await LoadUserSettings(userId);
        var parameters = GetParameters(userSettings);
        var desiredRetention = GetDesiredRetention(userSettings);
        var studySettings = GetStudySettings(userSettings);
        var result = await recomputeJob.RecomputeUserSrsBatch(userId, parameters, desiredRetention, lastCardId, batchSize,
                                                              studySettings.LoadBalancing, null, BuildEasyDaysPolicy(studySettings));
        await sessionService.BumpStudyOverviewVersion(userId);

        return Results.Ok(result);
    }

    [HttpPost("set-vocabulary-state")]
    [SwaggerOperation(Summary = "Set vocabulary state",
                      Description = "Set vocabulary state to neverForget or blacklist.")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IResult> SetVocabularyState(SetVocabularyStateRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        if (!debounceService.TryAcquire("state", userId, request.WordId, request.ReadingIndex))
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        var card = await userContext.FsrsCards.FirstOrDefaultAsync(c =>
                                                                       c.UserId == userId &&
                                                                       c.WordId == request.WordId &&
                                                                       c.ReadingIndex == request.ReadingIndex);

        switch (request.State)
        {
            case "neverForget-add":
                if (card == null)
                {
                    card = new FsrsCard(userId, request.WordId, request.ReadingIndex,
                                        due: DateTime.UtcNow,
                                        lastReview: DateTime.UtcNow,
                                        state: FsrsState.Mastered);
                    await userContext.FsrsCards.AddAsync(card);
                }
                else
                {
                    card.State = FsrsState.Mastered;
                }

                await WordFormHelper.RemoveRedundantKanaSrsCards(userContext, wordFormCache, userId, request.WordId, request.ReadingIndex);

                break;

            case "neverForget-remove":
                if (card != null && card.State == FsrsState.Mastered)
                    RestoreCardState(card);
                break;

            case "blacklist-add":
                if (card == null)
                {
                    card = new FsrsCard(userId, request.WordId, request.ReadingIndex,
                                        state: FsrsState.Blacklisted);
                    await userContext.FsrsCards.AddAsync(card);
                }
                else
                {
                    card.State = FsrsState.Blacklisted;
                }

                break;

            case "blacklist-remove":
                if (card != null && card.State == FsrsState.Blacklisted)
                {
                    RestoreCardState(card);
                }
                else if (card == null)
                {
                    // Check if blacklisted via WordSet (two queries — separate DbContexts)
                    var blacklistedSetIds = await userContext.UserWordSetStates
                        .Where(uwss => uwss.UserId == userId && uwss.State == WordSetStateType.Blacklisted)
                        .Select(uwss => uwss.SetId)
                        .ToListAsync();

                    var isSetBlacklisted = blacklistedSetIds.Count > 0 &&
                        await context.WordSetMembers
                            .AnyAsync(wsm => blacklistedSetIds.Contains(wsm.SetId) &&
                                             wsm.WordId == request.WordId &&
                                             wsm.ReadingIndex == request.ReadingIndex);

                    if (isSetBlacklisted)
                    {
                        // Create FsrsCard to override set blacklist
                        card = new FsrsCard(userId, request.WordId, request.ReadingIndex,
                                            state: FsrsState.Learning);
                        await userContext.FsrsCards.AddAsync(card);
                    }
                }
                break;

            case "forget-add":
                if (card != null)
                {
                    userContext.FsrsCards.Remove(card);
                }

                break;

            case "suspend-add":
                if (card == null)
                {
                    card = new FsrsCard(userId, request.WordId, request.ReadingIndex,
                                        state: FsrsState.Suspended);
                    await userContext.FsrsCards.AddAsync(card);
                }
                else if (card.State != FsrsState.Suspended)
                {
                    card.State = FsrsState.Suspended;
                }

                break;

            case "suspend-remove":
                if (card != null && card.State == FsrsState.Suspended)
                {
                    RestoreCardState(card);
                }

                break;

            case "bury-add":
                if (card != null)
                {
                    var burySettings = GetStudySettings(await LoadUserSettings(userId));
                    card.Due = ComputeNextMidnightUtc(DateTime.UtcNow, burySettings.Timezone);
                }
                break;

            case "bury-remove":
                if (card != null)
                {
                    card.Due = DateTime.UtcNow;
                }
                break;

            default:
                return Results.BadRequest($"Invalid state: {request.State}");
        }

        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
        await userContext.SaveChangesAsync();
        await sessionService.BumpStudyOverviewVersion(userId);

        logger.LogInformation("User set vocabulary state: WordId={WordId}, ReadingIndex={ReadingIndex}, State={State}",
                              request.WordId, request.ReadingIndex, request.State);
        return Results.Json(new { success = true });

        void RestoreCardState(FsrsCard card)
        {
            if (card.Stability is > 0)
            {
                card.State = FsrsState.Review;
            }
            else
            {
                card.State = FsrsState.Learning;
                card.Step = 0;
            }

            card.Due = DateTime.UtcNow;
        }
    }

    [HttpPost("mass-action/preview")]
    [SwaggerOperation(Summary = "Preview mass action",
                      Description = "Returns count and paginated list of cards matching the filters.")]
    public async Task<IResult> MassActionPreview(MassActionRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var validationError = ValidateMassActionRequest(request, previewOnly: true);
        if (validationError != null) return Results.BadRequest(validationError);

        var query = BuildMassActionQuery(userId, request);
        var totalCount = await query.CountAsync();

        var limit = Math.Clamp(request.Limit, 1, 100);
        var offset = Math.Max(0, request.Offset);

        var cards = await query
            .OrderBy(c => c.Due)
            .Skip(offset)
            .Take(limit)
            .Select(c => new { c.WordId, c.ReadingIndex, c.State, c.Due, c.CreatedAt })
            .ToListAsync();

        var wordIds = cards.Select(c => c.WordId).Distinct().ToList();
        var formDict = await WordFormHelper.LoadWordForms(context, wordIds);
        var freqDict = await WordFormHelper.LoadWordFormFrequencies(context, wordIds);
        var words = await context.JMDictWords
            .AsNoTracking()
            .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
            .Where(w => wordIds.Contains(w.WordId))
            .ToDictionaryAsync(w => w.WordId);

        var result = cards.Select(c =>
        {
            var form = formDict.GetValueOrDefault((c.WordId, (short)c.ReadingIndex));
            var freq = freqDict.GetValueOrDefault((c.WordId, (short)c.ReadingIndex));
            var mainDef = words.GetValueOrDefault(c.WordId)?.Definitions
                .FirstOrDefault()?.EnglishMeanings.FirstOrDefault();

            return new MassActionCardDto
            {
                WordId = c.WordId,
                ReadingIndex = (byte)c.ReadingIndex,
                Reading = form?.RubyText ?? form?.Text ?? "",
                MainDefinition = mainDef,
                FrequencyRank = freq?.FrequencyRank ?? 0,
                State = c.State,
                Due = c.Due,
                CreatedAt = c.CreatedAt
            };
        }).ToList();

        return Results.Ok(new PaginatedResponse<List<MassActionCardDto>>(result, totalCount, limit, offset));
    }

    [HttpPost("mass-action/execute")]
    [SwaggerOperation(Summary = "Execute mass action",
                      Description = "Applies the configured action to all matching cards.")]
    public async Task<IResult> MassActionExecute(MassActionRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var validationError = ValidateMassActionRequest(request, previewOnly: false);
        if (validationError != null) return Results.BadRequest(validationError);

        var query = BuildMassActionQuery(userId, request);
        int affected;

        switch (request.Action)
        {
            case "change-state":
            {
                var target = (FsrsState)request.TargetState!.Value;
                affected = target switch
                {
                    FsrsState.Learning => await query.ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.State, FsrsState.Learning)
                        .SetProperty(c => c.Step, 0)
                        .SetProperty(c => c.Due, DateTime.UtcNow)),
                    _ => await query.ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.State, target))
                };
                break;
            }

            case "push-due":
            {
                var days = request.PushDays!.Value;

                if (request.StaggerBatchSize is > 0)
                {
                    var cardIds = await query.OrderBy(c => c.Due).Select(c => c.CardId).ToListAsync();
                    var batchSize = request.StaggerBatchSize.Value;
                    affected = 0;

                    for (var i = 0; i < cardIds.Count; i += batchSize)
                    {
                        var batch = cardIds.GetRange(i, Math.Min(batchSize, cardIds.Count - i));
                        var extraDays = i / batchSize;
                        var totalDays = days + extraDays;

                        affected += await userContext.FsrsCards
                            .Where(c => batch.Contains(c.CardId))
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(c => c.Due, c => c.Due.AddDays(totalDays)));
                    }
                }
                else
                {
                    affected = await query.ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.Due, c => c.Due.AddDays(days)));
                }

                break;
            }

            case "delete-cards":
                affected = await query.ExecuteDeleteAsync();
                break;

            case "reset-schedule":
                affected = await query.ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.State, FsrsState.Learning)
                    .SetProperty(c => c.Step, 0)
                    .SetProperty(c => c.Stability, (double?)null)
                    .SetProperty(c => c.Difficulty, (double?)null)
                    .SetProperty(c => c.Due, DateTime.UtcNow)
                    .SetProperty(c => c.LastReview, (DateTime?)null));
                break;

            default:
                return Results.BadRequest($"Invalid action: {request.Action}");
        }

        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
        await sessionService.BumpStudyOverviewVersion(userId);

        logger.LogInformation("User executed mass action: Action={Action}, AffectedCount={Count}",
            request.Action, affected);

        return Results.Json(new { success = true, affectedCount = affected });
    }

    [HttpPost("composition-inference/preview")]
    [SwaggerOperation(Summary = "Preview composition inference",
                      Description = "Returns words that can be inferred as known via word-composition relationships with the user's already-known words.")]
    public async Task<IResult> CompositionInferencePreview(CompositionInferenceRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var inferred = await ComputeInferredPairs(userId, request.Direction);
        if (inferred == null) return Results.BadRequest($"Invalid direction: {request.Direction}");

        var total = inferred.Count;
        var limit = Math.Clamp(request.Limit, 1, 100);
        var offset = Math.Max(0, request.Offset);
        var page = inferred.Skip(offset).Take(limit).ToList();

        var dtos = await HydrateInferredPairs(userId, page);
        return Results.Ok(new PaginatedResponse<List<MassActionCardDto>>(dtos, total, limit, offset));
    }

    [HttpPost("composition-inference/execute")]
    [SwaggerOperation(Summary = "Execute composition inference",
                      Description = "Marks all (or a specified subset of) inferred words as Mastered or Blacklisted.")]
    public async Task<IResult> CompositionInferenceExecute(CompositionInferenceExecuteRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        FsrsState target = request.TargetState switch
        {
            "mastered" => FsrsState.Mastered,
            "blacklisted" => FsrsState.Blacklisted,
            _ => (FsrsState)0
        };
        if (target == 0) return Results.BadRequest($"Invalid targetState: {request.TargetState}");

        List<(int WordId, byte ReadingIndex)> pairs;
        if (request.WordKeys is { Count: > 0 })
        {
            pairs = request.WordKeys
                .Select(k => (k.WordId, k.ReadingIndex))
                .Distinct()
                .ToList();
        }
        else
        {
            var inferred = await ComputeInferredPairs(userId, request.Direction);
            if (inferred == null) return Results.BadRequest($"Invalid direction: {request.Direction}");
            pairs = inferred;
        }

        if (pairs.Count == 0) return Results.Json(new { success = true, affectedCount = 0 });

        var wordIds = pairs.Select(p => p.WordId).Distinct().ToList();
        var existing = await userContext.FsrsCards
            .Where(c => c.UserId == userId && wordIds.Contains(c.WordId))
            .ToListAsync();
        var existingDict = existing.ToDictionary(c => (c.WordId, c.ReadingIndex));

        var now = DateTime.UtcNow;
        var affected = 0;
        foreach (var (wid, ri) in pairs)
        {
            if (existingDict.TryGetValue((wid, ri), out var card))
            {
                if (card.State == target) continue;
                card.State = target;
                if (target == FsrsState.Mastered)
                {
                    card.Due = now;
                    card.LastReview = now;
                }
            }
            else
            {
                card = target == FsrsState.Mastered
                    ? new FsrsCard(userId, wid, ri, due: now, lastReview: now, state: FsrsState.Mastered)
                    : new FsrsCard(userId, wid, ri, state: FsrsState.Blacklisted);
                await userContext.FsrsCards.AddAsync(card);
            }

            if (target == FsrsState.Mastered)
            {
                await WordFormHelper.RemoveRedundantKanaSrsCards(userContext, wordFormCache, userId, wid, ri);
            }
            affected++;
        }

        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
        await userContext.SaveChangesAsync();
        await sessionService.BumpStudyOverviewVersion(userId);

        logger.LogInformation("User executed composition inference: Direction={Direction}, Target={Target}, Affected={Count}",
            request.Direction, target, affected);

        return Results.Json(new { success = true, affectedCount = affected });
    }

    private async Task<List<(int WordId, byte ReadingIndex)>?> ComputeInferredPairs(string userId, string direction)
    {
        var (known, blocked) = await LoadKnownAndBlockedSets(userId);

        if (known.Count == 0)
            return new List<(int, byte)>();

        if (direction == "compound-to-components")
        {
            var knownWordIds = known.Select(k => k.WordId).Distinct().ToList();
            var raw = await context.WordCompositions
                .AsNoTracking()
                .Where(c => knownWordIds.Contains(c.WordId))
                .Select(c => new { c.WordId, c.ReadingIndex, c.ComponentWordId, c.ComponentReadingIndex })
                .ToListAsync();

            return raw
                .Where(r => known.Contains((r.WordId, (byte)r.ReadingIndex)))
                .Select(r => (WordId: r.ComponentWordId, ReadingIndex: (byte)r.ComponentReadingIndex))
                .Distinct()
                .Where(p => !blocked.Contains(p))
                .OrderBy(p => p.WordId).ThenBy(p => p.ReadingIndex)
                .ToList();
        }

        if (direction == "components-to-compound")
        {
            var knownComponentIds = known.Select(k => k.WordId).Distinct().ToList();

            var candidatePairs = await context.WordCompositions
                .AsNoTracking()
                .Where(c => knownComponentIds.Contains(c.ComponentWordId))
                .Select(c => new { c.WordId, c.ReadingIndex })
                .Distinct()
                .ToListAsync();

            if (candidatePairs.Count == 0)
                return new List<(int, byte)>();

            var candidateWordIds = candidatePairs.Select(r => r.WordId).Distinct().ToList();
            var candidateSet = candidatePairs.Select(r => (r.WordId, r.ReadingIndex)).ToHashSet();

            var allRows = await context.WordCompositions
                .AsNoTracking()
                .Where(c => candidateWordIds.Contains(c.WordId))
                .Select(c => new { c.WordId, c.ReadingIndex, c.ComponentWordId, c.ComponentReadingIndex })
                .ToListAsync();

            return allRows
                .Where(r => candidateSet.Contains((r.WordId, r.ReadingIndex)))
                .GroupBy(r => (r.WordId, r.ReadingIndex))
                .Where(g => g.Count() >= 2
                            && g.All(r => known.Contains((r.ComponentWordId, (byte)r.ComponentReadingIndex))))
                .Select(g => (WordId: g.Key.WordId, ReadingIndex: (byte)g.Key.ReadingIndex))
                .Where(p => !blocked.Contains(p))
                .OrderBy(p => p.WordId).ThenBy(p => p.ReadingIndex)
                .ToList();
        }

        return null;
    }

    private async Task<(HashSet<(int WordId, byte ReadingIndex)> Known, HashSet<(int WordId, byte ReadingIndex)> Blocked)>
        LoadKnownAndBlockedSets(string userId)
    {
        var threshold = TimeSpan.FromDays(21);
        var raw = await userContext.FsrsCards
            .Where(c => c.UserId == userId
                        && (c.State == FsrsState.Mastered
                            || c.State == FsrsState.Blacklisted
                            || (c.State == FsrsState.Review && c.LastReview != null)))
            .Select(c => new { c.WordId, c.ReadingIndex, c.State, c.Due, c.LastReview })
            .ToListAsync();

        var known = new HashSet<(int, byte)>();
        var blocked = new HashSet<(int, byte)>();

        foreach (var c in raw)
        {
            var pair = (c.WordId, c.ReadingIndex);
            if (c.State == FsrsState.Mastered)
            {
                known.Add(pair);
                blocked.Add(pair);
            }
            else if (c.State == FsrsState.Blacklisted)
            {
                blocked.Add(pair);
            }
            else if (c.LastReview != null && c.Due - c.LastReview.Value >= threshold)
            {
                known.Add(pair);
            }
        }

        return (known, blocked);
    }

    private async Task<List<MassActionCardDto>> HydrateInferredPairs(
        string userId, List<(int WordId, byte ReadingIndex)> pairs)
    {
        if (pairs.Count == 0) return new List<MassActionCardDto>();
        var wordIds = pairs.Select(p => p.WordId).Distinct().ToList();

        var formDict = await WordFormHelper.LoadWordForms(context, wordIds);
        var freqDict = await WordFormHelper.LoadWordFormFrequencies(context, wordIds);
        var words = await context.JMDictWords
            .AsNoTracking()
            .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
            .Where(w => wordIds.Contains(w.WordId))
            .ToDictionaryAsync(w => w.WordId);

        var cardStates = await userContext.FsrsCards
            .Where(c => c.UserId == userId && wordIds.Contains(c.WordId))
            .Select(c => new { c.WordId, c.ReadingIndex, c.State })
            .ToListAsync();
        var stateDict = cardStates
            .ToDictionary(c => (c.WordId, c.ReadingIndex), c => c.State);

        return pairs.Select(p =>
        {
            var form = formDict.GetValueOrDefault((p.WordId, (short)p.ReadingIndex));
            var freq = freqDict.GetValueOrDefault((p.WordId, (short)p.ReadingIndex));
            var mainDef = words.GetValueOrDefault(p.WordId)?.Definitions
                .FirstOrDefault()?.EnglishMeanings.FirstOrDefault();
            var state = stateDict.GetValueOrDefault((p.WordId, p.ReadingIndex), (FsrsState)0);
            return new MassActionCardDto
            {
                WordId = p.WordId,
                ReadingIndex = p.ReadingIndex,
                Reading = form?.RubyText ?? form?.Text ?? "",
                MainDefinition = mainDef,
                FrequencyRank = freq?.FrequencyRank ?? 0,
                State = state,
                Due = default,
                CreatedAt = default,
            };
        }).ToList();
    }

    private IQueryable<FsrsCard> BuildMassActionQuery(string userId, MassActionRequest request)
    {
        var query = userContext.FsrsCards.Where(c => c.UserId == userId);

        if (request.StateFilter is { Length: > 0 })
        {
            var states = request.StateFilter.Select(s => (FsrsState)s).ToList();
            query = query.Where(c => states.Contains(c.State));
        }

        if (request.DateType is "created" or "due")
        {
            var isCreated = request.DateType == "created";
            if (request.DateFrom.HasValue)
            {
                var from = DateTime.SpecifyKind(request.DateFrom.Value.Date, DateTimeKind.Utc);
                query = isCreated
                    ? query.Where(c => c.CreatedAt >= from)
                    : query.Where(c => c.Due >= from);
            }

            if (request.DateTo.HasValue)
            {
                var to = DateTime.SpecifyKind(request.DateTo.Value.Date.AddDays(1), DateTimeKind.Utc);
                query = isCreated
                    ? query.Where(c => c.CreatedAt < to)
                    : query.Where(c => c.Due < to);
            }
        }

        return query;
    }

    private static string? ValidateMassActionRequest(MassActionRequest request, bool previewOnly)
    {
        if (request.Action is not ("change-state" or "push-due" or "delete-cards" or "reset-schedule"))
            return $"Invalid action: {request.Action}";

        if (!previewOnly)
        {
            switch (request.Action)
            {
                case "change-state":
                    if (request.TargetState is not ((int)FsrsState.Learning or (int)FsrsState.Blacklisted
                        or (int)FsrsState.Mastered or (int)FsrsState.Suspended))
                        return "Invalid target state. Must be Learning, Blacklisted, Mastered, or Suspended.";
                    break;

                case "push-due":
                    if (request.PushDays is null or < -365 or > 365)
                        return "PushDays is required and must be between -365 and 365.";
                    if (request.StaggerBatchSize is < 1 or > 10000)
                        return "StaggerBatchSize must be between 1 and 10000.";
                    break;

                case "delete-cards" or "reset-schedule":
                    var hasFilter = (request.StateFilter is { Length: > 0 })
                                    || request.DateFrom.HasValue
                                    || request.DateTo.HasValue;
                    if (!hasFilter)
                        return "At least one filter is required for this action.";
                    break;
            }
        }

        return null;
    }

    private async Task<UserFsrsSettings?> LoadUserSettings(string userId)
    {
        return await userContext.UserFsrsSettings.AsNoTracking()
                                                 .FirstOrDefaultAsync(s => s.UserId == userId);
    }

    private static bool AreParametersDefault(double[] parameters)
    {
        if (parameters.Length != FsrsConstants.DefaultParameters.Length)
        {
            return false;
        }

        for (var i = 0; i < parameters.Length; i++)
        {
            if (Math.Abs(parameters[i] - FsrsConstants.DefaultParameters[i]) > 1e-6)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDesiredRetentionDefault(double desiredRetention)
    {
        return Math.Abs(desiredRetention - FsrsConstants.DefaultDesiredRetention) < 1e-6;
    }

    private static bool IsSettingsDefault(double[] parameters, double desiredRetention)
    {
        return AreParametersDefault(parameters) && IsDesiredRetentionDefault(desiredRetention);
    }

    private static bool IsDesiredRetentionValid(double desiredRetention)
    {
        return desiredRetention > 0 && desiredRetention < 1 && !double.IsNaN(desiredRetention) && !double.IsInfinity(desiredRetention);
    }

    private static double[] GetParameters(UserFsrsSettings? settings)
    {
        return TryGetStoredParameters(settings, out var parameters) ? parameters : FsrsConstants.DefaultParameters;
    }

    private static double GetDesiredRetention(UserFsrsSettings? settings)
    {
        if (settings?.DesiredRetention is double desiredRetention && IsDesiredRetentionValid(desiredRetention))
        {
            return desiredRetention;
        }

        return FsrsConstants.DefaultDesiredRetention;
    }

    private static bool TryGetStoredParameters(UserFsrsSettings? settings, out double[] parameters)
    {
        parameters = Array.Empty<double>();
        if (settings == null)
        {
            return false;
        }

        var stored = settings.GetParametersOnce();
        if (stored.Length != FsrsConstants.DefaultParameters.Length)
        {
            return false;
        }

        if (stored.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
        {
            return false;
        }

        parameters = stored;
        return true;
    }

    private static bool TryParseParametersCsv(string? csv, out double[] parameters, out string error)
    {
        parameters = Array.Empty<double>();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(csv))
        {
            error = "Parameters are required.";
            return false;
        }

        var parts = csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var expectedCount = FsrsConstants.DefaultParameters.Length;
        if (parts.Length != expectedCount)
        {
            error = $"Parameters must contain {expectedCount} comma-separated values.";
            return false;
        }

        var parsed = new double[expectedCount];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                double.IsNaN(value) || double.IsInfinity(value))
            {
                error = $"Invalid parameter at position {i + 1}.";
                return false;
            }

            parsed[i] = value;
        }

        parameters = parsed;
        return true;
    }

    private static string SerializeParametersCsv(double[] parameters)
    {
        return string.Join(", ", parameters.Select(value => value.ToString("0.####", CultureInfo.InvariantCulture)));
    }

    private static DateTime ComputeNextMidnightUtc(DateTime utcNow, string? timezone)
    {
        if (string.IsNullOrEmpty(timezone))
            return utcNow.Date.AddDays(1);
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            var localDay = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz).Date.AddDays(1);
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDay, DateTimeKind.Unspecified), tz);
        }
        catch (TimeZoneNotFoundException)
        {
            return utcNow.Date.AddDays(1);
        }
    }

    private static StudySettingsDto GetStudySettings(UserFsrsSettings? settings)
    {
        if (settings?.SettingsJson is { Length: > 2 } json)
        {
            try { return JsonSerializer.Deserialize<StudySettingsDto>(json) ?? new StudySettingsDto(); }
            catch (JsonException) { }
        }
        return new StudySettingsDto();
    }

    /// <summary>
    /// Builds a load balancer seeded from the user's currently-scheduled review load, so freshly fuzzed
    /// due dates settle on the least-busy day within their fuzz window. Returns null when load balancing
    /// is disabled, leaving the scheduler on plain random fuzz.
    /// </summary>
    private async Task<IFsrsLoadBalancer?> BuildLoadBalancer(string userId, bool enabled)
    {
        if (!enabled) return null;
        return await FsrsLoadBalancerSeeder.SeedAsync(userContext, userId);
    }

    /// <summary>
    /// Builds the Easy-Days weekday preference from the user's settings, or returns null when it is off or
    /// load balancing is disabled (Easy Days is a refinement of load balancing and needs it enabled).
    /// </summary>
    private static EasyDaysPolicy? BuildEasyDaysPolicy(StudySettingsDto studySettings)
    {
        if (!studySettings.LoadBalancing) return null;
        return EasyDaysPolicy.From(studySettings.EasyDays, ResolveOffsetHours(DateTime.UtcNow, studySettings.Timezone));
    }

    private static double ResolveOffsetHours(DateTime utcNow, string? timezone)
    {
        if (string.IsNullOrEmpty(timezone)) return 0;
        try { return TimeZoneInfo.FindSystemTimeZoneById(timezone).GetUtcOffset(utcNow).TotalHours; }
        catch (TimeZoneNotFoundException) { return 0; }
        catch (InvalidTimeZoneException) { return 0; }
    }


    [HttpPost("reader-study-decks")]
    [SwaggerOperation(Summary = "Get user's study decks for the reader extension",
                      Description = "Returns all the user's study decks with their type. The extension uses the type to "
                                    + "classify deck membership and to offer only static word-list decks for manual adding.")]
    public async Task<IResult> GetReaderStudyDecks()
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var decks = await userContext.UserStudyDecks
            .AsNoTracking()
            .Where(sd => sd.UserId == userId && sd.IsActive)
            .OrderBy(sd => sd.SortOrder)
            .Select(sd => new { sd.UserStudyDeckId, sd.Name, DeckType = (int)sd.DeckType })
            .ToListAsync();

        return Results.Ok(decks);
    }
}
