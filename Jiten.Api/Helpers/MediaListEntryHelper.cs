using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Helpers;

/// <summary>EntryId and the per-pass fields are null when the title has entries but none is current; LastCompletedOn is the latest dated completion.</summary>
public record MediaListEntrySummary(
    long? EntryId,
    MediaListEntryState? State,
    DateOnly? StartedOn,
    DateOnly? FinishedOn,
    int? CharactersRead,
    int CompletedCount,
    int EntryCount,
    int? UnitCount = null,
    int? CompletedUnits = null,
    int? VolumeCharacters = null,
    DateOnly? LastCompletedOn = null);

/// <summary>One completion of a volume or episode, in the order the entries were created.</summary>
public record VolumeCompletion(int DeckId, int VolumeCharacters, int? CharactersRead, long? SeriesEntryId);

public record SeriesTally(IReadOnlyDictionary<long, long> PassCharacters, long StandaloneCharacters, IReadOnlyList<int> StandaloneDeckIds);

public static class MediaListEntryHelper
{
    public const int MaxCharactersRead = 100_000_000;

    public const int MaxEntriesPerDeck = 100;

    public static readonly string HistoryFullMessage = $"This title's history is full ({MaxEntriesPerDeck} at most).";

    private static readonly DateOnly EarliestDate = new(1900, 1, 1);

