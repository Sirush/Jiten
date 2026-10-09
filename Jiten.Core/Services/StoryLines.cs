using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core.Services;

public static class StoryLines
{
    public static IQueryable<DeckRelationship> LinksAmong(IQueryable<DeckRelationship> relationships, IReadOnlyCollection<int> deckIds) =>
        relationships.Where(r => deckIds.Contains(r.SourceDeckId) && deckIds.Contains(r.TargetDeckId)).Where(DeckRelationship.IsStoryLink);

    /// <summary>Every deck lands in exactly one component, single decks included.</summary>
    public static List<List<int>> Components(IEnumerable<int> deckIds, IEnumerable<(int SourceDeckId, int TargetDeckId)> links)
    {
        var uf = new UnionFind();
        foreach (var deckId in deckIds)
            uf.Add(deckId);
        foreach (var (source, target) in links)
            uf.Union(source, target);
        return uf.Components().ToList();
    }

    /// <summary>The line holding each existing deck; unknown deck ids are left out.</summary>
    public static async Task<Dictionary<int, List<int>>> LinesOfAsync(JitenDbContext db, IReadOnlyCollection<int> deckIds, CancellationToken ct = default)
    {
        var franchiseOf = await db.Decks.AsNoTracking()
                                  .Where(d => deckIds.Contains(d.DeckId))
                                  .Select(d => new { d.DeckId, d.FranchiseId })
                                  .ToDictionaryAsync(d => d.DeckId, d => d.FranchiseId, ct);

        var franchiseIds = franchiseOf.Values.OfType<int>().Distinct().ToList();
        var members = franchiseIds.Count == 0
            ? new Dictionary<int, int>()
            : await db.Decks.AsNoTracking()
                      .Where(d => d.FranchiseId != null && franchiseIds.Contains(d.FranchiseId.Value))
                      .Select(d => new { d.DeckId, FranchiseId = d.FranchiseId!.Value })
                      .ToDictionaryAsync(d => d.DeckId, d => d.FranchiseId, ct);

        var memberIds = members.Keys.ToList();
        var links = await LinksAmong(db.DeckRelationships.AsNoTracking(), memberIds)
                          .Select(r => new { r.SourceDeckId, r.TargetDeckId })
                          .ToListAsync(ct);

        var lineOf = new Dictionary<int, List<int>>();
        var sameFranchiseLinks = links.Where(l => members[l.SourceDeckId] == members[l.TargetDeckId])
                                      .Select(l => (l.SourceDeckId, l.TargetDeckId));
        foreach (var component in Components(memberIds, sameFranchiseLinks))
            foreach (var deckId in component)
                lineOf[deckId] = component;

        return franchiseOf.Keys.ToDictionary(id => id, id => lineOf.GetValueOrDefault(id) ?? [id]);
    }
}
