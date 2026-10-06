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
    private static readonly TimeSpan StaleEntryAge = TimeSpan.FromDays(30);
    private const int MaxStaleEntries = 10;
    private const string DateOutOfRangeMessage = "Pick a date between 1900 and today.";

    [HttpGet("media-list/entries/{deckId:int}")]
    [SwaggerOperation(Summary = "Get the caller's media list entries for a deck")]
    public async Task<IResult> GetMediaListEntries(int deckId)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        return Results.Ok(await BuildMediaListEntriesResponseAsync(userId, deckId));
    }

    [HttpGet("media-list/stale")]
    [SwaggerOperation(Summary = "Titles in progress with no update for a month, least recently touched first")]
    public async Task<IResult> GetStaleMediaListEntries()
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var cutoff = DateTime.UtcNow - StaleEntryAge;
        var candidates = await userContext.UserDeckPreferences
                                          .AsNoTracking()
                                          .Where(p => p.UserId == userId && p.Status == DeckStatus.Ongoing && !p.IsIgnored && p.UpdatedAt < cutoff &&
                                                      p.CurrentEntry != null && p.CurrentEntry.State == MediaListEntryState.InProgress &&
                                                      p.CurrentEntry.UpdatedAt < cutoff)
                                          .OrderBy(p => p.CurrentEntry!.UpdatedAt)
                                          .Select(p => new { p.DeckId, p.CurrentEntry!.UpdatedAt })
                                          .Take(MaxStaleEntries * 3)
                                          .ToListAsync();

        if (candidates.Count == 0)
            return Results.Ok(Array.Empty<object>());

        var candidateIds = candidates.Select(c => c.DeckId).ToList();
        var decks = await jitenContext.Decks
                                      .AsNoTracking()
                                      .Where(d => candidateIds.Contains(d.DeckId))
                                      .Select(d => new
                                                   {
                                                       d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.MediaType, d.CoverName,
                                                       d.CharacterCount, d.ParentDeckId, d.ReleaseDate, childrenDeckCount = d.Children.Count,
                                                       parent = d.ParentDeck == null
                                                           ? null
                                                           : new { d.ParentDeck.OriginalTitle, d.ParentDeck.RomajiTitle, d.ParentDeck.EnglishTitle, d.ParentDeck.CoverName }
                                                   })
                                      .ToDictionaryAsync(d => d.DeckId);

        var lastActivity = candidates.Where(c => decks.ContainsKey(c.DeckId)).ToDictionary(c => c.DeckId, c => c.UpdatedAt);

        // A series is still being read while any of its volumes changes, and a stale volume already asks about it.
        var seriesIds = decks.Values.Where(d => d.childrenDeckCount > 0).Select(d => d.DeckId).ToList();
        if (seriesIds.Count > 0)
        {
            var children = await jitenContext.Decks.AsNoTracking()
                                             .Where(d => d.ParentDeckId != null && seriesIds.Contains(d.ParentDeckId.Value))
                                             .Select(d => new { d.DeckId, ParentDeckId = d.ParentDeckId!.Value })
                                             .ToListAsync();
            var childIds = children.Select(c => c.DeckId).ToList();
            var prefTimes = await userContext.UserDeckPreferences.AsNoTracking()
                                             .Where(p => p.UserId == userId && childIds.Contains(p.DeckId))
                                             .Select(p => new { p.DeckId, p.UpdatedAt })
                                             .ToListAsync();
            var entryTimes = await userContext.UserMediaListEntries.AsNoTracking()
                                              .Where(r => r.UserId == userId && childIds.Contains(r.DeckId))
                                              .GroupBy(r => r.DeckId)
                                              .Select(g => new { DeckId = g.Key, UpdatedAt = g.Max(r => r.UpdatedAt) })
                                              .ToListAsync();

            var latestByChild = prefTimes.Concat(entryTimes)
                                         .GroupBy(t => t.DeckId)
                                         .ToDictionary(g => g.Key, g => g.Max(t => t.UpdatedAt));
            foreach (var series in children.GroupBy(c => c.ParentDeckId))
            {
                var latest = series.Select(c => latestByChild.GetValueOrDefault(c.DeckId)).DefaultIfEmpty().Max();
                if (latest >= cutoff || series.Any(c => lastActivity.ContainsKey(c.DeckId)))
                    lastActivity.Remove(series.Key);
                else if (latest > lastActivity[series.Key])
                    lastActivity[series.Key] = latest;
            }
        }

        var stale = lastActivity.OrderBy(a => a.Value).Take(MaxStaleEntries).ToList();
        var summaries = await MediaListEntryHelper.BuildSummariesAsync(userContext, jitenContext, userId, stale.Select(a => a.Key).ToList());

        return Results.Ok(stale.Select(a =>
                               {
                                   var d = decks[a.Key];
                                   return new
                                          {
                                              d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.MediaType, d.CoverName, d.CharacterCount,
                                              d.ParentDeckId, d.ReleaseDate, d.parent, d.childrenDeckCount, status = DeckStatus.Ongoing,
                                              listEntry = summaries.GetValueOrDefault(a.Key), lastUpdatedAt = a.Value
                                          };
                               })
                               .ToList());
    }

    [HttpPost("media-list/stale/{deckId:int}/still-going")]
    [SwaggerOperation(Summary = "Confirm a title in progress is still being read, which restarts the month before it is asked about again")]
    public async Task<IResult> ConfirmStillGoing(int deckId)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var entry = await userContext.UserDeckPreferences
                                     .Where(p => p.UserId == userId && p.DeckId == deckId && p.Status == DeckStatus.Ongoing)
                                     .Select(p => p.CurrentEntry)
                                     .FirstOrDefaultAsync();
        if (entry is not { State: MediaListEntryState.InProgress })
            return Results.NotFound(new { message = "This title is not in progress." });

        entry.UpdatedAt = DateTime.UtcNow;
        await userContext.SaveChangesAsync();
        return Results.NoContent();
    }

    [HttpPost("media-list/units/{deckId:int}")]
    [SwaggerOperation(Summary = "Set how many volumes or episodes of a series are completed")]
    public async Task<IResult> SetCompletedUnits(int deckId, [FromBody] SetCompletedUnitsRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        if (request.Date is { } requested && !MediaListEntryHelper.IsAllowedDate(requested))
            return Results.BadRequest(new { message = DateOutOfRangeMessage });
        var date = request.Date ?? DateOnly.FromDateTime(DateTime.UtcNow);

        var deck = await jitenContext.Decks.AsNoTracking()
                                     .Where(d => d.DeckId == deckId)
                                     .Select(d => new { d.MediaType, d.ParentDeckId })
                                     .FirstOrDefaultAsync();
        if (deck == null)
            return Results.NotFound(new { message = "Deck not found." });
        if (deck.ParentDeckId != null || !MediaListEntryHelper.TracksUnits(deck.MediaType))
            return Results.BadRequest(new { message = "This title has no volumes or episodes to count." });

        var children = await jitenContext.Decks.AsNoTracking()
                                         .Where(d => d.ParentDeckId == deckId)
                                         .OrderBy(d => d.DeckOrder).ThenBy(d => d.DeckId)
                                         .Select(d => d.DeckId)
                                         .ToListAsync();
        if (children.Count == 0)
            return Results.BadRequest(new { message = "This title has no volumes or episodes to count." });
        if (request.Completed < 0 || request.Completed > children.Count)
            return Results.BadRequest(new { message = $"Enter a number between 0 and {children.Count}." });

        var previous = await TakeSnapshotsAsync(userId, [deckId, ..children]);
        // Counted like the summary the stepper shows: finished at least once, or during a series reread, completed on that pass.
        long? rereadPass = (await MediaListEntryHelper.RereadPassIdsAsync(userContext, userId, [deckId])).TryGetValue(deckId, out var pass) ? pass : null;
        var completed = await MediaListEntryHelper.CompletedUnitIdsAsync(userContext, userId, deckId, children);
        var volumeStatus = DeckStatus.None;
        var changedVolumeIds = new List<int>();

        if (request.Completed > completed.Count)
        {
            var completeIds = children.Where(id => !completed.Contains(id)).Take(request.Completed - completed.Count).ToList();

            // A unit finished on an earlier pass already reads Completed, so the reread gets a completion of its own.
            var finishedBefore = rereadPass == null
                ? []
                : await userContext.UserDeckPreferences
                                   .Include(p => p.CurrentEntry)
                                   .Where(p => p.UserId == userId && completeIds.Contains(p.DeckId) &&
                                               (p.Status == DeckStatus.Completed || p.CurrentEntry!.State == MediaListEntryState.Completed))
                                   .ToListAsync();
            var fullDeckIds = await MediaListEntryHelper.FullHistoryDeckIdsAsync(userContext, userId, finishedBefore.Select(p => p.DeckId).ToList());
            foreach (var preference in finishedBefore.Where(p => !fullDeckIds.Contains(p.DeckId)))
                MediaListEntryHelper.CompleteForSeriesPass(userContext, preference, rereadPass!.Value, date);

            var toComplete = completeIds.Where(id => finishedBefore.All(p => p.DeckId != id)).Select(id => (id, DeckStatus.Completed)).ToList();
            var outcome = await DeckPreferenceHelper.ApplyStatusesAsync(userContext, jitenContext, userId, toComplete, overwriteExisting: true,
                                                                        skipIgnored: false, date);

            await SnapshotCoverageAtFinishAsync(userId, finishedBefore.Concat(outcome.Preferences.Values).Select(p => p.CurrentEntry));
            await userContext.SaveChangesAsync();
            volumeStatus = DeckStatus.Completed;
            changedVolumeIds.AddRange(completeIds.Where(id => !fullDeckIds.Contains(id)));
        }
        else if (request.Completed < completed.Count)
        {
            // Only units marked Completed are lowered, and during a reread only those completed on it; earlier completions stay history.
            var preferences = await userContext.UserDeckPreferences
                                               .Include(p => p.CurrentEntry)
                                               .Where(p => p.UserId == userId && children.Contains(p.DeckId) && p.Status == DeckStatus.Completed &&
                                                           (rereadPass == null || p.CurrentEntry!.SeriesEntryId == rereadPass))
                                               .ToListAsync();
            var count = completed.Count - request.Completed;
            if (preferences.Count < count)
            {
                var floor = completed.Count - preferences.Count;
                var which = floor == 1 ? "one of them was" : $"{floor} of them were";
                return Results.BadRequest(new { message = $"Can't go below {floor}: {which} finished on an earlier read. Delete that completion in the history to count it as unfinished." });
            }

            // The latest completions go first, so lowering undoes what raising just did instead of reaching for an old, undated one.
            var toLower = preferences.OrderByDescending(p => p.CurrentEntry?.FinishedOn ?? DateOnly.MinValue)
                                     .ThenByDescending(p => p.CurrentEntry?.Id ?? 0)
                                     .ThenByDescending(p => children.IndexOf(p.DeckId))
                                     .Take(count)
                                     .ToList();
            var reopen = new List<(int DeckId, DeckStatus Status)>();
            var lowerIds = toLower.Select(p => p.DeckId).ToList();
            var otherEntries = (await userContext.UserMediaListEntries
                                                 .Where(r => r.UserId == userId && lowerIds.Contains(r.DeckId))
                                                 .ToListAsync())
                               .GroupBy(r => r.DeckId)
                               .ToDictionary(g => g.Key, g => g.ToList());
            foreach (var preference in toLower)
            {
                // A completion with a start date or a count holds something worth keeping, so it goes back to in progress; a bare one is removed.
                if (preference.CurrentEntry is { } entry && (entry.StartedOn != null || entry.CharactersRead != null))
                {
                    reopen.Add((preference.DeckId, DeckStatus.Ongoing));
                    continue;
                }

                var bare = preference.CurrentEntry;
                if (bare != null)
                    userContext.UserMediaListEntries.Remove(bare);
                var previousEntry = (otherEntries.GetValueOrDefault(preference.DeckId) ?? []).Where(r => r != bare).MaxBy(r => r.Id);
                if (previousEntry == null)
                {
                    MediaListEntryHelper.ClearStatus(userContext, preference);
                    continue;
                }

                preference.Status = MediaListEntryHelper.StatusFor(previousEntry.State);
                preference.CurrentEntry = previousEntry;
            }

            changedVolumeIds.AddRange(lowerIds);
            if (reopen.Count > 0)
            {
                await DeckPreferenceHelper.ApplyStatusesAsync(userContext, jitenContext, userId, reopen, overwriteExisting: true, skipIgnored: false, date);
                volumeStatus = DeckStatus.Ongoing;
            }

            await userContext.SaveChangesAsync();
        }

        var parent = await UpdateParentDeckStatus(userId, deckId, volumeStatus, date, setOnSeries: true, changedVolumeIds);
        var status = parent.ParentStatus
                     ?? await userContext.UserDeckPreferences.Where(p => p.UserId == userId && p.DeckId == deckId).Select(p => (DeckStatus?)p.Status)
                                         .FirstOrDefaultAsync()
                     ?? DeckStatus.None;

        backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserAccomplishments(userId));
        await smartDeckDirty.MarkDirty(userId);

        var summaries = await MediaListEntryHelper.BuildSummariesAsync(userContext, jitenContext, userId, [deckId, ..changedVolumeIds], [deckId]);
        var volumeStatuses = await userContext.UserDeckPreferences.AsNoTracking()
                                              .Where(p => p.UserId == userId && changedVolumeIds.Contains(p.DeckId))
                                              .ToDictionaryAsync(p => p.DeckId, p => p.Status);
        var volumes = changedVolumeIds.Select(id => new
                                                    {
                                                        deckId = id, status = volumeStatuses.GetValueOrDefault(id, DeckStatus.None),
                                                        listEntry = summaries.GetValueOrDefault(id)
                                                    })
                                      .ToList();

        return Results.Ok(new
                          {
                              deckId, status, listEntry = summaries.GetValueOrDefault(deckId), allChildrenCompleted = parent.AllChildrenCompleted, volumes,
                              previous = previous.Where(s => changedVolumeIds.Count > 0 && (s.DeckId == deckId || changedVolumeIds.Contains(s.DeckId))).ToList()
                          });
    }

    [HttpPost("media-list/entries/{deckId:int}")]
    [SwaggerOperation(Summary = "Start a new entry, or record a past one")]
    public async Task<IResult> AddMediaListEntry(int deckId, [FromBody] AddMediaListEntryRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        if (!Enum.IsDefined(request.State))
            return Results.BadRequest(new { message = "Unknown entry state." });
        var deck = await jitenContext.Decks.AsNoTracking()
                                     .Where(d => d.DeckId == deckId)
                                     .Select(d => new { d.ParentDeckId })
                                     .FirstOrDefaultAsync();
        if (deck == null)
            return Results.NotFound(new { message = "Deck not found." });

        var error = ValidateEntry(request.State, request.StartedOn, request.FinishedOn, request.CharactersRead);
        if (error != null)
            return Results.BadRequest(new { message = error });

        if (await userContext.UserMediaListEntries.CountAsync(r => r.UserId == userId && r.DeckId == deckId) >= MediaListEntryHelper.MaxEntriesPerDeck)
            return Results.BadRequest(new { message = MediaListEntryHelper.HistoryFullMessage });

        var preference = await userContext.UserDeckPreferences
                                          .Include(p => p.CurrentEntry)
                                          .FirstOrDefaultAsync(p => p.UserId == userId && p.DeckId == deckId);

        if (request.State == MediaListEntryState.InProgress && preference?.CurrentEntry is { State: MediaListEntryState.InProgress })
            return Results.BadRequest(new { message = "This title is already in progress." });

        var entry = new UserMediaListEntry
                   {
                       UserId = userId, DeckId = deckId, State = request.State, StartedOn = request.StartedOn,
                       FinishedOn = request.State == MediaListEntryState.InProgress ? null : request.FinishedOn, CharactersRead = request.CharactersRead
                   };
        userContext.UserMediaListEntries.Add(entry);
        // A pass started now belongs to the series pass in progress; a past one added by hand is not placed in any.
        if (request.State == MediaListEntryState.InProgress && deck.ParentDeckId != null)
            entry.SeriesEntry = (await MediaListEntryHelper.SeriesPassesAsync(userContext, jitenContext, userId, [deckId])).GetValueOrDefault(deckId);

        if (preference == null)
        {
            preference = new UserDeckPreference { UserId = userId, DeckId = deckId };
            userContext.UserDeckPreferences.Add(preference);
        }

        // A new in-progress entry always becomes the current one; a past entry only does when the title had no status to keep.
        var statusChanged = false;
        if (request.State == MediaListEntryState.InProgress || preference.Status == DeckStatus.None)
        {
            var status = MediaListEntryHelper.StatusFor(request.State);
            statusChanged = preference.Status != status;
            preference.Status = status;
            preference.CurrentEntry = entry;
        }

        await SnapshotCoverageAtFinishAsync(userId, [entry]);
        await userContext.SaveChangesAsync();
        backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserAccomplishments(userId));

        (int? ParentDeckId, DeckStatus? ParentStatus, bool) parent = (null, null, false);
        if (statusChanged || entry.State == MediaListEntryState.Completed)
        {
            if (deck.ParentDeckId is { } parentDeckId)
            {
                var parentDate = request.State == MediaListEntryState.InProgress ? request.StartedOn : null;
                parent = await UpdateParentDeckStatus(userId, parentDeckId, preference.Status, parentDate, changedVolumeIds: [deckId]);
            }

            await smartDeckDirty.MarkDirty(userId);
        }

        return Results.Ok(await BuildMediaListEntriesResponseAsync(userId, deckId, parent.ParentDeckId, parent.ParentStatus, parent.Item3));
    }

    [HttpPut("media-list/entries/{deckId:int}/{entryId:long}")]
    [SwaggerOperation(Summary = "Edit the dates or character count of an entry")]
    public async Task<IResult> UpdateMediaListEntry(int deckId, long entryId, [FromBody] UpdateMediaListEntryRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var entry = await userContext.UserMediaListEntries.FirstOrDefaultAsync(r => r.Id == entryId && r.UserId == userId && r.DeckId == deckId);
        if (entry == null)
            return Results.NotFound(new { message = "That part of your history no longer exists." });

        var startedOn = request.HasStartedOn ? request.StartedOn : entry.StartedOn;
        var finishedOn = request.HasFinishedOn ? request.FinishedOn : entry.FinishedOn;
        var charactersRead = request.HasCharactersRead ? request.CharactersRead : entry.CharactersRead;
        var error = ValidateEntry(entry.State, startedOn, finishedOn, charactersRead);
        if (error != null)
            return Results.BadRequest(new { message = error });

        // A moved finish date no longer matches the day the coverage was captured.
        if (entry.FinishedOn != finishedOn)
            entry.CoverageAtFinish = null;

        entry.StartedOn = startedOn;
        entry.FinishedOn = entry.State == MediaListEntryState.InProgress ? null : finishedOn;
        entry.CharactersRead = charactersRead;

        await userContext.SaveChangesAsync();
        backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserAccomplishments(userId));

        return Results.Ok(await BuildMediaListEntriesResponseAsync(userId, deckId));
    }

    [HttpDelete("media-list/entries/{deckId:int}/{entryId:long}")]
    [SwaggerOperation(Summary = "Delete one entry; the title's status is unchanged")]
    public async Task<IResult> DeleteMediaListEntry(int deckId, long entryId)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var entry = await userContext.UserMediaListEntries.FirstOrDefaultAsync(r => r.Id == entryId && r.UserId == userId && r.DeckId == deckId);
        if (entry == null)
            return Results.NotFound(new { message = "That part of your history no longer exists." });

        var parentDeckId = await jitenContext.Decks.AsNoTracking().Where(d => d.DeckId == deckId).Select(d => d.ParentDeckId).FirstOrDefaultAsync();
        var previous = await TakeSnapshotsAsync(userId, parentDeckId is { } seriesDeckId ? [deckId, seriesDeckId] : [deckId]);
        var preference = await userContext.UserDeckPreferences
                                          .FirstOrDefaultAsync(p => p.UserId == userId && p.DeckId == deckId && p.CurrentEntryId == entryId);
        var statusChanged = false;
        if (preference != null)
        {
            var others = await userContext.UserMediaListEntries
                                          .Where(r => r.UserId == userId && r.DeckId == deckId && r.Id != entryId)
                                          .OrderByDescending(r => r.Id)
                                          .Select(r => new { r.Id, r.State })
                                          .ToListAsync();
            var expected = MediaListEntryHelper.EntryStateFor(preference.Status);

            // The status moves to the latest other entry it describes, or to none; a past entry must not take progress logged later.
            preference.CurrentEntryId = expected == null ? null : others.FirstOrDefault(r => r.State == expected)?.Id;

            // Completed is only kept while a completion is left; otherwise the title follows its latest remaining entry, or leaves the list.
            if (preference.Status == DeckStatus.Completed && preference.CurrentEntryId == null)
            {
                statusChanged = true;
                var latest = others.FirstOrDefault();
                if (latest != null)
                {
                    preference.Status = MediaListEntryHelper.StatusFor(latest.State);
                    preference.CurrentEntryId = latest.Id;
                }
                else
                    MediaListEntryHelper.ClearStatus(userContext, preference);
            }
        }

        userContext.UserMediaListEntries.Remove(entry);
        await userContext.SaveChangesAsync();
        backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserAccomplishments(userId));

        (int? ParentDeckId, DeckStatus? ParentStatus, bool) parent = (null, null, false);
        if (statusChanged || entry.State == MediaListEntryState.Completed)
        {
            if (parentDeckId is { } seriesId)
            {
                var newStatus = await userContext.UserDeckPreferences.Where(p => p.UserId == userId && p.DeckId == deckId)
                                                 .Select(p => (DeckStatus?)p.Status).FirstOrDefaultAsync() ?? DeckStatus.None;
                parent = await UpdateParentDeckStatus(userId, seriesId, newStatus, null);
            }

            await smartDeckDirty.MarkDirty(userId);
        }

        return Results.Ok(await BuildMediaListEntriesResponseAsync(userId, deckId, parent.ParentDeckId, parent.ParentStatus, previous: previous));
    }

    private static string? ValidateEntry(MediaListEntryState state, DateOnly? startedOn, DateOnly? finishedOn, int? charactersRead)
    {
        if (new[] { startedOn, finishedOn }.Any(date => date is { } d && !MediaListEntryHelper.IsAllowedDate(d)))
            return DateOutOfRangeMessage;

        if (state != MediaListEntryState.InProgress && startedOn is { } start && finishedOn is { } end && end < start)
            return "The finish date is before the start date.";

        return charactersRead is <= 0 or > MediaListEntryHelper.MaxCharactersRead
            ? "Enter a character count above zero or leave it empty."
            : null;
    }

    /// <summary>Parent fields are set when the change moved the status of the series the deck belongs to.</summary>
    private async Task<object> BuildMediaListEntriesResponseAsync(string userId, int deckId, int? parentDeckId = null, DeckStatus? parentStatus = null,
                                                                  bool allChildrenCompleted = false, IReadOnlyList<MediaListSnapshot>? previous = null)
    {
        var entries = await userContext.UserMediaListEntries
                                     .AsNoTracking()
                                     .Where(r => r.UserId == userId && r.DeckId == deckId)
                                     .ToListAsync();

        var preference = await userContext.UserDeckPreferences
                                          .AsNoTracking()
                                          .Where(p => p.UserId == userId && p.DeckId == deckId)
                                          .Select(p => new { p.Status, p.CurrentEntryId })
                                          .FirstOrDefaultAsync();
        var currentReadId = preference?.CurrentEntryId;

        var summary = (await MediaListEntryHelper.BuildSummariesAsync(userContext, jitenContext, userId, [deckId], [deckId])).GetValueOrDefault(deckId);

        return new
               {
                   deckId,
                   status = preference?.Status ?? DeckStatus.None,
                   parentDeckId,
                   parentStatus,
                   allChildrenCompleted,
                   summary,
                   previous,
                   entries = entries.OrderBy(r => r.StartedOn ?? r.FinishedOn ?? DateOnly.MinValue)
                                .ThenBy(r => r.Id)
                                .Select(r => new
                                             {
                                                 r.Id, r.State, r.StartedOn, r.FinishedOn, r.CharactersRead, r.CoverageAtFinish,
                                                 isCurrent = r.Id == currentReadId
                                             })
                                .ToList(),
               };
    }

    /// <summary>The titles' status and history as they are now, for the Undo of the change about to be made.</summary>
    private async Task<List<MediaListSnapshot>> TakeSnapshotsAsync(string userId, IReadOnlyCollection<int> deckIds)
    {
        var preferences = await userContext.UserDeckPreferences
                                           .AsNoTracking()
                                           .Where(p => p.UserId == userId && deckIds.Contains(p.DeckId))
                                           .Select(p => new { p.DeckId, p.Status, p.CurrentEntryId })
                                           .ToDictionaryAsync(p => p.DeckId);
        var entries = (await userContext.UserMediaListEntries
                                        .AsNoTracking()
                                        .Where(r => r.UserId == userId && deckIds.Contains(r.DeckId))
                                        .OrderBy(r => r.Id)
                                        .ToListAsync())
                      .GroupBy(r => r.DeckId)
                      .ToDictionary(g => g.Key, g => g.ToList());

        return deckIds.Distinct()
                      .Select(id => new MediaListSnapshot
                                    {
                                        DeckId = id,
                                        Status = preferences.GetValueOrDefault(id)?.Status ?? DeckStatus.None,
                                        Entries = (entries.GetValueOrDefault(id) ?? [])
                                                  .Select(r => new MediaListSnapshotEntry
                                                               {
                                                                   Id = r.Id, State = r.State, StartedOn = r.StartedOn, FinishedOn = r.FinishedOn,
                                                                   CharactersRead = r.CharactersRead, SeriesEntryId = r.SeriesEntryId,
                                                                   IsCurrent = r.Id == preferences.GetValueOrDefault(id)?.CurrentEntryId
                                                               })
                                                  .ToList()
                                    })
                      .ToList();
    }
}
