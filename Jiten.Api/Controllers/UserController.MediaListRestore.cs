using Hangfire;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Helpers;
using Jiten.Api.Jobs;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

public partial class UserController
{
    /// <summary>A series and every volume the units stepper can change in one call.</summary>
    private const int MaxRestoredDecks = MaxProgressSubdecksPerDeck + 1;

    [HttpPost("media-list/restore")]
    [SwaggerOperation(Summary = "Put titles back to a snapshot of their status and history, such as the one a change returned for Undo")]
    public async Task<IResult> RestoreMediaList([FromBody] RestoreMediaListRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var snapshots = request.Decks;
        if (snapshots.Count == 0 || snapshots.Count > MaxRestoredDecks)
            return Results.BadRequest(new { message = $"Restore between 1 and {MaxRestoredDecks} titles at a time." });
        if (snapshots.Select(s => s.DeckId).Distinct().Count() != snapshots.Count)
            return Results.BadRequest(new { message = "Each title can only appear once." });

        foreach (var snapshot in snapshots)
        {
            if (ValidateSnapshot(snapshot) is { } snapshotError)
                return Results.BadRequest(new { message = snapshotError });
        }

        var deckIds = snapshots.Select(s => s.DeckId).ToList();
        var parentOf = await jitenContext.Decks.AsNoTracking()
                                         .Where(d => deckIds.Contains(d.DeckId))
                                         .ToDictionaryAsync(d => d.DeckId, d => d.ParentDeckId);
        if (parentOf.Count != deckIds.Count)
            return Results.NotFound(new { message = "Deck not found." });

        // An id may name an entry deleted since the snapshot, which is recreated, but never a row of another title or user.
        var entryIds = snapshots.SelectMany(s => s.Entries.Select(e => e.Id)).OfType<long>().ToList();
        if (entryIds.Distinct().Count() != entryIds.Count)
            return Results.BadRequest(new { message = "An entry appears twice." });
        var deckOfEntry = snapshots.SelectMany(s => s.Entries.Where(e => e.Id != null).Select(e => (Id: e.Id!.Value, s.DeckId)))
                                   .ToDictionary(e => e.Id, e => e.DeckId);
        var owners = await userContext.UserMediaListEntries
                                      .Where(r => entryIds.Contains(r.Id))
                                      .Select(r => new { r.Id, r.UserId, r.DeckId })
                                      .ToListAsync();
        if (owners.Any(o => o.UserId != userId || o.DeckId != deckOfEntry[o.Id]))
            return Results.BadRequest(new { message = "That history belongs to another title." });

        var preferences = await userContext.UserDeckPreferences
                                           .Where(p => p.UserId == userId && deckIds.Contains(p.DeckId))
                                           .ToDictionaryAsync(p => p.DeckId);
        var existing = (await userContext.UserMediaListEntries
                                         .Where(r => r.UserId == userId && deckIds.Contains(r.DeckId))
                                         .ToListAsync())
                       .ToDictionary(r => r.Id);
        var parentIds = parentOf.Values.OfType<int>().Distinct().ToList();
        var seriesEntryIds = (await userContext.UserMediaListEntries
                                               .Where(r => r.UserId == userId && parentIds.Contains(r.DeckId))
                                               .Select(r => new { r.Id, r.DeckId })
                                               .ToListAsync())
                             .ToDictionary(r => r.Id, r => r.DeckId);

        var restored = new Dictionary<long, UserMediaListEntry>();
        var links = new List<(UserMediaListEntry Entry, long SeriesEntryId, int SeriesDeckId)>();
        var kept = snapshots.SelectMany(s => s.Entries).Select(e => e.Id).OfType<long>().ToHashSet();

        foreach (var snapshot in snapshots)
        {
            var preference = preferences.GetValueOrDefault(snapshot.DeckId);
            foreach (var gone in existing.Values.Where(r => r.DeckId == snapshot.DeckId && !kept.Contains(r.Id)))
                userContext.UserMediaListEntries.Remove(gone);

            UserMediaListEntry? current = null;
            foreach (var item in snapshot.Entries)
            {
                var entry = item.Id is { } id ? existing.GetValueOrDefault(id) : null;
                if (entry == null)
                {
                    entry = new UserMediaListEntry { UserId = userId, DeckId = snapshot.DeckId };
                    userContext.UserMediaListEntries.Add(entry);
                }

                var finishedOn = item.State == MediaListEntryState.InProgress ? null : item.FinishedOn;
                if (item.State != MediaListEntryState.Completed || entry.FinishedOn != finishedOn)
                    entry.CoverageAtFinish = null;
                entry.State = item.State;
                entry.StartedOn = item.StartedOn;
                entry.FinishedOn = finishedOn;
                entry.CharactersRead = item.CharactersRead;
                entry.SeriesEntry = null;
                entry.SeriesEntryId = null;
                if (item.SeriesEntryId is { } link && parentOf[snapshot.DeckId] is { } seriesDeckId)
                    links.Add((entry, link, seriesDeckId));
                if (item.Id is { } snapshotId)
                    restored[snapshotId] = entry;
                if (item.IsCurrent)
                    current = entry;
            }

            if (snapshot.Status == DeckStatus.None)
            {
                if (preference != null)
                    MediaListEntryHelper.ClearStatus(userContext, preference);
                continue;
            }

            if (preference == null)
            {
                preference = new UserDeckPreference { UserId = userId, DeckId = snapshot.DeckId };
                userContext.UserDeckPreferences.Add(preference);
                preferences[snapshot.DeckId] = preference;
            }

            preference.Status = snapshot.Status;
            preference.CurrentEntry = current;
            if (current == null)
                preference.CurrentEntryId = null;
        }

        foreach (var (entry, link, seriesDeckId) in links)
        {
            if (restored.TryGetValue(link, out var seriesEntry) && seriesEntry.DeckId == seriesDeckId)
                entry.SeriesEntry = seriesEntry;
            else if (seriesEntryIds.GetValueOrDefault(link) == seriesDeckId && !deckIds.Contains(seriesDeckId))
                entry.SeriesEntryId = link;
        }

        await userContext.SaveChangesAsync();

        var parentChanges = new List<object>();
        foreach (var snapshot in snapshots.Where(s => parentOf[s.DeckId] is { } p && !deckIds.Contains(p)))
        {
            var seriesId = parentOf[snapshot.DeckId]!.Value;
            var parent = await UpdateParentDeckStatus(userId, seriesId, snapshot.Status, null);
            if (parent.ParentDeckId != null)
                parentChanges.Add(new { deckId = seriesId, status = parent.ParentStatus });
        }

        backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserAccomplishments(userId));
        await smartDeckDirty.MarkDirty(userId);

