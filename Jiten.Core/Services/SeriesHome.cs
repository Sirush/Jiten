using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core.Services;

public static class SeriesHome
{
    /// <summary>
    /// Franchise holding most of each series' members, lowest id on ties; absent for a series with no member in a franchise
    /// and for every setting, since a setting spans franchises without joining them.
    /// </summary>
    public static async Task<Dictionary<int, int>> FranchisesAsync(JitenDbContext db, IReadOnlyCollection<int> seriesIds,
                                                                   CancellationToken ct = default)
    {
        if (seriesIds.Count == 0)
            return [];

        var ids = seriesIds.ToList();
        var counts = await db.SeriesMembers.AsNoTracking()
                             .Where(m => ids.Contains(m.SeriesId) && m.Series.Kind == SeriesKind.Series && m.Deck.FranchiseId != null)
                             .GroupBy(m => new { m.SeriesId, FranchiseId = m.Deck.FranchiseId!.Value })
                             .Select(g => new { g.Key.SeriesId, g.Key.FranchiseId, Count = g.Count() })
                             .ToListAsync(ct);

        return counts.GroupBy(c => c.SeriesId)
                     .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Count).ThenBy(c => c.FranchiseId).First().FranchiseId);
    }

    public static async Task<int?> FranchiseAsync(JitenDbContext db, int seriesId, CancellationToken ct = default) =>
        (await FranchisesAsync(db, [seriesId], ct)).TryGetValue(seriesId, out var franchiseId) ? franchiseId : null;
}
