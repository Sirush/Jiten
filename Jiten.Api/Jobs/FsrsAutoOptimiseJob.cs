using System.Text.Json.Serialization;
using Hangfire;
using Jiten.Api.Helpers;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Jiten.Api.Jobs;

public class FsrsAutoOptimiseOptions
{
    public bool Enabled { get; set; }
    public int FirstFitMinimum { get; set; } = 1000;
    public double GrowthRatio { get; set; } = 0.25;
    public int GrowthFloor { get; set; } = 500;
    public int GrowthCap { get; set; } = 5000;
    public int MinDaysBetweenAttempts { get; set; } = 7;
    public int ActiveWithinDays { get; set; } = 14;
}

public record FsrsAutoOptimiseDecision(
    string UserId,
    [property: JsonConverter(typeof(CamelCaseEnumConverter<FsrsAutoOptimiseOutcome>))] FsrsAutoOptimiseOutcome Outcome,
    int ReviewTotal,
    double? CurrentLoss = null,
    double? NewLoss = null,
    bool Saved = false)
{
    public double? RelativeGain => CurrentLoss is > 0 && NewLoss is { } next ? (CurrentLoss.Value - next) / CurrentLoss.Value : null;
}

/// <summary>Refits active users' FSRS parameters as their history grows, keeping a fit unless it scores worse than the set in force; due dates never move.</summary>
public class FsrsAutoOptimiseJob(
    IDbContextFactory<UserDbContext> userContextFactory,
    SrsRecomputeJob recomputeJob,
    IStudySessionService sessionService,
    IOptions<FsrsAutoOptimiseOptions> options,
    ILogger<FsrsAutoOptimiseJob> logger)
{
    public readonly record struct Candidate(string UserId, int ReviewTotal, int BaselineReviewCount);

    [Queue("default")]
    [DisableConcurrentExecution(timeoutInSeconds: 7200)]
    public async Task Run()
    {
        if (!options.Value.Enabled)
            return;
        await Sweep(dryRun: false, limit: null);
    }

    [Queue("default")]
    [DisableConcurrentExecution(timeoutInSeconds: 7200)]
    public Task DryRun(int limit) => Sweep(dryRun: true, limit);

    public async Task<List<FsrsAutoOptimiseDecision>> Sweep(bool dryRun, int? limit, string? onlyUserId = null)
    {
        var candidates = await SelectCandidates(DateTime.UtcNow, limit, onlyUserId);
        var decisions = new List<FsrsAutoOptimiseDecision>(candidates.Count);

        foreach (var candidate in candidates)
        {
            FsrsAutoOptimiseDecision decision;
            try
            {
                decision = await OptimiseUser(candidate, dryRun);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "FSRS auto-optimise failed for user {UserId}", candidate.UserId);
                decision = new FsrsAutoOptimiseDecision(candidate.UserId, FsrsAutoOptimiseOutcome.Failed, candidate.ReviewTotal);
                if (!dryRun)
                    await RecordAttempt(candidate.UserId, FsrsAutoOptimiseOutcome.Failed, candidate.ReviewTotal);
            }

            logger.LogInformation(
                "FSRS auto-optimise {UserId}: {Outcome}, reviews {ReviewTotal}, loss {CurrentLoss} -> {NewLoss}, dry run {DryRun}",
                decision.UserId, decision.Outcome, decision.ReviewTotal, decision.CurrentLoss, decision.NewLoss, dryRun);
            decisions.Add(decision);
        }

        var gains = decisions.Where(d => d.Outcome == FsrsAutoOptimiseOutcome.Accepted && d.RelativeGain != null)
                             .Select(d => d.RelativeGain!.Value).Order().ToList();
        var byOutcome = decisions.GroupBy(d => d.Outcome).ToDictionary(g => g.Key.ToString(), g => g.Count());
        logger.LogInformation(
            "FSRS auto-optimise run: {Candidates} candidates, {Accepted} accepted, outcomes {Outcomes}, median relative loss gain {MedianGain}, dry run {DryRun}",
            candidates.Count, byOutcome.GetValueOrDefault(nameof(FsrsAutoOptimiseOutcome.Accepted)), byOutcome,
            gains.Count > 0 ? gains[gains.Count / 2] : null, dryRun);

        return decisions;
    }

    /// <summary>Active users due a refit, from the daily rollup and settings rows only; first fits first, then by growth.</summary>
    public async Task<List<Candidate>> SelectCandidates(DateTime utcNow, int? limit, string? onlyUserId = null)
    {
        var o = options.Value;
        await using var userContext = await userContextFactory.CreateDbContextAsync();

        var activeSince = DateOnly.FromDateTime(utcNow.AddDays(-o.ActiveWithinDays));
        var activeQuery = userContext.UserReviewDailies.Where(d => d.LocalDate >= activeSince && d.ReviewCount > 0);
        if (onlyUserId != null)
            activeQuery = activeQuery.Where(d => d.UserId == onlyUserId);
        var active = await activeQuery.Select(d => d.UserId).Distinct().ToListAsync();

        var totals = await userContext.UserReviewDailies
                                      .Where(d => active.Contains(d.UserId))
                                      .GroupBy(d => d.UserId)
                                      .Select(g => new { UserId = g.Key, Total = g.Sum(d => d.ReviewCount) })
                                      .ToListAsync();
        var settings = await userContext.UserFsrsSettings
                                        .AsNoTracking()
                                        .Where(s => active.Contains(s.UserId))
                                        .Select(s => new
                                        {
                                            s.UserId, s.ParametersSource, s.AutoOptimise,
                                            s.AutoOptimiseAttemptedAtUtc, s.AutoOptimiseAttemptedReviewCount
                                        })
                                        .ToDictionaryAsync(s => s.UserId);

        var due = new List<Candidate>();
        foreach (var total in totals)
        {
            var row = settings.GetValueOrDefault(total.UserId);
            var source = row?.ParametersSource ?? FsrsParametersSource.Default;
            var baseline = row?.AutoOptimiseAttemptedReviewCount ?? 0;
            if (IsDue(o, source, row?.AutoOptimise ?? true, row?.AutoOptimiseAttemptedAtUtc, baseline, total.Total, utcNow))
                due.Add(new Candidate(total.UserId, total.Total, baseline));
        }

        var ordered = due.OrderBy(c => c.BaselineReviewCount > 0)
                         .ThenByDescending(c => c.BaselineReviewCount > 0 ? (double)(c.ReviewTotal - c.BaselineReviewCount) / c.BaselineReviewCount : c.ReviewTotal)
                         .ThenBy(c => c.UserId, StringComparer.Ordinal);
        return (limit is { } max ? ordered.Take(max) : ordered).ToList();
    }

    /// <param name="baselineReviewCount">Lifetime total at the last attempt or manual change; 0 when there was none.</param>
    public static bool IsDue(FsrsAutoOptimiseOptions o, FsrsParametersSource source, bool autoOptimise, DateTime? attemptedAtUtc,
                             int baselineReviewCount, int reviewTotal, DateTime utcNow)
    {
        if (!autoOptimise || source == FsrsParametersSource.Edited || reviewTotal < o.FirstFitMinimum)
            return false;
        if (attemptedAtUtc is { } attemptedAt && utcNow - attemptedAt < TimeSpan.FromDays(o.MinDaysBetweenAttempts))
            return false;
        if (baselineReviewCount <= 0)
            return true;

        var growthNeeded = Math.Max(o.GrowthFloor, Math.Min((int)Math.Ceiling(baselineReviewCount * o.GrowthRatio), o.GrowthCap));
        return reviewTotal - baselineReviewCount >= growthNeeded;
    }

    private readonly record struct Evaluation(
        FsrsAutoOptimiseOutcome Outcome,
        double? CurrentLoss = null,
        double? NewLoss = null,
        double[]? Fitted = null);

    private async Task<FsrsAutoOptimiseDecision> OptimiseUser(Candidate candidate, bool dryRun)
    {
        var userId = candidate.UserId;
        var (snapshot, e) = await Evaluate(userId);
        var decision = new FsrsAutoOptimiseDecision(userId, e.Outcome, candidate.ReviewTotal, e.CurrentLoss, e.NewLoss);
        if (dryRun)
            return decision;

        if (e.Fitted == null)
        {
            await RecordAttempt(userId, e.Outcome, candidate.ReviewTotal);
            return decision;
        }

        if (!await SaveFit(userId, snapshot, e.Fitted, candidate.ReviewTotal, e.CurrentLoss, e.NewLoss))
            return decision with { Outcome = FsrsAutoOptimiseOutcome.Conflict };

        await recomputeJob.RecomputeMemoryStates(userId);
        await sessionService.BumpStudyOverviewVersion(userId);
        return decision with { Saved = true };
    }

    /// <summary>Fits on the whole history and scores both sets on it; <see cref="Evaluation.Fitted"/> is set unless the fit scores worse.</summary>
    private async Task<(FsrsParameterProvenance.Snapshot Snapshot, Evaluation Result)> Evaluate(string userId)
    {
        await using var userContext = await userContextFactory.CreateDbContextAsync();
        var settings = await FsrsSettingsHelper.LoadAsync(userContext, userId);
        var snapshot = FsrsParameterProvenance.Capture(settings);
        var (currentParameters, version) = FsrsSettingsHelper.ResolveParameters(settings);

        var reviews = await CardArchiveService.LoadAllReviewsAsync(userContext, userId);
        if (FsrsOptimisationService.ComputeHealth(reviews).LikelyHardAsFail)
            return (snapshot, new Evaluation(FsrsAutoOptimiseOutcome.HardAsFail));

        var items = FsrsOptimisationService.BuildTrainingItems(reviews);
        if (reviews.Count < FsrsOptimizer.MinimumReviews || items.Count == 0)
            return (snapshot, new Evaluation(FsrsAutoOptimiseOutcome.NotEnoughData));

        var fitted = FsrsOptimisationService.Fit(version, items).Parameters;
        var currentLoss = FsrsOptimisationService.EvaluateLoss(version, items, currentParameters);
        var newLoss = FsrsOptimisationService.EvaluateLoss(version, items, fitted);
        if (!(currentLoss > 0) || !double.IsFinite(newLoss))
            return (snapshot, new Evaluation(FsrsAutoOptimiseOutcome.NotEnoughData));
        if (newLoss > currentLoss)
            return (snapshot, new Evaluation(FsrsAutoOptimiseOutcome.NoGain, currentLoss, newLoss));

        return (snapshot, new Evaluation(FsrsAutoOptimiseOutcome.Accepted, currentLoss, newLoss, fitted));
    }

    /// <summary>Saves the fit unless the user changed their parameters or opted out while it ran.</summary>
    private async Task<bool> SaveFit(string userId, FsrsParameterProvenance.Snapshot snapshot, double[] fitted, int reviewTotal,
                                     double? currentLoss, double? newLoss)
    {
        await using var userContext = await userContextFactory.CreateDbContextAsync();
        var settings = await userContext.UserFsrsSettings.FirstOrDefaultAsync(s => s.UserId == userId);
        if (FsrsParameterProvenance.Capture(settings) != snapshot || settings?.AutoOptimise == false)
            return false;

        if (settings == null)
        {
            settings = new UserFsrsSettings { UserId = userId };
            userContext.UserFsrsSettings.Add(settings);
        }

        var now = DateTime.UtcNow;
        settings.Parameters = fitted;
        FsrsParameterProvenance.SetSource(settings, FsrsParametersSource.Auto, reviewTotal, now);
        FsrsParameterProvenance.StampAttempt(settings, now, reviewTotal, FsrsAutoOptimiseOutcome.Accepted);
        await FsrsParameterProvenance.RecordAsync(userContext, settings, snapshot, reviewTotal, now, newLoss, currentLoss);
        await userContext.SaveChangesAsync();
        return true;
    }

    private async Task RecordAttempt(string userId, FsrsAutoOptimiseOutcome outcome, int reviewTotal)
    {
        await using var userContext = await userContextFactory.CreateDbContextAsync();
        var settings = await userContext.UserFsrsSettings.FirstOrDefaultAsync(s => s.UserId == userId);
        if (settings == null)
        {
            settings = new UserFsrsSettings { UserId = userId };
            userContext.UserFsrsSettings.Add(settings);
        }

        FsrsParameterProvenance.StampAttempt(settings, DateTime.UtcNow, reviewTotal, outcome);
        await userContext.SaveChangesAsync();
    }
}
