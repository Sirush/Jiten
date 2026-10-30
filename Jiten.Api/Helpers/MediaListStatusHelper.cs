using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Helpers;

/// <param name="UpdatedAt">Latest change across the display deck and its volumes.</param>
public readonly record struct DisplayStatus(DeckStatus Status, bool IsFavourite, DateTime UpdatedAt);

/// <summary><c>Note</c> is loaded only when asked for.</summary>
public readonly record struct PreferenceRow(
    int DeckId,
    DeckStatus Status,
    bool IsFavourite,
    DateTime UpdatedAt,
    bool IsPrivate,
    short? Score,
    string? Note);

public static class MediaListStatusHelper
{
    /// <summary>
    /// A user's tracked statuses keyed by the top-level deck they display under: the parent's own status wins over its
    /// children's, otherwise the highest-ranked child status; favourite is set if any of them is.
    /// </summary>
    public static async Task<Dictionary<int, DisplayStatus>> GetDisplayStatusesAsync(
        UserDbContext userContext, JitenDbContext jitenContext, string userId)
    {
        var prefs = await userContext.UserDeckPreferences
                                     .AsNoTracking()
                                     .Where(p => p.UserId == userId && p.Status != DeckStatus.None)
                                     .Select(p => new PreferenceRow(p.DeckId, p.Status, p.IsFavourite, p.UpdatedAt, p.IsPrivate, p.Score, null))
                                     .ToListAsync();

        return await FoldDisplayStatusesAsync(jitenContext, prefs);
    }

    /// <summary>Every row that carries a status, a privacy flag or a score, plus noted rows when <paramref name="withNotes"/>.</summary>
    public static Task<List<PreferenceRow>> LoadPreferenceRowsAsync(UserDbContext userContext, string userId, bool withNotes,
                                                                    CancellationToken ct = default) =>
        userContext.UserDeckPreferences
                   .AsNoTracking()
                   .Where(p => p.UserId == userId && (p.Status != DeckStatus.None || p.IsPrivate || p.Score != null || (withNotes && p.Note != null)))
                   .Select(p => new PreferenceRow(p.DeckId, p.Status, p.IsFavourite, p.UpdatedAt, p.IsPrivate, p.Score, withNotes ? p.Note : null))
                   .ToListAsync(ct);

    /// <summary>Folds the rows with a status into display statuses, as <see cref="GetDisplayStatusesAsync"/> does; rows of missing decks drop out.</summary>
    public static async Task<Dictionary<int, DisplayStatus>> FoldDisplayStatusesAsync(JitenDbContext jitenContext, IEnumerable<PreferenceRow> rows)
    {
        var prefs = rows.Where(p => p.Status != DeckStatus.None).ToList();
        if (prefs.Count == 0)
            return new Dictionary<int, DisplayStatus>();

        var prefDeckIds = prefs.Select(p => p.DeckId).ToList();
        var parentByDeck = await jitenContext.Decks
                                             .AsNoTracking()
                                             .Where(d => prefDeckIds.Contains(d.DeckId))
                                             .Select(d => new { d.DeckId, d.ParentDeckId })
                                             .ToDictionaryAsync(d => d.DeckId, d => d.ParentDeckId);

        var folded = new Dictionary<int, (DisplayStatus Value, bool Own)>();
        foreach (var p in prefs)
        {
            if (!parentByDeck.TryGetValue(p.DeckId, out var parentId)) continue;
            var displayId = parentId ?? p.DeckId;
            var isOwn = displayId == p.DeckId;

            if (!folded.TryGetValue(displayId, out var cur))
            {
                folded[displayId] = (new DisplayStatus(p.Status, p.IsFavourite, p.UpdatedAt), isOwn);
                continue;
            }

            var statusValue = cur.Value.Status;
            if (isOwn && !cur.Own)
                statusValue = p.Status;
            else if (isOwn == cur.Own && p.Status.Rank() > cur.Value.Status.Rank())
                statusValue = p.Status;

            var updatedAt = p.UpdatedAt > cur.Value.UpdatedAt ? p.UpdatedAt : cur.Value.UpdatedAt;
            folded[displayId] = (new DisplayStatus(statusValue, cur.Value.IsFavourite || p.IsFavourite, updatedAt), cur.Own || isOwn);
        }

        return folded.ToDictionary(kv => kv.Key, kv => kv.Value.Value);
    }
}
