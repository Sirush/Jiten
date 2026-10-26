using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Helpers;

public static class MediaListStatusHelper
{
    /// <summary>
    /// A user's tracked statuses keyed by the top-level deck they display under: the parent's own status wins over its
    /// children's, otherwise the highest-ranked child status; favourite is set if any of them is.
    /// </summary>
    public static async Task<Dictionary<int, (DeckStatus Status, bool IsFavourite)>> GetDisplayStatusesAsync(
        UserDbContext userContext, JitenDbContext jitenContext, string userId)
    {
        var prefs = await userContext.UserDeckPreferences
                                     .AsNoTracking()
                                     .Where(p => p.UserId == userId && p.Status != DeckStatus.None)
                                     .Select(p => new { p.DeckId, p.Status, p.IsFavourite })
                                     .ToListAsync();

        if (prefs.Count == 0)
            return new Dictionary<int, (DeckStatus Status, bool IsFavourite)>();

        var prefDeckIds = prefs.Select(p => p.DeckId).ToList();
        var parentByDeck = await jitenContext.Decks
                                             .AsNoTracking()
                                             .Where(d => prefDeckIds.Contains(d.DeckId))
                                             .Select(d => new { d.DeckId, d.ParentDeckId })
                                             .ToDictionaryAsync(d => d.DeckId, d => d.ParentDeckId);

        var folded = new Dictionary<int, (DeckStatus Status, bool Own, bool IsFavourite)>();
        foreach (var p in prefs)
        {
            if (!parentByDeck.TryGetValue(p.DeckId, out var parentId)) continue;
            var displayId = parentId ?? p.DeckId;
            var isOwn = displayId == p.DeckId;

            if (!folded.TryGetValue(displayId, out var cur))
            {
                folded[displayId] = (p.Status, isOwn, p.IsFavourite);
                continue;
            }

            var statusValue = cur.Status;
            if (isOwn && !cur.Own)
                statusValue = p.Status;
            else if (isOwn == cur.Own && p.Status.Rank() > cur.Status.Rank())
                statusValue = p.Status;

            folded[displayId] = (statusValue, cur.Own || isOwn, cur.IsFavourite || p.IsFavourite);
        }

        return folded.ToDictionary(kv => kv.Key, kv => (kv.Value.Status, kv.Value.IsFavourite));
    }
}
