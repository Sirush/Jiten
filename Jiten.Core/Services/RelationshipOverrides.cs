using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core.Services;

public static class RelationshipOverrides
{
    /// <summary>Records each pair as admin-curated without saving; the caller owns SaveChanges.</summary>
    public static async Task MarkAsync(JitenDbContext db, IEnumerable<(int A, int B)> pairs, CancellationToken ct = default)
    {
        var wanted = pairs.Select(p => DeckRelationshipOverride.Key(p.A, p.B)).Where(k => k.Low != k.High).Distinct().ToList();
        if (wanted.Count == 0)
            return;

        var lows = wanted.Select(k => k.Low).Distinct().ToList();
        var existing = (await db.DeckRelationshipOverrides.AsNoTracking()
                                .Where(o => lows.Contains(o.LowDeckId))
                                .Select(o => new { o.LowDeckId, o.HighDeckId })
                                .ToListAsync(ct))
                       .Select(o => (o.LowDeckId, o.HighDeckId))
                       .Concat(db.DeckRelationshipOverrides.Local.Select(o => (o.LowDeckId, o.HighDeckId)))
                       .ToHashSet();

        foreach (var (low, high) in wanted)
            if (existing.Add((low, high)))
                db.DeckRelationshipOverrides.Add(new DeckRelationshipOverride { LowDeckId = low, HighDeckId = high });
    }

    public static Task<bool> IsCuratedAsync(JitenDbContext db, int a, int b, CancellationToken ct = default)
    {
        var (low, high) = DeckRelationshipOverride.Key(a, b);
        return db.DeckRelationshipOverrides.AnyAsync(o => o.LowDeckId == low && o.HighDeckId == high, ct);
    }
}