    /// <summary>Dates are the user's calendar days, which can run a day ahead of UTC.</summary>
    public static bool IsAllowedDate(DateOnly date) => date >= EarliestDate && date <= DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);

    /// <summary>Visual novel routes, YouTube videos and game entries are not watched or read in order, so they get no unit count.</summary>
    private static readonly MediaType[] UntrackedUnitTypes = [MediaType.VisualNovel, MediaType.YouTube, MediaType.VideoGame];

    public static bool TracksUnits(MediaType mediaType) => !UntrackedUnitTypes.Contains(mediaType);

    /// <summary>The entry state a title's status keeps current, or null for a status with no pass of its own.</summary>
    public static MediaListEntryState? EntryStateFor(DeckStatus status) => status switch
    {
        DeckStatus.Ongoing => MediaListEntryState.InProgress,
        DeckStatus.Completed => MediaListEntryState.Completed,
        DeckStatus.Dropped => MediaListEntryState.Dropped,
        _ => null
    };

    public static DeckStatus StatusFor(MediaListEntryState state) => state switch
    {
        MediaListEntryState.InProgress => DeckStatus.Ongoing,
        MediaListEntryState.Completed => DeckStatus.Completed,
        _ => DeckStatus.Dropped
    };

    public static bool CanBeCurrent(DeckStatus status, MediaListEntryState state) => status switch
    {
        DeckStatus.Dropped or DeckStatus.Planning => state is MediaListEntryState.Dropped or MediaListEntryState.Completed,
        _ => EntryStateFor(status) == state
    };

    private static bool StartsAfterCompletion(UserDeckPreference preference, bool newEntry) =>
        preference.CurrentEntry is { State: MediaListEntryState.Completed } && (newEntry || preference.Status != DeckStatus.Completed);

    public static bool StartsEntry(UserDeckPreference preference, DeckStatus status, bool newEntry)
    {
        var entry = preference.CurrentEntry;
        return status switch
        {
            DeckStatus.Ongoing => entry == null || StartsAfterCompletion(preference, newEntry) ||
                                  (newEntry && entry.State == MediaListEntryState.Dropped),
            DeckStatus.Completed => entry == null ||
                                    (entry.State == MediaListEntryState.Dropped && preference.Status == DeckStatus.Planning),
            _ => false
        };
    }

    private static DateOnly? FinishDate(UserMediaListEntry entry, DateOnly? date) =>
        date is { } d && entry.StartedOn is { } s && d < s ? s : date;

    private static void Reopen(UserMediaListEntry entry, MediaListEntryState state)
    {
        if (entry.State == MediaListEntryState.Completed)
            entry.CoverageAtFinish = null;
        entry.State = state;
    }

    /// <summary>Mirrors a status change onto the current entry; call before setting Status. True when reading totals may have changed.</summary>
    /// <param name="undoCompletion">Dropping a Completed title turns its completion into a dropped pass; without it, dropping keeps the completion.</param>
    /// <param name="seriesPass">The series pass in progress the deck is a volume of; an entry created or completed now belongs to it.</param>
    public static bool ApplyStatus(UserDbContext userContext, UserDeckPreference preference, DeckStatus status, DateOnly? date,
                                   bool newEntry,
                                   int? deckCharacters = null, bool undoCompletion = false, UserMediaListEntry? seriesPass = null)
    {
        var entry = preference.CurrentEntry;
        var closedPass = StartsAfterCompletion(preference, newEntry);

        switch (status)
        {
            case DeckStatus.None:
                preference.CurrentEntry = null;
                preference.CurrentEntryId = null;
                return true;

            case DeckStatus.Planning:
                if (entry is not { State: MediaListEntryState.InProgress })
                    return false;

                if (entry.CharactersRead == null)
                {
                    userContext.UserMediaListEntries.Remove(entry);
                    preference.CurrentEntry = null;
                    preference.CurrentEntryId = null;
                    return false;
                }

                entry.State = MediaListEntryState.Dropped;
                entry.FinishedOn ??= FinishDate(entry, date);
                return false;

            case DeckStatus.Ongoing:
                if (entry == null || StartsEntry(preference, status, newEntry))
                {
                    preference.CurrentEntry = NewEntry(userContext, preference, MediaListEntryState.InProgress, startedOn: date,
                                                       finishedOn: null, seriesPass);
                    return false;
                }

                if (entry.State == MediaListEntryState.InProgress)
                    return false;

                var wasCompleted = entry.State == MediaListEntryState.Completed;
                Reopen(entry, MediaListEntryState.InProgress);
                entry.FinishedOn = null;
                return wasCompleted;

            case DeckStatus.Dropped:
                if (entry is { State: MediaListEntryState.InProgress } ||
                    (entry is { State: MediaListEntryState.Completed } && undoCompletion && !closedPass))
                {
                    var undone = entry.State == MediaListEntryState.Completed;
                    Reopen(entry, MediaListEntryState.Dropped);
                    entry.FinishedOn = FinishDate(entry, date);
                    return undone;
                }

                return false;

            case DeckStatus.Completed:
                if (entry is { State: MediaListEntryState.Completed })
                    return false;

                if (entry == null || StartsEntry(preference, status, newEntry))
                {
                    preference.CurrentEntry = NewEntry(userContext, preference, MediaListEntryState.Completed, startedOn: null,
                                                       finishedOn: date, seriesPass);
                    return true;
                }

                if (entry.CharactersRead is { } logged && deckCharacters is { } total && logged <= total)
                    entry.CharactersRead = null;

                Reopen(entry, MediaListEntryState.Completed);
                entry.FinishedOn = FinishDate(entry, date);
                if (seriesPass != null)
                    entry.SeriesEntry = seriesPass;
                return true;

            default:
                return false;
        }
    }

    public static void ClearStatus(UserDbContext userContext, UserDeckPreference preference)
    {
        preference.CurrentEntry = null;
        preference.CurrentEntryId = null;
        if (preference.IsFavourite || preference.IsIgnored)
            preference.Status = DeckStatus.None;
        else
            userContext.UserDeckPreferences.Remove(preference);
    }

    private static UserMediaListEntry NewEntry(UserDbContext userContext, UserDeckPreference preference, MediaListEntryState state,
                                               DateOnly? startedOn,
                                               DateOnly? finishedOn, UserMediaListEntry? seriesPass)
    {
        var entry = new UserMediaListEntry
                    {
                        UserId = preference.UserId, DeckId = preference.DeckId, State = state, StartedOn = startedOn,
                        FinishedOn = finishedOn, SeriesEntry = seriesPass
                    };
        userContext.UserMediaListEntries.Add(entry);
        return entry;
    }

    /// <summary>Adds a completion to a volume already finished on an earlier pass, so the series pass being read counts it.</summary>
    public static void CompleteForSeriesPass(UserDbContext userContext, UserDeckPreference preference, long seriesPassId, DateOnly? date)
    {
        var entry = NewEntry(userContext, preference, MediaListEntryState.Completed, startedOn: null, finishedOn: date, seriesPass: null);
        entry.SeriesEntryId = seriesPassId;
        preference.CurrentEntry = entry;
        preference.Status = DeckStatus.Completed;
    }

    /// <summary>Decks whose saved history already holds <see cref="MaxEntriesPerDeck"/> entries.</summary>
    public static async Task<HashSet<int>> FullHistoryDeckIdsAsync(UserDbContext userContext, string userId,
                                                                   IReadOnlyCollection<int> deckIds)
    {
        if (deckIds.Count == 0)
            return [];

        return (await userContext.UserMediaListEntries
                                 .Where(r => r.UserId == userId && deckIds.Contains(r.DeckId))
                                 .GroupBy(r => r.DeckId)
                                 .Where(g => g.Count() >= MaxEntriesPerDeck)
                                 .Select(g => g.Key)
                                 .ToListAsync()).ToHashSet();
    }

    /// <summary>For each volume among <paramref name="deckIds"/>, the tracked entry of its series' pass in progress, if there is one.</summary>
    public static async Task<Dictionary<int, UserMediaListEntry>> SeriesPassesAsync(UserDbContext userContext, JitenDbContext jitenContext,
                                                                                    string userId,
                                                                                    IReadOnlyCollection<int> deckIds)
    {
        if (deckIds.Count == 0)
            return [];

        var parentOf = await jitenContext.Decks.AsNoTracking()
                                         .Where(d => deckIds.Contains(d.DeckId) && d.ParentDeckId != null)
                                         .ToDictionaryAsync(d => d.DeckId, d => d.ParentDeckId!.Value);
        if (parentOf.Count == 0)
            return [];

        var seriesIds = parentOf.Values.Distinct().ToList();
        // A series changed earlier in the same request is only visible through the change tracker.
        var preferences = userContext.ChangeTracker.Entries<UserDeckPreference>()
                                     .Where(e => e.State != EntityState.Deleted)
                                     .Select(e => e.Entity)
                                     .Where(p => p.UserId == userId && seriesIds.Contains(p.DeckId))
                                     .ToList();
        var unloaded = seriesIds.Except(preferences.Select(p => p.DeckId)).ToList();
        if (unloaded.Count > 0)
            preferences.AddRange(await userContext.UserDeckPreferences
                                                  .Include(p => p.CurrentEntry)
                                                  .Where(p => p.UserId == userId && unloaded.Contains(p.DeckId))
                                                  .ToListAsync());

        var missingEntries = preferences.Where(p => p.CurrentEntry == null && p.CurrentEntryId != null).Select(p => p.CurrentEntryId!.Value)
                                        .ToList();
        if (missingEntries.Count > 0)
            await userContext.UserMediaListEntries.Where(r => missingEntries.Contains(r.Id)).LoadAsync();

        var passBySeries = preferences.Where(p => p.CurrentEntry is { State: MediaListEntryState.InProgress })
                                      .ToDictionary(p => p.DeckId, p => p.CurrentEntry!);
        return parentOf.Where(kv => passBySeries.ContainsKey(kv.Value)).ToDictionary(kv => kv.Key, kv => passBySeries[kv.Value]);
    }

    /// <summary>Loads and removes every entry of the given decks; the deletion happens on the caller's SaveChanges.</summary>
    public static async Task RemoveReadsAsync(UserDbContext userContext, string userId, IReadOnlyCollection<int> deckIds)
    {
        if (deckIds.Count == 0)
            return;

        var entries = await userContext.UserMediaListEntries
                                       .Where(r => r.UserId == userId && deckIds.Contains(r.DeckId))
                                       .ToListAsync();

        foreach (var preference in userContext.ChangeTracker.Entries<UserDeckPreference>()
                                              .Select(e => e.Entity)
                                              .Where(p => p.UserId == userId && deckIds.Contains(p.DeckId)))
        {
            preference.CurrentEntry = null;
            preference.CurrentEntryId = null;
        }

        userContext.UserMediaListEntries.RemoveRange(entries);
    }

    /// <summary><paramref name="seriesIds"/> limits the volume lookup to decks known to have children; null checks every deck.</summary>
    public static async Task<Dictionary<int, MediaListEntrySummary>> BuildSummariesAsync(
        UserDbContext userContext, JitenDbContext jitenContext,
        string userId, IReadOnlyCollection<int> deckIds,
        IReadOnlyCollection<int>? seriesIds = null)
    {
        if (deckIds.Count == 0)
            return new Dictionary<int, MediaListEntrySummary>();

        var rows = await userContext.UserMediaListEntries
                                    .AsNoTracking()
                                    .Where(r => r.UserId == userId && deckIds.Contains(r.DeckId))
                                    .Select(r => new
                                                 {
                                                     Entry = r,
                                                     IsCurrent =
                                                         userContext.UserDeckPreferences.Any(p => p.UserId == userId &&
                                                                                                 p.CurrentEntryId == r.Id)
                                                 })
                                    .ToListAsync();

        var currentIds = rows.Where(r => r.IsCurrent).Select(r => r.Entry.Id).ToHashSet();
        var summaries = rows.Select(r => r.Entry)
                            .GroupBy(r => r.DeckId)
                            .ToDictionary(g => g.Key, g => Summarise(g.ToList(), currentIds));

        // A series planned or carried over from before entries existed still shows how far its volumes got.
        var bareSeries = (seriesIds ?? []).Where(id => !summaries.ContainsKey(id)).ToHashSet();
        foreach (var id in bareSeries)
            summaries[id] = EmptySummary;

        summaries = await WithSeriesProgressAsync(userContext, jitenContext, userId, summaries, seriesIds);
        foreach (var id in bareSeries.Where(id => summaries[id] is { UnitCount: null, VolumeCharacters: null }))
            summaries.Remove(id);
        return summaries;
    }

    private static readonly MediaListEntrySummary EmptySummary = new(null, null, null, null, null, 0, 0);

    /// <summary>
    /// Adds a series' progress through its volumes or episodes: how many are done on its current pass (for media read or watched in order) and
    /// how many characters that pass stands for, which the series uses unless it has a typed count of its own.
    /// </summary>
    public static async Task<Dictionary<int, MediaListEntrySummary>> WithSeriesProgressAsync(
        UserDbContext userContext, JitenDbContext jitenContext,
        string userId, Dictionary<int, MediaListEntrySummary> summaries,
        IReadOnlyCollection<int>? seriesIds = null)
    {
        var parentIds = (seriesIds ?? summaries.Keys).Where(summaries.ContainsKey).Distinct().ToList();
        if (parentIds.Count == 0)
            return summaries;

        var children = await jitenContext.Decks
                                         .AsNoTracking()
                                         .Where(d => d.ParentDeckId != null && parentIds.Contains(d.ParentDeckId.Value))
                                         .Select(d => new
                                                      {
                                                          d.DeckId, ParentDeckId = d.ParentDeckId!.Value, d.CharacterCount,
                                                          ParentMediaType = d.ParentDeck!.MediaType,
                                                          ParentCharacters = d.ParentDeck.CharacterCount
                                                      })
                                         .ToListAsync();
        if (children.Count == 0)
            return summaries;

        var childIds = children.Select(c => c.DeckId).ToList();
        var entriesByChild = (await userContext.UserMediaListEntries
                                               .AsNoTracking()
                                               .Where(r => r.UserId == userId && childIds.Contains(r.DeckId))
                                               .OrderBy(r => r.Id)
                                               .Select(r => new { r.DeckId, r.State, r.CharactersRead, r.SeriesEntryId })
                                               .ToListAsync())
                             .GroupBy(r => r.DeckId)
                             .ToDictionary(g => g.Key, g => g.ToList());
        var completedSeriesIds = parentIds.Where(id => summaries[id].State == MediaListEntryState.Completed).ToList();
        var completedPasses = completedSeriesIds.Count == 0
            ? []
            : (await userContext.UserMediaListEntries
                                .AsNoTracking()
                                .Where(r => r.UserId == userId && completedSeriesIds.Contains(r.DeckId) &&
                                            r.State == MediaListEntryState.Completed)
                                .OrderBy(r => r.Id)
                                .Select(r => new { r.DeckId, r.Id })
                                .ToListAsync())
              .GroupBy(r => r.DeckId)
              .ToDictionary(g => g.Key, g => g.Select(r => r.Id).ToList());
        var rereadPasses = await RereadPassIdsAsync(userContext, userId,
                                                    parentIds.Where(id => summaries[id] is
                                                                        { State: MediaListEntryState.InProgress, CompletedCount: > 0 })
                                                             .ToList());
        var completedByStatus = (await userContext.UserDeckPreferences
                                                  .AsNoTracking()
                                                  .Where(p => p.UserId == userId && childIds.Contains(p.DeckId) &&
                                                              p.Status == DeckStatus.Completed)
                                                  .Select(p => p.DeckId)
                                                  .ToListAsync()).ToHashSet();

        foreach (var group in children.GroupBy(c => c.ParentDeckId))
        {
            var summary = summaries[group.Key];
            var first = group.First();
            var volumeCharacters = group.ToDictionary(c => c.DeckId, c => c.CharacterCount);
            var entries = group.SelectMany(c => entriesByChild.GetValueOrDefault(c.DeckId) ?? []).ToList();
            var completions = entries.Where(r => r.State == MediaListEntryState.Completed).ToList();
            HashSet<int> done;
            long characters;

            if (summary is { State: MediaListEntryState.Completed, EntryId: { } currentPass })
            {
                var tally = TallySeries(first.ParentCharacters, completedPasses.GetValueOrDefault(group.Key) ?? [currentPass],
                                        completions.Select(r => new VolumeCompletion(r.DeckId, volumeCharacters[r.DeckId], r.CharactersRead,
                                                                                     r.SeriesEntryId))
                                                   .ToList());
                characters = tally.PassCharacters.GetValueOrDefault(currentPass);
                done = completions.Select(r => r.DeckId).Concat(group.Select(c => c.DeckId).Where(completedByStatus.Contains)).ToHashSet();
            }
            else if (rereadPasses.TryGetValue(group.Key, out var pass))
            {
                var thisPass = entries.Where(r => r.SeriesEntryId == pass).ToList();
                done = thisPass.Where(r => r.State == MediaListEntryState.Completed).Select(r => r.DeckId).ToHashSet();
                characters = thisPass.Sum(r => r.State == MediaListEntryState.Completed
                                              ? (long)(r.CharactersRead ?? volumeCharacters[r.DeckId])
                                              : r.CharactersRead ?? 0);
            }
            else
            {
                done = completions.Select(r => r.DeckId).Concat(group.Select(c => c.DeckId).Where(completedByStatus.Contains)).ToHashSet();
                characters = completions.Sum(r => (long)(r.CharactersRead ?? volumeCharacters[r.DeckId])) +
                             done.Where(id => completions.All(r => r.DeckId != id)).Sum(id => (long)volumeCharacters[id]) +
                             entries.Where(r => r.State != MediaListEntryState.Completed).Sum(r => (long)(r.CharactersRead ?? 0));
            }

            var tracksUnits = TracksUnits(first.ParentMediaType);
            summaries[group.Key] = summary with
                                   {
                                       UnitCount = tracksUnits ? group.Count() : null, CompletedUnits = tracksUnits ? done.Count : null,
                                       VolumeCharacters = characters > 0 ? (int)Math.Min(characters, MaxCharactersRead) : null
                                   };
        }

        return summaries;
    }

    /// <summary>Each completed series pass covers one completion per volume: the one linked to it, else the oldest unlinked one no earlier pass took; the rest are reads of their own.</summary>
    public static SeriesTally TallySeries(int seriesCharacters, IReadOnlyList<long> completedPassIds,
                                          IReadOnlyList<VolumeCompletion> completions)
    {
        var passCharacters = completedPassIds.Distinct().ToDictionary(id => id, _ => (long)seriesCharacters);
        var covered = new HashSet<(long Pass, int DeckId)>();
        var unlinked = new List<VolumeCompletion>();
        var standalone = new List<VolumeCompletion>();

        static long Difference(VolumeCompletion c) => (c.CharactersRead ?? c.VolumeCharacters) - (long)c.VolumeCharacters;

        foreach (var completion in completions)
        {
            if (completion.SeriesEntryId is { } link && passCharacters.ContainsKey(link) && covered.Add((link, completion.DeckId)))
                passCharacters[link] += Difference(completion);
            else if (completion.SeriesEntryId == null)
                unlinked.Add(completion);
            else
                standalone.Add(completion);
        }

        foreach (var completion in unlinked)
        {
            var pass = completedPassIds.Select(id => (long?)id).FirstOrDefault(id => !covered.Contains((id!.Value, completion.DeckId)));
            if (pass is { } p)
            {
                covered.Add((p, completion.DeckId));
                passCharacters[p] += Difference(completion);
            }
            else
                standalone.Add(completion);
        }

        return new SeriesTally(passCharacters.ToDictionary(kv => kv.Key, kv => Math.Max(kv.Value, 0)),
                               standalone.Sum(c => (long)(c.CharactersRead ?? c.VolumeCharacters)),
                               standalone.Select(c => c.DeckId).ToList());
    }

    public static MediaListEntrySummary Summarise(IReadOnlyCollection<UserMediaListEntry> entries, IReadOnlySet<long> currentIds)
    {
        var current = entries.FirstOrDefault(r => currentIds.Contains(r.Id));
        var completed = entries.Count(r => r.State == MediaListEntryState.Completed);
        var lastFinished = entries.Where(r => r.State == MediaListEntryState.Completed).Max(r => r.FinishedOn);

        if (current == null)
            return new MediaListEntrySummary(null, null, null, lastFinished, null, completed, entries.Count, LastCompletedOn: lastFinished);

        var finishedOn = current.State == MediaListEntryState.InProgress ? lastFinished : current.FinishedOn;

        return new MediaListEntrySummary(current.Id, current.State, current.StartedOn, finishedOn, current.CharactersRead, completed,
                                         entries.Count,
                                         LastCompletedOn: lastFinished);
    }

    /// <summary>The current pass of each series being read again: in progress, with a completed pass before it.</summary>
    public static async Task<Dictionary<int, long>> RereadPassIdsAsync(UserDbContext userContext, string userId,
                                                                       IReadOnlyCollection<int> seriesIds)
    {
        if (seriesIds.Count == 0)
            return [];

        return await userContext.UserDeckPreferences
                                .AsNoTracking()
                                .Where(p => p.UserId == userId && seriesIds.Contains(p.DeckId) &&
                                            p.CurrentEntry!.State == MediaListEntryState.InProgress &&
                                            userContext.UserMediaListEntries.Any(r => r.UserId == userId && r.DeckId == p.DeckId &&
                                                                                      r.State == MediaListEntryState.Completed))
                                .ToDictionaryAsync(p => p.DeckId, p => p.CurrentEntryId!.Value);
    }

    /// <summary>Units done on the series' current pass: finished at least once, or during a reread, completed on that pass.</summary>
    public static async Task<HashSet<int>> CompletedUnitIdsAsync(UserDbContext userContext, string userId, int seriesId,
                                                                 IReadOnlyCollection<int> unitIds)
    {
        if (!(await RereadPassIdsAsync(userContext, userId, [seriesId])).TryGetValue(seriesId, out var pass))
            return await CompletedDeckIdsAsync(userContext, userId, unitIds);

        return (await userContext.UserMediaListEntries
                                 .Where(r => r.UserId == userId && unitIds.Contains(r.DeckId) && r.State == MediaListEntryState.Completed &&
                                             r.SeriesEntryId == pass)
                                 .Select(r => r.DeckId)
                                 .Distinct()
                                 .ToListAsync()).ToHashSet();
    }

    /// <summary>Decks the user has finished at least once: marked Completed, or holding a completed entry while being read again.</summary>
    public static async Task<HashSet<int>> CompletedDeckIdsAsync(UserDbContext userContext, string userId, IReadOnlyCollection<int> deckIds)
    {
        if (deckIds.Count == 0)
            return [];

        return (await CompletedDeckIds(userContext, userId, deckIds).ToListAsync()).ToHashSet();
    }

    /// <summary>Same rule as <see cref="CompletedDeckIdsAsync"/>, composable into a larger query; null <paramref name="deckIds"/> covers every deck.</summary>
    public static IQueryable<int> CompletedDeckIds(UserDbContext userContext, string userId, IReadOnlyCollection<int>? deckIds = null)
    {
        var byStatus = userContext.UserDeckPreferences.Where(p => p.UserId == userId && p.Status == DeckStatus.Completed)
                                  .Select(p => p.DeckId);
        var byEntry = userContext.UserMediaListEntries.Where(r => r.UserId == userId && r.State == MediaListEntryState.Completed)
                                 .Select(r => r.DeckId);
        if (deckIds != null)
        {
            byStatus = byStatus.Where(id => deckIds.Contains(id));
            byEntry = byEntry.Where(id => deckIds.Contains(id));
        }

        return byStatus.Union(byEntry);
    }

    /// <summary>A Completed or Dropped series is the user's call and only follows its unit count when set on the series itself; None or Planning opens once a volume is read.</summary>
    public static DeckStatus ResolveSeriesStatus(DeckStatus seriesStatus, bool allVolumesCompleted, DeckStatus volumeStatus,
                                                 bool setOnSeries) =>
        seriesStatus switch
        {
            DeckStatus.Completed when setOnSeries && !allVolumesCompleted => DeckStatus.Ongoing,
            DeckStatus.Dropped when setOnSeries && volumeStatus is DeckStatus.Completed or DeckStatus.Ongoing => DeckStatus.Ongoing,
            DeckStatus.None or DeckStatus.Planning
                when allVolumesCompleted || volumeStatus is DeckStatus.Completed or DeckStatus.Ongoing or DeckStatus.Dropped => DeckStatus
                    .Ongoing,
            _ => seriesStatus
        };

    /// <summary>Fills <see cref="DeckDto.ListEntry"/> on the decks the viewer has a status on.</summary>
    public static async Task ApplyListEntriesAsync(UserDbContext userContext, JitenDbContext jitenContext, string userId,
                                                   IReadOnlyCollection<DeckDto> decks)
    {
        var tracked = decks.Where(d => d.Status is not null and not DeckStatus.None).ToList();
        if (tracked.Count == 0)
            return;

        var summaries = await BuildSummariesAsync(userContext, jitenContext, userId, tracked.Select(d => d.DeckId).Distinct().ToList(),
                                                  tracked.Where(d => d.ChildrenDeckCount > 0).Select(d => d.DeckId).ToList());
        foreach (var deck in tracked)
            deck.ListEntry = summaries.GetValueOrDefault(deck.DeckId);
    }
}