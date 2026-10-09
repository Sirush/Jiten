using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core.Services;

public sealed record FranchiseSyncSummary(int Created, int Renamed, int Merged, int Deleted, int Unchanged, int DecksUpdated);

/// <summary>Recomputes every franchise from story links (types 1-6), series membership and saved builder boards.</summary>
public static class FranchiseSync
{
    private const long SyncLockKey = 7_212_026_002;
    private const int UpdateChunkSize = 5000;

    /// <param name="repointStudyDecks">Called after commit with (deleted franchise id, franchise now holding most of its former decks).</param>
    public static async Task<FranchiseSyncSummary> RunAsync(JitenDbContext db, Func<int, int, Task>? repointStudyDecks = null,
                                                            CancellationToken ct = default)
    {
        var isNpgsql = db.Database.ProviderName?.Contains("Npgsql") == true;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Two concurrent runs would each create a row for the same new component.
        if (isNpgsql)
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0});", SyncLockKey);

        var links = await db.DeckRelationships.AsNoTracking()
                            .Where(DeckRelationship.IsStoryLink)
                            .Select(r => new { r.SourceDeckId, r.TargetDeckId })
                            .ToListAsync(ct);
        var series = await db.Series.AsNoTracking()
                             .Where(s => s.Kind == SeriesKind.Series)
                             .Select(s => new { s.SeriesId, s.OriginalTitle, s.RomajiTitle, s.EnglishTitle })
                             .ToDictionaryAsync(s => s.SeriesId, s => new GroupTitles(s.OriginalTitle, s.RomajiTitle, s.EnglishTitle), ct);
        var memberships = await db.SeriesMembers.AsNoTracking()
                                  .Where(m => m.Series.Kind == SeriesKind.Series)
                                  .Select(m => new { m.SeriesId, m.DeckId })
                                  .ToListAsync(ct);
        var boardMembers = await db.FranchiseMembers.AsNoTracking()
                                   .Select(m => new { m.DeckId, m.FranchiseId })
                                   .ToDictionaryAsync(m => m.DeckId, m => m.FranchiseId, ct);

        var uf = new UnionFind();
        foreach (var link in links)
            uf.Union(link.SourceDeckId, link.TargetDeckId);

        var seriesByDeck = new Dictionary<int, HashSet<int>>();
        foreach (var group in memberships.GroupBy(m => m.SeriesId))
        {
            var deckIds = group.Select(m => m.DeckId).Distinct().ToList();
            foreach (var deckId in deckIds)
            {
                uf.Union(deckIds[0], deckId);
                if (!seriesByDeck.TryGetValue(deckId, out var seriesIds))
                    seriesByDeck[deckId] = seriesIds = [];
                seriesIds.Add(group.Key);
            }
        }

        foreach (var board in boardMembers.GroupBy(m => m.Value, m => m.Key))
        {
            var first = board.First();
            foreach (var deckId in board)
                uf.Union(first, deckId);
        }

        var components = uf.Components()
                           .Where(c => c.Count >= 2)
                           .Select(c => c.Order().ToList())
                           .OrderBy(c => c[0])
                           .ToList();

        var current = await db.Decks.AsNoTracking()
                              .Where(d => d.FranchiseId != null)
                              .Select(d => new { d.DeckId, FranchiseId = d.FranchiseId!.Value })
                              .ToDictionaryAsync(d => d.DeckId, d => d.FranchiseId, ct);
        var rows = await db.Franchises.ToDictionaryAsync(f => f.FranchiseId, ct);

        var assigned = new int?[components.Count];
        var taken = new HashSet<int>();

        // A saved board keeps its franchise id; boards that links have since joined fold into the one holding the most decks.
        var boardMerges = new List<(int From, int To)>();
        for (var i = 0; i < components.Count; i++)
        {
            var boards = components[i].Where(boardMembers.ContainsKey)
                                      .GroupBy(d => boardMembers[d])
                                      .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                                      .Select(g => g.Key)
                                      .ToList();
            if (boards.Count == 0)
                continue;

            assigned[i] = boards[0];
            taken.Add(boards[0]);
            boardMerges.AddRange(boards.Skip(1).Select(id => (id, boards[0])));
        }

        var claims = components.SelectMany((decks, index) => decks.Where(current.ContainsKey)
                                                                  .GroupBy(d => current[d])
                                                                  .Where(g => rows.ContainsKey(g.Key))
                                                                  .Select(g => (Index: index, FranchiseId: g.Key, Count: g.Count())))
                               .OrderByDescending(c => c.Count).ThenBy(c => c.FranchiseId).ThenBy(c => c.Index);
        foreach (var claim in claims)
        {
            if (assigned[claim.Index] != null || !taken.Add(claim.FranchiseId))
                continue;
            assigned[claim.Index] = claim.FranchiseId;
        }

        var allComponentDecks = components.SelectMany(c => c).ToList();
        var titles = await db.Decks.AsNoTracking()
                             .Where(d => allComponentDecks.Contains(d.DeckId))
                             .Select(d => new { d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.ReleaseDate })
                             .ToDictionaryAsync(d => d.DeckId, ct);

        GroupTitles SuggestTitles(List<int> decks)
        {
            var members = decks.Where(titles.ContainsKey)
                               .Select(id => (new GroupTitles(titles[id].OriginalTitle, titles[id].RomajiTitle, titles[id].EnglishTitle),
                                              titles[id].ReleaseDate, id))
                               .ToList();
            var seriesTitles = decks.SelectMany(id => seriesByDeck.GetValueOrDefault(id) ?? [])
                                    .Distinct()
                                    .Order()
                                    .Select(id => series[id])
                                    .ToList();
            return FranchiseNaming.Suggest(members, seriesTitles);
        }

        var now = DateTime.UtcNow;
        var created = new Dictionary<int, Franchise>();
        for (var i = 0; i < components.Count; i++)
        {
            if (assigned[i] != null)
                continue;
            var row = new Franchise { CreatedAt = now, UpdatedAt = now };
            row.SetTitles(SuggestTitles(components[i]));
            db.Franchises.Add(row);
            created[i] = row;
        }

        await db.SaveChangesAsync(ct);
        foreach (var (index, row) in created)
            assigned[index] = row.FranchiseId;

        var target = new Dictionary<int, int>();
        for (var i = 0; i < components.Count; i++)
            foreach (var deckId in components[i])
                target[deckId] = assigned[i]!.Value;

        var moves = target.Where(t => current.GetValueOrDefault(t.Key) != t.Value)
                          .Select(t => (DeckId: t.Key, FranchiseId: (int?)t.Value))
                          .Concat(current.Keys.Where(id => !target.ContainsKey(id)).Select(id => (DeckId: id, FranchiseId: (int?)null)))
                          .ToList();

        if (isNpgsql)
        {
            foreach (var chunk in moves.Chunk(UpdateChunkSize))
            {
                await db.Database.ExecuteSqlRawAsync("""
                    UPDATE jiten."Decks" AS d SET "FranchiseId" = v.franchise_id
                    FROM unnest({0}::int[], {1}::int[]) AS v(deck_id, franchise_id)
                    WHERE d."DeckId" = v.deck_id
                    """, [chunk.Select(m => m.DeckId).ToArray(), chunk.Select(m => m.FranchiseId).ToArray()], ct);
            }
        }
        else
        {
            foreach (var group in moves.GroupBy(m => m.FranchiseId))
            {
                var ids = group.Select(m => m.DeckId).ToList();
                var franchiseId = group.Key;
                await db.Decks.Where(d => ids.Contains(d.DeckId))
                        .ExecuteUpdateAsync(s => s.SetProperty(d => d.FranchiseId, franchiseId), ct);
            }
        }

        var changedFranchises = moves.Select(m => m.FranchiseId)
                                     .Concat(moves.Select(m => current.TryGetValue(m.DeckId, out var old) ? (int?)old : null))
                                     .OfType<int>()
                                     .ToHashSet();

        int renamed = 0, unchanged = 0;
        for (var i = 0; i < components.Count; i++)
        {
            if (created.ContainsKey(i))
                continue;

            var row = rows[assigned[i]!.Value];
            var touched = changedFranchises.Contains(row.FranchiseId);
            if (!row.NameIsManual)
            {
                var suggested = SuggestTitles(components[i]);
                if (suggested != row.Titles())
                {
                    row.SetTitles(suggested);
                    renamed++;
                    touched = true;
                }
            }

            if (touched)
                row.UpdatedAt = now;
            else
                unchanged++;
        }

        foreach (var (from, to) in boardMerges)
            await db.FranchiseMembers.Where(m => m.FranchiseId == from)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.FranchiseId, to), ct);

        var repoints = new List<(int OldId, int NewId)>();
        var deleted = 0;
        foreach (var row in rows.Values.Where(r => !taken.Contains(r.FranchiseId)))
        {
            var successor = current.Where(c => c.Value == row.FranchiseId && target.ContainsKey(c.Key))
                                   .GroupBy(c => target[c.Key])
                                   .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                                   .Select(g => (int?)g.Key)
                                   .FirstOrDefault();
            if (successor is { } newId)
                repoints.Add((row.FranchiseId, newId));
            else
                deleted++;

            db.Franchises.Remove(row);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (repointStudyDecks != null)
            foreach (var (oldId, newId) in repoints)
                await repointStudyDecks(oldId, newId);

        return new FranchiseSyncSummary(created.Count, renamed, repoints.Count, deleted, unchanged, moves.Count);
    }

    /// <summary>As <see cref="RunAsync(JitenDbContext, Func{int, int, Task}?, CancellationToken)"/>, repointing study decks through a fresh user context each.</summary>
    public static Task<FranchiseSyncSummary> RunAsync(JitenDbContext db, Func<UserDbContext> createUserDb, CancellationToken ct = default) =>
        RunAsync(db, async (oldId, newId) =>
        {
            await using var userDb = createUserDb();
            await RepointStudyDecksAsync(userDb, MediaGroupKind.Franchise, oldId, newId, ct);
        }, ct);

    /// <summary>Study decks on a merged-away or deleted franchise or series follow it to the group that took its decks.</summary>
    public static Task<int> RepointStudyDecksAsync(UserDbContext userDb, MediaGroupKind kind, int oldId, int newId,
                                                   CancellationToken ct = default, MediaGroupKind? newKind = null)
    {
        MediaGroupKind? targetKind = newKind ?? kind;
        int? targetId = newId;
        return userDb.UserStudyDecks
                     .Where(sd => sd.DeckType == StudyDeckType.MediaGroup && sd.GroupKind == kind && sd.GroupId == oldId)
                     .ExecuteUpdateAsync(s => s.SetProperty(sd => sd.GroupKind, targetKind)
                                               .SetProperty(sd => sd.GroupId, targetId), ct);
    }
}
