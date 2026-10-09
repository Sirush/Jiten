using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public sealed class FranchiseSuggestionService(JitenDbContext db, FranchiseService franchise)
{
    public const int MaxDismissDecks = 500;

    private sealed record DeckFacts(int DeckId, string OriginalTitle, string? EnglishTitle, int? FranchiseId, MediaType MediaType, DateOnly ReleaseDate);

    public async Task<PaginatedResponse<List<FranchiseSuggestionDto>>> ListAsync(string? query, MediaType? mediaType, FranchiseSuggestionScope scope,
                                                                                int offset, int limit, CancellationToken ct = default)
    {
        var decks = await db.Decks.AsNoTracking()
                            .Where(d => d.ParentDeckId == null && d.MediaType != MediaType.YouTube)
                            .Select(d => new DeckFacts(d.DeckId, d.OriginalTitle, d.EnglishTitle, d.FranchiseId, d.MediaType, d.ReleaseDate))
                            .ToDictionaryAsync(d => d.DeckId, ct);

        var pairs = FranchiseSuggestions.FindPairs(decks.Values.Select(d => new SuggestionDeck(d.DeckId, d.OriginalTitle, d.EnglishTitle, d.FranchiseId)));
        var commonWords = await CommonWordsAsync(pairs.Select(p => p.Key).Distinct().ToList(), ct);
        var groups = FranchiseSuggestions.Group(pairs.Where(p => !commonWords.Contains(p.Key)).ToList(),
                                                decks.ToDictionary(d => d.Key, d => d.Value.FranchiseId));

        var dismissed = (await db.FranchiseSuggestionDismissals.AsNoTracking()
                                 .Select(x => new { x.DeckId, x.RootKey })
                                 .ToListAsync(ct))
                        .Select(x => (x.DeckId, x.RootKey))
                        .ToHashSet();

        bool IsIsolated(int deckId) => decks[deckId].FranchiseId == null;

        var needle = FranchiseSuggestions.Normalise(query)?.ToLowerInvariant();
        var visible = groups.Where(g => g.DeckIds.Where(IsIsolated).Any(id => !dismissed.Contains((id, g.Key))))
                            .Where(g => mediaType == null || g.DeckIds.Any(id => IsIsolated(id) && decks[id].MediaType == mediaType))
                            .Where(g => scope switch
                            {
                                FranchiseSuggestionScope.Franchise => g.DeckIds.Any(id => !IsIsolated(id)),
                                FranchiseSuggestionScope.Unlinked => g.DeckIds.All(IsIsolated),
                                _ => true
                            })
                            .Where(g => string.IsNullOrEmpty(needle) || g.Key.Contains(needle) ||
                                        g.DeckIds.Any(id => decks[id].OriginalTitle.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                                                            decks[id].EnglishTitle?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true))
                            .OrderByDescending(g => g.DeckIds.Count(IsIsolated))
                            .ThenBy(g => g.Root, new FranchiseNameComparer())
                            .ToList();

        var page = visible.Skip(offset).Take(limit).ToList();
        var pageDeckIds = page.SelectMany(g => g.DeckIds).Distinct().ToList();

        var nodes = (await franchise.LoadNodesAsync(pageDeckIds, ct)).ToDictionary(n => n.DeckId);
        var franchiseIds = pageDeckIds.Select(id => decks[id].FranchiseId).OfType<int>().Distinct().ToList();
        var franchiseNames = await db.Franchises.AsNoTracking()
                                     .Where(f => franchiseIds.Contains(f.FranchiseId))
                                     .ToDictionaryAsync(f => f.FranchiseId, f => f.Name, ct);
        var linkTypes = (await db.Set<Link>().AsNoTracking()
                                 .Where(l => pageDeckIds.Contains(l.DeckId))
                                 .Select(l => new { l.DeckId, l.LinkType })
                                 .Distinct()
                                 .ToListAsync(ct))
                        .GroupBy(l => l.DeckId)
                        .ToDictionary(g => g.Key, g => g.Select(l => l.LinkType).Order().ToList());

        var dtos = page.Select(g =>
        {
            var ordered = g.DeckIds.OrderBy(id => FranchiseNaming.ReleaseOrder(decks[id].ReleaseDate)).ThenBy(id => id).ToList();
            return new FranchiseSuggestionDto
            {
                Root = g.Root,
                RootKey = g.Key,
                AnchorDeckId = Anchor(ordered, decks),
                Decks = ordered.Where(nodes.ContainsKey).Select(id => new FranchiseSuggestionDeckDto
                {
                    Deck = nodes[id],
                    FranchiseId = decks[id].FranchiseId,
                    FranchiseName = decks[id].FranchiseId is { } f ? franchiseNames.GetValueOrDefault(f) : null,
                    LinkTypes = linkTypes.GetValueOrDefault(id) ?? []
                }).ToList()
            };
        }).ToList();

        return new PaginatedResponse<List<FranchiseSuggestionDto>>(dtos, visible.Count, limit, offset);
    }

    /// <summary>Records each deck as not belonging under the root; returns how many were newly dismissed.</summary>
    public async Task<int> DismissAsync(string rootKey, List<int> deckIds, CancellationToken ct = default)
    {
        var existing = await db.FranchiseSuggestionDismissals
                               .Where(x => x.RootKey == rootKey && deckIds.Contains(x.DeckId))
                               .Select(x => x.DeckId)
                               .ToListAsync(ct);
        var known = await db.Decks.AsNoTracking().Where(d => deckIds.Contains(d.DeckId)).Select(d => d.DeckId).ToListAsync(ct);
        var fresh = known.Except(existing).ToList();

        db.FranchiseSuggestionDismissals.AddRange(fresh.Select(id => new FranchiseSuggestionDismissal { DeckId = id, RootKey = rootKey }));
        await db.SaveChangesAsync(ct);
        return fresh.Count;
    }

    public Task<int> RestoreAsync(string rootKey, List<int> deckIds, CancellationToken ct = default) =>
        db.FranchiseSuggestionDismissals.Where(x => x.RootKey == rootKey && deckIds.Contains(x.DeckId)).ExecuteDeleteAsync(ct);

    private async Task<HashSet<string>> CommonWordsAsync(List<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0)
            return [];

        var words = await db.Lookups.AsNoTracking()
                            .Where(l => keys.Contains(l.LookupKey))
                            .Join(db.JMDictWords, l => l.WordId, w => w.WordId, (l, w) => new { l.LookupKey, w.PartsOfSpeech })
                            .ToListAsync(ct);

        return words.GroupBy(w => w.LookupKey)
                    .Where(g => FranchiseSuggestions.IsCommonWord(g.SelectMany(w => w.PartsOfSpeech)))
                    .Select(g => g.Key)
                    .ToHashSet();
    }

    private static int Anchor(List<int> ordered, Dictionary<int, DeckFacts> decks)
    {
        var mainFranchise = ordered.Where(id => decks[id].FranchiseId != null)
                                   .GroupBy(id => decks[id].FranchiseId!.Value)
                                   .OrderByDescending(g => g.Count())
                                   .ThenBy(g => g.Key)
                                   .FirstOrDefault();
        return mainFranchise?.First() ?? ordered[0];
    }
}