        var summaries = await MediaListEntryHelper.BuildSummariesAsync(userContext, jitenContext, userId, deckIds);
        var statuses = await userContext.UserDeckPreferences.AsNoTracking()
                                        .Where(p => p.UserId == userId && deckIds.Contains(p.DeckId))
                                        .ToDictionaryAsync(p => p.DeckId, p => p.Status);

        return Results.Ok(new
                          {
                              decks = deckIds.Select(id => new
                                                           {
                                                               deckId = id, status = statuses.GetValueOrDefault(id, DeckStatus.None),
                                                               listEntry = summaries.GetValueOrDefault(id), parentDeckId = parentOf[id]
                                                           })
                                             .ToList(),
                              parents = parentChanges
                          });
    }

    /// <summary>The same limits the other media list endpoints hold a title to.</summary>
    private static string? ValidateSnapshot(MediaListSnapshot snapshot)
    {
        if (!Enum.IsDefined(snapshot.Status))
            return "Unknown status.";
        if (snapshot.Entries.Count > MediaListEntryHelper.MaxEntriesPerDeck)
            return MediaListEntryHelper.HistoryFullMessage;
        if (snapshot.Status == DeckStatus.None && snapshot.Entries.Count > 0)
            return "A title off the list keeps no history.";

        foreach (var entry in snapshot.Entries)
        {
            if (!Enum.IsDefined(entry.State))
                return "Unknown entry state.";
            if (ValidateEntry(entry.State, entry.StartedOn, entry.FinishedOn, entry.CharactersRead) is { } error)
                return error;
        }

        if (snapshot.Entries.Count(e => e.State == MediaListEntryState.InProgress) > 1)
            return "Only one entry can be in progress.";

        var current = snapshot.Entries.Where(e => e.IsCurrent).ToList();
        if (current.Count > 1 || (current.Count == 1 && !MediaListEntryHelper.CanBeCurrent(snapshot.Status, current[0].State)))
            return "The current entry does not match the status.";

        return null;
    }
}
