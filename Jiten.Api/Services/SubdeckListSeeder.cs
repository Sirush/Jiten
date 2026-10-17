using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

/// <summary>
/// Gives the subdeck that inherits a deck's own text a copy of every user's status and passes on that deck.
/// The parent keeps its rows: it stays the title on users' lists, and difficulty votes, ratings and rankings only exist on top-level decks.
/// </summary>
public sealed class SubdeckListSeeder(UserDbContext userContext)
{
    public async Task SeedFromParentAsync(int parentDeckId, int childDeckId)
    {
        var parentEntries = await userContext.UserMediaListEntries.AsNoTracking()
                                             .Where(e => e.DeckId == parentDeckId)
                                             .ToListAsync();
        var parentPreferences = await userContext.UserDeckPreferences.AsNoTracking()
                                                 .Where(p => p.DeckId == parentDeckId && p.Status != DeckStatus.None)
                                                 .ToListAsync();
        if (parentEntries.Count == 0 && parentPreferences.Count == 0)
            return;

        var copies = parentEntries.ToDictionary(e => e.Id, e => new UserMediaListEntry
                                                                {
                                                                    UserId = e.UserId, DeckId = childDeckId, State = e.State,
                                                                    StartedOn = e.StartedOn, FinishedOn = e.FinishedOn,
                                                                    CharactersRead = e.CharactersRead, CoverageAtFinish = e.CoverageAtFinish,
                                                                    SeriesEntryId = e.Id, CreatedAt = e.CreatedAt
                                                                });
        userContext.UserMediaListEntries.AddRange(copies.Values);
        await userContext.SaveChangesAsync();

        foreach (var preference in parentPreferences)
        {
            userContext.UserDeckPreferences.Add(new UserDeckPreference
                                                {
                                                    UserId = preference.UserId, DeckId = childDeckId, Status = preference.Status,
                                                    CurrentEntryId = preference.CurrentEntryId is { } current && copies.TryGetValue(current, out var copy)
                                                        ? copy.Id
                                                        : null
                                                });
        }

        await userContext.SaveChangesAsync();
        await RestoreTimestampsAsync(parentDeckId, childDeckId);
    }

    /// <summary>SaveChanges stamps added rows with now, which would reset the stale-read nudge and popularity recency.</summary>
    private async Task RestoreTimestampsAsync(int parentDeckId, int childDeckId)
    {
        await userContext.UserMediaListEntries
                         .Where(e => e.DeckId == childDeckId && e.SeriesEntryId != null)
                         .ExecuteUpdateAsync(s => s.SetProperty(e => e.UpdatedAt,
                                                                e => userContext.UserMediaListEntries
                                                                                .Where(source => source.Id == e.SeriesEntryId)
                                                                                .Select(source => source.UpdatedAt)
                                                                                .First()));

        await userContext.UserDeckPreferences
                         .Where(p => p.DeckId == childDeckId)
                         .ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedAt,
                                                                p => userContext.UserDeckPreferences
                                                                                .Where(q => q.UserId == p.UserId && q.DeckId == parentDeckId)
                                                                                .Select(q => q.UpdatedAt)
                                                                                .First()));
    }
}
