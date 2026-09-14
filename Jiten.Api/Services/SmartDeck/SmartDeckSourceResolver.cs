 using Jiten.Core.Data;
using Jiten.Core.Services.SmartDeck;

namespace Jiten.Api.Services.SmartDeck;

public readonly record struct SmartDeckPreferenceRow(int DeckId, int? ParentDeckId, DeckStatus Status, DateTime UpdatedAt);

public sealed record SmartDeckTitle(
    int ParentDeckId,
    double Weight,
    bool Pinned,
    bool Planning,
    bool Boosted,
    bool ManuallyIncluded,
    DateTime LastActivity,
    DeckStatus Status);

public enum SmartDeckWindowSource
{
    /// <summary>No window: the title is unordered and nothing in it is marked Ongoing.</summary>
    None = 0,
    /// <summary>Units the user marked Ongoing.</summary>
    Ongoing = 1,
    /// <summary>The next units in DeckOrder after the last completed one.</summary>
    Sequence = 2,
}

public sealed record SmartDeckUnitWindow(
    int ParentDeckId,
    int? CursorDeckId,
    IReadOnlyList<int> WindowDeckIds,
    int CompletedUnits,
    int TotalUnits,
    SmartDeckWindowSource Source = SmartDeckWindowSource.Sequence,
    bool Sequential = true);

public static class SmartDeckSourceResolver
{
    public static List<SmartDeckTitle> ResolveTitles(SmartDeckSettings settings, IReadOnlyList<SmartDeckPreferenceRow> rows, DateTime now)
    {
        var excluded = settings.ExcludedDeckIds.ToHashSet();
        var included = settings.IncludedDeckIds.ToHashSet();
        var pinOrder = settings.PinnedDeckIds.Select((id, i) => (id, i)).ToDictionary(t => t.id, t => t.i);

        var parentRows = rows.Where(r => r.ParentDeckId == null).ToDictionary(r => r.DeckId);
        var childActivity = rows.Where(r => r.ParentDeckId != null)
                                .GroupBy(r => r.ParentDeckId!.Value)
                                .ToDictionary(g => g.Key, g => g.Max(r => r.UpdatedAt));

        var candidates = new List<(int ParentId, DeckStatus Status, DateTime Activity, bool Planning, bool Manual)>();

        foreach (var (parentId, row) in parentRows)
        {
            if (excluded.Contains(parentId)) continue;
            var activity = Max(row.UpdatedAt, childActivity.GetValueOrDefault(parentId, DateTime.MinValue));
            var manual = included.Contains(parentId) || pinOrder.ContainsKey(parentId);

            if (row.Status == DeckStatus.Ongoing || manual)
                candidates.Add((parentId, row.Status, activity, Planning: false, manual));
            else if (row.Status == DeckStatus.Planning && settings.WeighPlanning)
                candidates.Add((parentId, row.Status, activity, Planning: true, Manual: false));
        }

        foreach (var manualId in pinOrder.Keys.Concat(included))
        {
            if (excluded.Contains(manualId) || parentRows.ContainsKey(manualId)) continue;
            if (candidates.Any(c => c.ParentId == manualId)) continue;
            candidates.Add((manualId, DeckStatus.None, now, Planning: false, Manual: true));
        }

        var ordered = candidates
            .OrderBy(c => pinOrder.TryGetValue(c.ParentId, out var p) ? p : int.MaxValue)
            .ThenBy(c => c.Planning)
            .ThenByDescending(c => c.Activity)
            .ThenBy(c => c.ParentId)
            .Take(SmartDeckConstants.MaxTitles)
            .ToList();

        return ordered.Select((c, index) =>
        {
            var pinned = pinOrder.ContainsKey(c.ParentId);
            var ageDays = Math.Max(0, (now - c.Activity).TotalDays);
            return new SmartDeckTitle(
                c.ParentId,
                SmartDeckConstants.TitleWeight(pinned, c.Planning, ageDays, settings.RecencyHalfLifeDays),
                pinned,
                c.Planning,
                Boosted: index < SmartDeckConstants.BoostedTitles,
                ManuallyIncluded: c.Manual,
                c.Activity,
                c.Status);
        }).ToList();
    }

    public static SmartDeckUnitWindow ResolveWindow(
        int parentDeckId,
        IReadOnlyList<(int DeckId, int DeckOrder)> unitsInOrder,
        IReadOnlySet<int> completedUnitIds,
        int lookaheadUnits,
        IReadOnlySet<int>? ongoingUnitIds = null,
        bool sequential = true)
    {
        var sorted = unitsInOrder.OrderBy(u => u.DeckOrder).ThenBy(u => u.DeckId).ToList();

        var ongoing = sorted.Where(u => ongoingUnitIds?.Contains(u.DeckId) == true && !completedUnitIds.Contains(u.DeckId))
                            .Take(SmartDeckConstants.MaxOngoingUnits)
                            .Select(u => u.DeckId)
                            .ToList();
        var completedCount = sorted.Count(u => completedUnitIds.Contains(u.DeckId));
        if (ongoing.Count > 0)
        {
            if (sequential)
            {
                var lastOngoing = sorted.FindLastIndex(u => ongoing.Contains(u.DeckId));
                foreach (var u in sorted.Skip(lastOngoing + 1))
                {
                    if (ongoing.Count >= lookaheadUnits) break;
                    if (!completedUnitIds.Contains(u.DeckId)) ongoing.Add(u.DeckId);
                }
            }
            return new SmartDeckUnitWindow(parentDeckId, null, ongoing, completedCount, sorted.Count, SmartDeckWindowSource.Ongoing, sequential);
        }
        if (!sequential)
            return new SmartDeckUnitWindow(parentDeckId, null, [], completedCount, sorted.Count, SmartDeckWindowSource.None, false);

        var cursorIndex = -1;
        for (var i = 0; i < sorted.Count; i++)
            if (completedUnitIds.Contains(sorted[i].DeckId))
                cursorIndex = i;

        var window = sorted.Skip(cursorIndex + 1).Take(lookaheadUnits).Select(u => u.DeckId).ToList();
        return new SmartDeckUnitWindow(
            parentDeckId,
            cursorIndex >= 0 ? sorted[cursorIndex].DeckId : null,
            window,
            CompletedUnits: cursorIndex + 1,
            TotalUnits: sorted.Count,
            SmartDeckWindowSource.Sequence,
            Sequential: true);
    }

    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;
}
