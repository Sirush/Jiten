using System.Threading.RateLimiting;
using Hangfire;
using Jiten.Api.Authorization;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.Billing;
using Jiten.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

/// <summary>Immersion plans (Jiten+). Create/edit is Trial+; read/delete survive a lapsed subscription.</summary>
[ApiController]
[Route("api/roadmaps")]
[Produces("application/json")]
[Authorize]
public class RoadmapController(
    UserDbContext userContext,
    IDbContextFactory<JitenDbContext> jitenFactory,
    ICurrentUserService currentUserService,
    IRoadmapDataLoader loader,
    IUserLimitsService userLimits,
    IBackgroundJobClient backgroundJobs,
    IHostEnvironment environment,
    ILogger<RoadmapController> logger) : ControllerBase
{
    private const int MAX_NAME_LENGTH = 100;

    private static readonly PartitionedRateLimiter<string> GenerationLimiter =
        PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<string, string>(userId =>
                RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                })),
            PartitionedRateLimiter.Create<string, string>(userId =>
                RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 25, Window = TimeSpan.FromMinutes(10), QueueLimit = 0, AutoReplenishment = true
                })));

    private static readonly PartitionedRateLimiter<string> PreviewLimiter =
        PartitionedRateLimiter.Create<string, string>(userId =>
            RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 40, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
            }));

    private IResult? CheckPreviewBudget(string userId)
    {
        if (environment.IsEnvironment("Testing"))
            return null;

        using var lease = PreviewLimiter.AttemptAcquire(userId);
        return lease.IsAcquired
            ? null
            : Results.Json(new { error = "Too many previews. Give it a moment." },
                           statusCode: StatusCodes.Status429TooManyRequests);
    }

    private IResult? CheckGenerationBudget(string userId)
    {
        if (environment.IsEnvironment("Testing"))
            return null;

        using var lease = GenerationLimiter.AttemptAcquire(userId);
        if (lease.IsAcquired)
            return null;

        return Results.Json(
            new { error = "You're building plans faster than we can generate them. Give it a moment, then try again." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    public record DefinitionDto(
        List<int>? MediaTypes,
        List<int>? GenresInclude, List<int>? GenresExclude,
        List<int>? TagsInclude, List<int>? TagsExclude,
        int? YearFrom, int? YearTo,
        double? ShowsDifficultyMin, double? ShowsDifficultyMax,
        double? NovelsDifficultyMin, double? NovelsDifficultyMax,
        double? ComprehensionFloor,
        double? ComfortTarget,
        double? GoalComprehensionTarget,
        bool? IncludeLearningWords,
        int? AcquisitionThreshold,
        int? Steps,
        int? GoalSteps,
        string? Preference,
        string? CandidateMode,
        double? ContentSimilarity,
        bool? IncludeAdultOnly,
        bool? AdultOnlyExclusive);

    public record CreateRequest(string? Name, string? Mode, int? GoalDeckId, DefinitionDto? Definition);

    // ---- Defaults -----------------------------------------------------------

    /// <summary>Suggests builder defaults (difficulty bands, media types) from the user's completed media.</summary>
    [HttpGet("defaults")]
    [JitenPlus]
    public async Task<IResult> GetDefaults()
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var (showsMin, showsMax, novelsMin, novelsMax) = await loader.SuggestDifficultyBandsAsync(userId);
        var limits = await userLimits.GetLimitsAsync(userId);

        return Results.Ok(new
        {
            showsDifficultyMin = showsMin,
            showsDifficultyMax = showsMax,
            novelsDifficultyMin = novelsMin,
            novelsDifficultyMax = novelsMax,
            hasBands = showsMin.HasValue || novelsMin.HasValue,
            maxRoadmaps = limits.Roadmaps
        });
    }

    // ---- Preview ------------------------------------------------------------

    /// <summary>Estimates candidate counts for a definition without generating it.</summary>
    [HttpPost("preview")]
    [JitenPlus]
    public async Task<IResult> Preview([FromBody] DefinitionDto? definition, [FromQuery] int? goalDeckId = null)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        if (CheckPreviewBudget(userId) is { } limited)
            return limited;

        var preview = await loader.PreviewAsync(userId, ToDefinition(definition), RoadmapJob.MaxCandidates, goalDeckId);

        return Results.Ok(new
        {
            matchingFilters = preview.MatchingFilters,
            candidates = preview.Candidates,
            aboveFloor = preview.AboveFloor,
            aboveComfort = preview.AboveComfort,
            hasCoverageData = preview.HasCoverageData,
            goalCoverage = preview.GoalCoverage
        });
    }

    // ---- Create -------------------------------------------------------------

    [HttpPost]
    [JitenPlus]
    public async Task<IResult> Create([FromBody] CreateRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var name = SanitizeName(request.Name);
        if (string.IsNullOrEmpty(name))
            return Results.BadRequest(new { error = "Please give the plan a name." });

        var mode = ParseMode(request.Mode);

        if (mode == RoadmapMode.Goal)
        {
            if (!request.GoalDeckId.HasValue)
                return Results.BadRequest(new { error = "Goal mode needs a target title." });

            await using var jiten = await jitenFactory.CreateDbContextAsync();
            var goalExists = await jiten.Decks.AnyAsync(d => d.DeckId == request.GoalDeckId.Value && d.ParentDeckId == null);
            if (!goalExists)
                return Results.BadRequest(new { error = "That target title doesn't exist, or isn't a top-level entry." });
        }

        var definition = ToDefinition(request.Definition);

        var maxRoadmaps = (await userLimits.GetLimitsAsync(userId)).Roadmaps;
        var roadmapCount = await userContext.UserRoadmaps.CountAsync(r => r.UserId == userId);
        if (roadmapCount >= maxRoadmaps)
            return Results.BadRequest(new
            {
                error = $"You can have at most {maxRoadmaps} plans. Delete one to make room."
            });

        if (CheckGenerationBudget(userId) is { } limited)
            return limited;

        var roadmap = new UserRoadmap
        {
            UserId = userId,
            Name = name,
            Mode = mode,
            GoalDeckId = mode == RoadmapMode.Goal ? request.GoalDeckId : null,
            Definition = definition,
            Status = RoadmapStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        userContext.UserRoadmaps.Add(roadmap);
        await userContext.SaveChangesAsync();

        backgroundJobs.Enqueue<RoadmapJob>(j => j.Generate(roadmap.Id));

        logger.LogInformation("Roadmap: user {UserId} created roadmap {RoadmapId} ({Mode}, goal={Goal})",
                              userId, roadmap.Id, mode, roadmap.GoalDeckId);

        return Results.Ok(ToDto(roadmap, includePayload: false));
    }

    // ---- List / read --------------------------------------------------------

    [HttpGet]
    public async Task<IResult> GetRoadmaps()
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        // Projects out the multi-megabyte StepsJson; this endpoint is polled while a roadmap generates.
        var rows = await userContext.UserRoadmaps.AsNoTracking()
                                    .Where(r => r.UserId == userId)
                                    .OrderByDescending(r => r.CreatedAt)
                                    .Select(r => new
                                    {
                                        r.Id, r.Name, r.Mode, r.GoalDeckId, r.Status, r.FailureReason,
                                        r.StepCount, r.CandidateCount, r.CreatedAt, r.GeneratedAt, r.DefinitionJson
                                    })
                                    .ToListAsync();

        return Results.Ok(rows.Select(r => ToDto(new UserRoadmap
        {
            Id = r.Id, Name = r.Name, Mode = r.Mode, GoalDeckId = r.GoalDeckId, Status = r.Status,
            FailureReason = r.FailureReason, StepCount = r.StepCount, CandidateCount = r.CandidateCount,
            CreatedAt = r.CreatedAt, GeneratedAt = r.GeneratedAt, DefinitionJson = r.DefinitionJson
        }, includePayload: false)));
    }

    [HttpGet("{id:long}")]
    public async Task<IResult> GetRoadmap(long id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var roadmap = await userContext.UserRoadmaps.AsNoTracking()
                                       .FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);
        if (roadmap is null)
            return Results.NotFound();

        var payload = roadmap.Payload;
        await FillMissingLengthsAsync(payload);

        return Results.Ok(ToDto(roadmap, includePayload: true, payload));
    }

    private async Task FillMissingLengthsAsync(RoadmapPayload payload)
    {
        var missing = payload.Steps.Where(s => s.CharacterCount == 0 && s.SpeechDuration == 0)
                             .Select(s => s.DeckId)
                             .Distinct()
                             .ToList();
        if (missing.Count == 0)
            return;

        await using var jiten = await jitenFactory.CreateDbContextAsync();
        var lengths = await jiten.Decks.AsNoTracking()
                                 .Where(d => missing.Contains(d.DeckId))
                                 .Select(d => new { d.DeckId, d.CharacterCount, d.SpeechDuration })
                                 .ToDictionaryAsync(d => d.DeckId);

        foreach (var step in payload.Steps)
        {
            if (!lengths.TryGetValue(step.DeckId, out var length))
                continue;
            step.CharacterCount = length.CharacterCount;
            step.SpeechDuration = length.SpeechDuration;
        }
    }

    [HttpGet("{id:long}/steps/{index:int}/words")]
    public async Task<IResult> GetStepWords(long id, int index)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var roadmap = await userContext.UserRoadmaps.AsNoTracking()
                                       .FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);
        if (roadmap is null)
            return Results.NotFound();

        var step = roadmap.Payload.Steps.FirstOrDefault(s => s.Index == index);
        if (step is null)
            return Results.NotFound();

        var keys = step.Words;

        var texts = await loader.LoadWordTextsAsync(keys);
        var ranks = await loader.LoadFrequencyRanksAsync(keys);

        // Stored order is already frequency-sorted; preserve it.
        var words = keys.Select(key =>
        {
            texts.TryGetValue(key, out var text);
            return new RoadmapWordDto
            {
                WordId = RoadmapEngine.UnpackWordId(key),
                ReadingIndex = RoadmapEngine.UnpackReadingIndex(key),
                Text = text.Text ?? string.Empty,
                Reading = text.Reading ?? string.Empty,
                FrequencyRank = ranks.GetValueOrDefault(key, 0)
            };
        }).ToList();

        return Results.Ok(new { words });
    }

    // ---- Edit ---------------------------------------------------------------

    /// <summary>Replaces a roadmap's settings and regenerates it in place.</summary>
    [HttpPut("{id:long}")]
    [JitenPlus]
    public async Task<IResult> Update(long id, [FromBody] CreateRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var roadmap = await userContext.UserRoadmaps.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);
        if (roadmap is null)
            return Results.NotFound();

        if (InFlight(roadmap) is { } busy)
            return busy;

        var name = SanitizeName(request.Name);
        if (string.IsNullOrEmpty(name))
            return Results.BadRequest(new { error = "Please give the plan a name." });

        var mode = ParseMode(request.Mode);

        if (mode == RoadmapMode.Goal)
        {
            if (!request.GoalDeckId.HasValue)
                return Results.BadRequest(new { error = "Goal mode needs a target title." });

            await using var jiten = await jitenFactory.CreateDbContextAsync();
            var goalExists = await jiten.Decks.AnyAsync(d => d.DeckId == request.GoalDeckId.Value && d.ParentDeckId == null);
            if (!goalExists)
                return Results.BadRequest(new { error = "That target title doesn't exist, or isn't a top-level entry." });
        }

        if (CheckGenerationBudget(userId) is { } limited)
            return limited;

        var definition = ToDefinition(request.Definition);
        definition.PinnedDeckIds = new List<int>();
        definition.ExcludedDeckIds = new List<int>();

        roadmap.Name = name;
        roadmap.Mode = mode;
        roadmap.GoalDeckId = mode == RoadmapMode.Goal ? request.GoalDeckId : null;
        roadmap.Definition = definition;

        logger.LogInformation("Roadmap: user {UserId} edited roadmap {RoadmapId} ({Mode}, goal={Goal})",
                              userId, roadmap.Id, mode, roadmap.GoalDeckId);

        return await QueueGenerationAsync(roadmap);
    }

    // ---- Regenerate ---------------------------------------------------------

    [HttpPost("{id:long}/regenerate")]
    [JitenPlus]
    public async Task<IResult> Regenerate(long id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var roadmap = await userContext.UserRoadmaps.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);
        if (roadmap is null)
            return Results.NotFound();

        if (InFlight(roadmap) is { } busy)
            return busy;

        if (CheckGenerationBudget(userId) is { } limited)
            return limited;

        return await QueueGenerationAsync(roadmap);
    }

    /// <summary>Rejects while a run is in flight; a duplicate enqueue would silently land stale results.</summary>
    private static IResult? InFlight(UserRoadmap roadmap)
    {
        if (roadmap.Status is RoadmapStatus.Pending or RoadmapStatus.Generating)
            return Results.BadRequest(new { error = "This plan is still generating. Wait for it to finish first." });
        return null;
    }

    // ---- Swap a step --------------------------------------------------------

    /// <summary>Permanently excludes the deck at <paramref name="index"/>, pins earlier steps, and regenerates the rest.</summary>
    [HttpPost("{id:long}/steps/{index:int}/swap")]
    [JitenPlus]
    public async Task<IResult> SwapStep(long id, int index)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var roadmap = await userContext.UserRoadmaps.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);
        if (roadmap is null)
            return Results.NotFound();

        if (roadmap.Status != RoadmapStatus.Ready)
            return Results.BadRequest(new { error = "This plan isn't ready yet." });

        var payload = roadmap.Payload;
        var target = payload.Steps.FirstOrDefault(s => s.Index == index);
        if (target is null)
            return Results.BadRequest(new { error = "That step isn't part of this plan." });

        if (CheckGenerationBudget(userId) is { } limited)
            return limited;

        var definition = roadmap.Definition;

        if (!definition.ExcludedDeckIds.Contains(target.DeckId))
            definition.ExcludedDeckIds.Add(target.DeckId);

        definition.PinnedDeckIds = payload.Steps
                                          .Where(s => s.Index < index)
                                          .OrderBy(s => s.Index)
                                          .Select(s => s.DeckId)
                                          .ToList();

        roadmap.Definition = definition;

        logger.LogInformation("Roadmap: user {UserId} swapped step {Index} (deck {DeckId}) on roadmap {RoadmapId}",
                              userId, index, target.DeckId, roadmap.Id);

        return await QueueGenerationAsync(roadmap);
    }

    /// <summary>Clears swap history and rebuilds from the original settings.</summary>
    [HttpPost("{id:long}/reset-swaps")]
    [JitenPlus]
    public async Task<IResult> ResetSwaps(long id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var roadmap = await userContext.UserRoadmaps.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);
        if (roadmap is null)
            return Results.NotFound();

        if (InFlight(roadmap) is { } busy)
            return busy;

        if (CheckGenerationBudget(userId) is { } limited)
            return limited;

        var definition = roadmap.Definition;
        definition.ExcludedDeckIds = new List<int>();
        definition.PinnedDeckIds = new List<int>();
        roadmap.Definition = definition;

        return await QueueGenerationAsync(roadmap);
    }

    // ---- Delete -------------------------------------------------------------

    [HttpDelete("{id:long}")]
    public async Task<IResult> Delete(long id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var roadmap = await userContext.UserRoadmaps.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);
        if (roadmap is null)
            return Results.NotFound();

        userContext.UserRoadmaps.Remove(roadmap);
        await userContext.SaveChangesAsync();

        return Results.Ok(new { deleted = true });
    }

    /// <summary>Resets to Pending and enqueues a fresh generation.</summary>
    private async Task<IResult> QueueGenerationAsync(UserRoadmap roadmap)
    {
        roadmap.Status = RoadmapStatus.Pending;
        roadmap.FailureReason = null;
        await userContext.SaveChangesAsync();

        backgroundJobs.Enqueue<RoadmapJob>(j => j.Generate(roadmap.Id));

        return Results.Ok(ToDto(roadmap, includePayload: false));
    }

    // ---- Mapping ------------------------------------------------------------

    private static object ToDto(UserRoadmap roadmap, bool includePayload, RoadmapPayload? payload = null)
    {
        var definition = roadmap.Definition;

        return new
        {
            id = roadmap.Id,
            name = roadmap.Name,
            mode = roadmap.Mode.ToString().ToLowerInvariant(),
            goalDeckId = roadmap.GoalDeckId,
            status = roadmap.Status.ToString().ToLowerInvariant(),
            failureReason = roadmap.FailureReason,
            stepCount = roadmap.StepCount,
            candidateCount = roadmap.CandidateCount,
            createdAt = roadmap.CreatedAt,
            generatedAt = roadmap.GeneratedAt,
            definition = new
            {
                mediaTypes = definition.MediaTypes,
                genresInclude = definition.GenresInclude,
                genresExclude = definition.GenresExclude,
                tagsInclude = definition.TagsInclude,
                tagsExclude = definition.TagsExclude,
                yearFrom = definition.YearFrom,
                yearTo = definition.YearTo,
                showsDifficultyMin = definition.ShowsDifficultyMin,
                showsDifficultyMax = definition.ShowsDifficultyMax,
                novelsDifficultyMin = definition.NovelsDifficultyMin,
                novelsDifficultyMax = definition.NovelsDifficultyMax,
                comprehensionFloor = definition.ComprehensionFloor,
                comfortTarget = definition.ComfortTarget,
                goalComprehensionTarget = definition.GoalComprehensionTarget,
                includeLearningWords = definition.IncludeLearningWords,
                acquisitionThreshold = definition.AcquisitionThreshold,
                steps = definition.Steps,
                goalSteps = definition.GoalSteps,
                preference = definition.Preference.ToString().ToLowerInvariant(),
                candidateMode = definition.CandidateMode.ToString().ToLowerInvariant(),
                contentSimilarity = definition.ContentSimilarity,
                includeAdultOnly = definition.IncludeAdultOnly,
                adultOnlyExclusive = definition.AdultOnlyExclusive,
                excludedDeckIds = definition.ExcludedDeckIds,
                pinnedDeckIds = definition.PinnedDeckIds
            },
            swappedCount = definition.ExcludedDeckIds.Count,
            payload = includePayload ? payload ?? roadmap.Payload : null
        };
    }

    private static string? SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var trimmed = name.Trim();
        return trimmed.Length > MAX_NAME_LENGTH ? trimmed[..MAX_NAME_LENGTH] : trimmed;
    }

    private static RoadmapMode ParseMode(string? mode) =>
        string.Equals(mode, "goal", StringComparison.OrdinalIgnoreCase) ? RoadmapMode.Goal : RoadmapMode.Discovery;

    private static RoadmapDefinition ToDefinition(DefinitionDto? dto)
    {
        var definition = new RoadmapDefinition();
        if (dto is null)
            return definition;

        definition.MediaTypes = dto.MediaTypes ?? new();
        definition.GenresInclude = dto.GenresInclude ?? new();
        definition.GenresExclude = dto.GenresExclude ?? new();
        definition.TagsInclude = dto.TagsInclude ?? new();
        definition.TagsExclude = dto.TagsExclude ?? new();
        definition.YearFrom = dto.YearFrom;
        definition.YearTo = dto.YearTo;

        definition.ShowsDifficultyMin = Clamp(dto.ShowsDifficultyMin, 0, 5);
        definition.ShowsDifficultyMax = Clamp(dto.ShowsDifficultyMax, 0, 5);
        definition.NovelsDifficultyMin = Clamp(dto.NovelsDifficultyMin, 0, 5);
        definition.NovelsDifficultyMax = Clamp(dto.NovelsDifficultyMax, 0, 5);

        definition.ComprehensionFloor = Math.Clamp(dto.ComprehensionFloor ?? 0.80, 0.50, 0.99);

        // Comfort target can never sit below the floor.
        definition.ComfortTarget = Math.Clamp(dto.ComfortTarget ?? 0.90, definition.ComprehensionFloor, 0.99);

        // Capped below 1.0: full coverage of a real title is unattainable.
        definition.GoalComprehensionTarget = Math.Clamp(dto.GoalComprehensionTarget ?? 0.95, 0.50, 0.99);

        definition.IncludeLearningWords = dto.IncludeLearningWords ?? true;
        definition.AcquisitionThreshold = Math.Clamp(dto.AcquisitionThreshold ?? 12, 1, 50);
        definition.Steps = Math.Clamp(dto.Steps ?? 5, RoadmapDefinition.MinSteps, RoadmapDefinition.MaxSteps);
        definition.GoalSteps = Math.Clamp(dto.GoalSteps ?? RoadmapDefinition.MaxGoalSteps,
                                          RoadmapDefinition.MinSteps, RoadmapDefinition.MaxGoalSteps);
        definition.ContentSimilarity = Math.Clamp(dto.ContentSimilarity ?? 0, -3, 3);

        definition.Preference = string.Equals(dto.Preference, "volume", StringComparison.OrdinalIgnoreCase)
            ? RoadmapPreference.Volume
            : RoadmapPreference.Efficiency;

        definition.CandidateMode = string.Equals(dto.CandidateMode, "catalogwide", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(dto.CandidateMode, "catalog-wide", StringComparison.OrdinalIgnoreCase)
            ? RoadmapCandidateMode.CatalogWide
            : RoadmapCandidateMode.Seeded;

        definition.IncludeAdultOnly = dto.IncludeAdultOnly ?? false;
        definition.AdultOnlyExclusive = definition.IncludeAdultOnly && (dto.AdultOnlyExclusive ?? false);

        return definition;
    }

    private static double? Clamp(double? value, double min, double max) =>
        value.HasValue ? Math.Clamp(value.Value, min, max) : null;
}
