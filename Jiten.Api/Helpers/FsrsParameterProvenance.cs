using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Helpers;

/// <summary>Keeps <see cref="UserFsrsSettings"/> provenance and <see cref="FsrsParameterHistory"/> in step with every parameter change.</summary>
public static class FsrsParameterProvenance
{
    private const int HistoryLimit = 10;

    public readonly record struct Snapshot(string ParametersJson, FsrsParametersSource Source);

    /// <summary>Taken before a change so the first history row can record the set being replaced.</summary>
    public static Snapshot Capture(UserFsrsSettings? settings)
        => new(settings?.ParametersJson ?? "[]", settings?.ParametersSource ?? FsrsParametersSource.Default);

    public static IQueryable<FsrsParameterHistory> Ordered(UserDbContext userContext, string userId)
        => userContext.FsrsParameterHistories.Where(h => h.UserId == userId).OrderBy(h => h.CreatedAtUtc).ThenBy(h => h.Id);

    /// <summary>Records a change the user made and restarts the nightly refit's growth and spacing clock. Does not save.</summary>
    public static async Task RecordManualChangeAsync(UserDbContext userContext, UserFsrsSettings settings, Snapshot previous, FsrsParametersSource source)
    {
        var now = DateTime.UtcNow;
        var reviewTotal = await FsrsOptimisationService.LifetimeReviewTotalAsync(userContext, settings.UserId);
        SetSource(settings, source, reviewTotal, now);
        StampAttempt(settings, now, reviewTotal, FsrsAutoOptimiseOutcome.None);
        await RecordAsync(userContext, settings, previous, reviewTotal, now);
    }

    /// <summary>Adds a history row for the settings' current parameters and trims old rows; the first change also records the set it replaces. Does not save.</summary>
    public static async Task RecordAsync(UserDbContext userContext, UserFsrsSettings settings, Snapshot previous,
                                         int reviewCount, DateTime utcNow, double? loss = null, double? previousLoss = null)
    {
        var existing = await Ordered(userContext, settings.UserId).ToListAsync();

        if (existing.Count == 0)
            userContext.FsrsParameterHistories.Add(Row(settings.UserId, previous.ParametersJson, previous.Source, 0, utcNow.AddMilliseconds(-1)));

        var row = Row(settings.UserId, settings.ParametersJson, settings.ParametersSource, reviewCount, utcNow);
        row.Loss = loss;
        row.PreviousLoss = previousLoss;
        userContext.FsrsParameterHistories.Add(row);

        var keep = HistoryLimit - (existing.Count == 0 ? 2 : 1);
        if (existing.Count > keep)
            userContext.FsrsParameterHistories.RemoveRange(existing.Take(existing.Count - keep));
    }

    /// <param name="at">Null clears the clock so the next sweep may refit without waiting for growth.</param>
    public static void StampAttempt(UserFsrsSettings settings, DateTime? at, int reviewTotal, FsrsAutoOptimiseOutcome outcome)
    {
        settings.AutoOptimiseAttemptedAtUtc = at;
        settings.AutoOptimiseAttemptedReviewCount = reviewTotal;
        settings.AutoOptimiseLastOutcome = outcome;
    }

    /// <summary>Only optimiser output carries a fit date and review count.</summary>
    public static void SetSource(UserFsrsSettings settings, FsrsParametersSource source, int fittedReviewCount, DateTime fittedAtUtc)
    {
        var fitted = source is FsrsParametersSource.Optimised or FsrsParametersSource.Auto;
        settings.ParametersSource = source;
        settings.ParametersFittedAtUtc = fitted ? fittedAtUtc : null;
        settings.ParametersFittedReviewCount = fitted ? fittedReviewCount : 0;
    }

    /// <param name="ordered">Rows in <see cref="Ordered"/> order.</param>
    /// <returns>The newest row not undone by a restore, which holds the parameters in force.</returns>
    public static FsrsParameterHistory? Current(IReadOnlyList<FsrsParameterHistory> ordered)
        => ordered.LastOrDefault(h => h.RevertedAtUtc == null);

    /// <param name="ordered">Rows in <see cref="Ordered"/> order.</param>
    /// <returns>The row an undo returns to: the newest live row before the current one.</returns>
    public static FsrsParameterHistory? UndoTarget(IReadOnlyList<FsrsParameterHistory> ordered)
        => ordered.Where(h => h.RevertedAtUtc == null).SkipLast(1).LastOrDefault();

    /// <summary>Makes <paramref name="target"/> current: every row after it is stamped as undone. Does not save.</summary>
    public static void MarkRestored(IReadOnlyList<FsrsParameterHistory> ordered, FsrsParameterHistory target, DateTime utcNow)
    {
        target.RevertedAtUtc = null;
        foreach (var row in ordered.SkipWhile(h => !ReferenceEquals(h, target)).Skip(1))
            row.RevertedAtUtc ??= utcNow;
    }

    private static FsrsParameterHistory Row(string userId, string parametersJson, FsrsParametersSource source, int reviewCount, DateTime utcNow)
        => new()
           {
               UserId = userId,
               ParametersJson = parametersJson,
               Version = (int)FsrsSettingsHelper.ResolveStoredJson(parametersJson).Version,
               Source = source,
               ReviewCount = reviewCount,
               CreatedAtUtc = utcNow
           };
}
