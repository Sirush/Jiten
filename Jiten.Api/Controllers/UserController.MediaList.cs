using Hangfire;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Helpers;
using Jiten.Api.Jobs;
using Jiten.Api.Services.ExternalMediaList;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

public partial class UserController
{
    private const int MaxImportApplyEntries = 20000;

    private const int MaxBulkPreferenceDecks = 500;

    private const int MaxProgressSubdecksPerDeck = 200;

    private const int MaxProgressSubdecksPerRequest = 5000;

    private static readonly Regex AnilistIdRegex = new(@"anilist\.co/(?:anime|manga)/(\d+)", RegexOptions.Compiled);

    private static readonly Regex VndbIdRegex = new(@"vndb\.org/(v\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Fetches a public external media list and matches it against the catalogue
    /// </summary>
    [HttpPost("media-list/import/preview")]
    [EnableRateLimiting("external-fetch")]
    [SwaggerOperation(Summary = "Preview an external media list import")]
    public async Task<IResult> PreviewMediaListImport([FromBody] MediaListImportPreviewRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        if (!Enum.TryParse<ExternalListProvider>(request.Provider, ignoreCase: true, out var provider))
            return Results.BadRequest(new { message = "Unknown provider." });

        var username = ExternalListInput.Normalize(provider, request.Username);
        if (username.Length is 0 or > 100)
            return Results.BadRequest(new { message = "Enter a username or the URL of your profile." });

        var fetch = await externalListClient.FetchListAsync(provider, username, HttpContext.RequestAborted);
        if (fetch.Error != null)
            return Results.BadRequest(new { message = fetch.Error });

        var (linkType, idRegex) = provider == ExternalListProvider.Anilist
            ? (LinkType.Anilist, AnilistIdRegex)
            : (LinkType.Vndb, VndbIdRegex);

        var links = await jitenContext.Decks
                                      .AsNoTracking()
                                      .Where(d => d.ParentDeckId == null)
                                      .SelectMany(d => d.Links)
                                      .Where(l => l.LinkType == linkType)
                                      .Select(l => new { l.Url, l.DeckId })
                                      .ToListAsync();

        var deckByExternalId = new Dictionary<string, int>();
        foreach (var link in links)
        {
            var match = idRegex.Match(link.Url);
            if (match.Success)
                deckByExternalId.TryAdd(match.Groups[1].Value, link.DeckId);
        }

        var matchedByDeck = new Dictionary<int, ExternalListEntry>();
        var unmatched = new List<ExternalListEntry>();
        foreach (var entry in fetch.Entries)
        {
            if (!deckByExternalId.TryGetValue(entry.ExternalId, out var deckId))
            {
                unmatched.Add(entry);
                continue;
            }

            // Several external entries can share one deck (e.g. seasons linked to the same parent); strongest status wins.
            if (!matchedByDeck.TryGetValue(deckId, out var existing) || entry.MappedStatus.Rank() > existing.MappedStatus.Rank())
                matchedByDeck[deckId] = entry;
        }

        var matchedDeckIds = matchedByDeck.Keys.ToList();

        var decks = await jitenContext.Decks
                                      .AsNoTracking()
                                      .Where(d => matchedDeckIds.Contains(d.DeckId))
                                      .Select(d => new { d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.CoverName, d.MediaType })
                                      .ToDictionaryAsync(d => d.DeckId);

        var preferences = await userContext.UserDeckPreferences
                                           .AsNoTracking()
                                           .Where(p => p.UserId == userId && matchedDeckIds.Contains(p.DeckId))
                                           .Select(p => new { p.DeckId, p.Status, p.IsIgnored })
                                           .ToDictionaryAsync(p => p.DeckId);

        var subdeckCounts = await jitenContext.Decks
                                              .AsNoTracking()
                                              .Where(d => d.ParentDeckId != null && matchedDeckIds.Contains(d.ParentDeckId.Value))
                                              .GroupBy(d => d.ParentDeckId!.Value)
                                              .Select(g => new { ParentDeckId = g.Key, Count = g.Count() })
                                              .ToDictionaryAsync(g => g.ParentDeckId, g => g.Count);

        var matched = matchedByDeck
                      .Where(kv => decks.ContainsKey(kv.Key))
                      .Select(kv =>
                      {
                          var deck = decks[kv.Key];
                          var pref = preferences.GetValueOrDefault(kv.Key);
                          var currentStatus = pref != null && pref.Status != DeckStatus.None ? pref.Status : (DeckStatus?)null;
                          return new
                                 {
                                     deckId = deck.DeckId,
                                     originalTitle = deck.OriginalTitle,
                                     romajiTitle = deck.RomajiTitle,
                                     englishTitle = deck.EnglishTitle,
                                     coverName = deck.CoverName,
                                     mediaType = deck.MediaType,
                                     externalStatus = kv.Value.ExternalStatus,
                                     mappedStatus = kv.Value.MappedStatus,
                                     finishedAt = kv.Value.FinishedAt,
                                     startedOn = kv.Value.StartedOn,
                                     completedOn = kv.Value.CompletedOn,
                                     repeatCount = kv.Value.RepeatCount,
                                     progress = kv.Value.Progress,
                                     subdeckCount = subdeckCounts.TryGetValue(deck.DeckId, out var subdecks) ? subdecks : (int?)null,
                                     currentStatus,
                                     isIgnored = pref?.IsIgnored ?? false,
                                 };
                      })
                      .OrderBy(m => m.originalTitle)
                      .ToList();

        var conflicts = matched.Count(m => !m.isIgnored && m.currentStatus != null && m.currentStatus != m.mappedStatus);

        logger.LogInformation("Media list import preview: UserId={UserId}, Provider={Provider}, Fetched={Fetched}, Matched={Matched}",
                              userId, provider, fetch.Entries.Count, matched.Count);

        return Results.Ok(new
                          {
                              username,
                              matched,
                              unmatched = unmatched
                                          .Select(u => new { title = u.Title, url = u.Url, externalStatus = u.ExternalStatus, mappedStatus = u.MappedStatus })
                                          .OrderBy(u => u.title)
                                          .ToList(),
                              counts = new { total = fetch.Entries.Count, matched = matched.Count, unmatched = unmatched.Count, conflicts },
                          });
    }

    /// <summary>
    /// Applies reviewed import rows
    /// </summary>
    [HttpPost("media-list/import/apply")]
    [SwaggerOperation(Summary = "Apply an external media list import")]
    public async Task<IResult> ApplyMediaListImport([FromBody] MediaListImportApplyRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        if (request.Entries.Count == 0)
            return Results.BadRequest(new { message = "Nothing to import." });
        if (request.Entries.Count > MaxImportApplyEntries)
            return Results.BadRequest(new { message = $"Too many entries in one request (max {MaxImportApplyEntries})." });

        var deduped = request.Entries
                             .Where(e => Enum.IsDefined(e.Status) && e.Status != DeckStatus.None)
                             .GroupBy(e => e.DeckId)
                             .Select(g => g.OrderByDescending(e => e.Status.Rank()).First())
                             .ToList();

        var requestedIds = deduped.Select(e => e.DeckId).ToList();
        var validIds = (await jitenContext.Decks
                                          .AsNoTracking()
                                          .Where(d => requestedIds.Contains(d.DeckId) && d.ParentDeckId == null)
                                          .Select(d => d.DeckId)
                                          .ToListAsync()).ToHashSet();

        var valid = deduped.Where(e => validIds.Contains(e.DeckId)).ToList();
        var entries = valid.Select(e => (e.DeckId, e.Status)).ToList();
        var invalid = request.Entries.Count - entries.Count;

        var subdecks = await ResolveProgressSubdecksAsync(valid.Where(e => e.Volumes is not { Count: > 0 }).ToList());
        var (volumes, oversizedVolumes) = await ResolveImportedVolumesAsync(valid, MaxProgressSubdecksPerRequest - subdecks.Keep.Count - subdecks.Overwrite.Count);
        var oversizedDecks = subdecks.OversizedDecks + oversizedVolumes;

        var historyDeckIds = valid.Select(e => e.DeckId).Concat(volumes.SelectMany(v => v.Volumes.Select(e => e.DeckId))).ToList();
        var decksWithHistory = (await userContext.UserMediaListEntries
                                                 .Where(r => r.UserId == userId && historyDeckIds.Contains(r.DeckId))
                                                 .Select(r => r.DeckId)
                                                 .Distinct()
                                                 .ToListAsync()).ToHashSet();

        var outcome = await DeckPreferenceHelper.ApplyStatusesAsync(userContext, jitenContext, userId, entries,
                                                                    request.OverwriteExisting, skipIgnored: true, newEntry: true);

        var importedRows = new Dictionary<ImportedMediaListEntry, UserMediaListEntry>(ReferenceEqualityComparer.Instance);
        var historyImported = await ApplyImportedHistoryAsync(userId, valid, outcome.Preferences, decksWithHistory, importedRows);
        var totalsChanged = outcome.TotalsChanged || historyImported > 0;
        var subdecksCompleted = 0;
        var favourited = 0;

        foreach (var entry in valid.Where(e => e.IsFavourite))
        {
            if (!outcome.Preferences.TryGetValue(entry.DeckId, out var preference) || preference.IsIgnored || preference.IsFavourite)
                continue;

            preference.IsFavourite = true;
            favourited++;
        }

        foreach (var (children, overwrite) in new[] { (subdecks.Keep, false), (subdecks.Overwrite, true) })
        {
            if (children.Count == 0)
                continue;

            var childOutcome = await DeckPreferenceHelper.ApplyStatusesAsync(userContext, jitenContext, userId, children, overwrite, skipIgnored: true,
                                                                             newEntry: true);
            subdecksCompleted += childOutcome.Added + childOutcome.Updated;
            totalsChanged |= childOutcome.TotalsChanged;
        }

        foreach (var (series, volumeEntries, overwrite) in volumes)
        {
            var volumeOutcome = await DeckPreferenceHelper.ApplyStatusesAsync(userContext, jitenContext, userId,
                                                                              volumeEntries.Select(v => (v.DeckId, v.Status)).ToList(), overwrite,
                                                                              skipIgnored: true, newEntry: true);

            // A volume row names the series pass it was read under by its position in the series' own exported history.
            UserMediaListEntry? SeriesEntryAt(int? index) =>
                index is { } i && series.History is { } seriesHistory && i >= 0 && i < seriesHistory.Count
                    ? importedRows.GetValueOrDefault(seriesHistory[i])
                    : null;

            var volumeHistory = await ApplyImportedHistoryAsync(userId, volumeEntries, volumeOutcome.Preferences, decksWithHistory, importedRows,
                                                                SeriesEntryAt);
            subdecksCompleted += volumeOutcome.Added + volumeOutcome.Updated;
            totalsChanged |= volumeOutcome.TotalsChanged || volumeHistory > 0;
        }

        await userContext.SaveChangesAsync();
        await smartDeckDirty.MarkDirty(userId);

        if (totalsChanged)
            backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserAccomplishments(userId));

        logger.LogInformation("Media list import applied: UserId={UserId}, Added={Added}, Updated={Updated}, Skipped={Skipped}, " +
                              "Favourited={Favourited}, Subdecks={Subdecks}, OversizedDecks={OversizedDecks}",
                              userId, outcome.Added, outcome.Updated, outcome.SkippedIgnored + outcome.SkippedExisting + invalid,
                              favourited, subdecksCompleted, oversizedDecks);

        return Results.Ok(new
                          {
                              added = outcome.Added,
                              updated = outcome.Updated,
                              unchanged = outcome.Unchanged,
                              skippedIgnored = outcome.SkippedIgnored,
                              skippedExisting = outcome.SkippedExisting,
                              skippedFull = outcome.SkippedFull,
                              invalid,
                              favourited,
                              subdecksCompleted,
                              oversizedDecks,
                          });
    }

    /// <summary>Volume rows of a Jiten export, kept to the children of the title they were exported under and to the unit progress limits.</summary>
    private async Task<(List<(MediaListImportEntry Series, List<MediaListImportEntry> Volumes, bool Overwrite)> Volumes, int OversizedDecks)>
        ResolveImportedVolumesAsync(IReadOnlyCollection<MediaListImportEntry> entries, int budget)
    {
        var withVolumes = entries.Where(e => e.Volumes is { Count: > 0 }).ToList();
        if (withVolumes.Count == 0)
            return ([], 0);

        var rootIds = withVolumes.Select(e => e.DeckId).ToList();
        var parentOf = await jitenContext.Decks.AsNoTracking()
                                         .Where(d => d.ParentDeckId != null && rootIds.Contains(d.ParentDeckId.Value))
                                         .ToDictionaryAsync(d => d.DeckId, d => d.ParentDeckId!.Value);

        var result = new List<(MediaListImportEntry, List<MediaListImportEntry>, bool)>();
        var oversized = 0;
        foreach (var entry in withVolumes)
        {
            var volumes = entry.Volumes!.Where(v => parentOf.GetValueOrDefault(v.DeckId) == entry.DeckId && Enum.IsDefined(v.Status) &&
                                                    v.Status != DeckStatus.None)
                               .DistinctBy(v => v.DeckId)
                               .Select(v => new MediaListImportEntry { DeckId = v.DeckId, Status = v.Status, History = v.History })
                               .ToList();
            if (volumes.Count > MaxProgressSubdecksPerDeck)
            {
                oversized++;
                continue;
            }

            volumes = volumes.Take(Math.Max(budget, 0)).ToList();
            budget -= volumes.Count;
            if (volumes.Count > 0)
                result.Add((entry, volumes, entry.OverwriteSubdecks));
        }

        return (result, oversized);
    }

    /// <summary>Known states only, at most one pass in progress (the flagged one, else the last), and no more than a full history.</summary>
    private static List<ImportedMediaListEntry> ImportableRows(IReadOnlyCollection<ImportedMediaListEntry>? history)
    {
        var rows = history?.Where(r => Enum.IsDefined(r.State)).ToList() ?? [];
        var inProgress = rows.Where(r => r.State == MediaListEntryState.InProgress).ToList();
        if (inProgress.Count > 1)
        {
            var kept = inProgress.LastOrDefault(r => r.IsCurrent) ?? inProgress[^1];
            rows = rows.Where(r => r.State != MediaListEntryState.InProgress || ReferenceEquals(r, kept)).ToList();
        }

        return rows.Take(MediaListEntryHelper.MaxEntriesPerDeck).ToList();
    }

    /// <summary>Fills the entries the status import just opened; a title that already had history keeps it untouched, so re-importing never duplicates.</summary>
    /// <param name="importedRows">Collects every row turned into an entry, so volume rows can find the series entry they point at.</param>
    /// <param name="seriesEntryAt">Resolves a volume row's link to a series row, for titles imported as volumes of a series.</param>
    private async Task<int> ApplyImportedHistoryAsync(string userId, IReadOnlyCollection<MediaListImportEntry> entries,
                                                    IReadOnlyDictionary<int, UserDeckPreference> preferences, IReadOnlySet<int> decksWithHistory,
                                                    Dictionary<ImportedMediaListEntry, UserMediaListEntry> importedRows,
                                                    Func<int?, UserMediaListEntry?>? seriesEntryAt = null)
    {
        var candidates = entries
                         .Where(e => !decksWithHistory.Contains(e.DeckId) &&
                                     preferences.TryGetValue(e.DeckId, out var p) && !p.IsIgnored && p.Status == e.Status)
                         .ToList();
        if (candidates.Count == 0)
            return 0;

        static DateOnly? Clean(DateOnly? date) => date is { } d && MediaListEntryHelper.IsAllowedDate(d) ? d : null;
        static int? Characters(int? value) => value is > 0 and <= MediaListEntryHelper.MaxCharactersRead ? value : null;
        // Sources accept a start after the finish; the finish date is the one totals and sorting rely on, so the start gives way.
        static DateOnly? StartBefore(DateOnly? startedOn, DateOnly? finishedOn) => startedOn > finishedOn ? null : startedOn;

        var imported = 0;
        foreach (var entry in candidates)
        {
            var preference = preferences[entry.DeckId];
            var current = preference.CurrentEntry;

            var rows = ImportableRows(entry.History);
            if (rows.Count > 0)
            {
                var expectedState = MediaListEntryHelper.EntryStateFor(entry.Status);
                UserMediaListEntry? flagged = null, latestMatching = null;
                foreach (var item in rows)
                {
                    var finishedOn = item.State == MediaListEntryState.InProgress ? null : Clean(item.FinishedOn);
                    var historyEntry = new UserMediaListEntry
                                       {
                                           UserId = userId, DeckId = entry.DeckId, State = item.State, StartedOn = StartBefore(Clean(item.StartedOn), finishedOn),
                                           FinishedOn = finishedOn, CharactersRead = Characters(item.CharactersRead),
                                           SeriesEntry = seriesEntryAt?.Invoke(item.SeriesEntry)
                                       };
                    userContext.UserMediaListEntries.Add(historyEntry);
                    importedRows[item] = historyEntry;
                    imported++;

                    if (item.IsCurrent && MediaListEntryHelper.CanBeCurrent(entry.Status, item.State))
                        flagged = historyEntry;
                    if (expectedState == item.State)
                        latestMatching = historyEntry;
                }

                // Status changes act on the current pass, so it must be one the status can point at; with none, the pass the import opened stays.
                var chosen = flagged ?? latestMatching;
                if (expectedState != null && chosen == null)
                    continue;

                if (current != null)
                    userContext.UserMediaListEntries.Remove(current);
                preference.CurrentEntry = chosen;
                continue;
            }

            var repeats = Math.Min(entry.RepeatCount ?? 0, MediaListEntryHelper.MaxEntriesPerDeck - 1);
            // A finished title dropped or planned for a reread: the source's dates and count describe its last completion.
            var describesLastCompletion = repeats > 0 && entry.Status is DeckStatus.Dropped or DeckStatus.Planning;

            // Dropping a title records no pass of its own, but a source that dates the drop or counts its progress describes one.
            if (current == null && entry.Status == DeckStatus.Dropped && !describesLastCompletion &&
                ((Clean(entry.StartedOn) ?? Clean(entry.FinishedOn)) != null || Characters(entry.CharactersRead) != null))
            {
                current = new UserMediaListEntry { UserId = userId, DeckId = entry.DeckId, State = MediaListEntryState.Dropped };
                userContext.UserMediaListEntries.Add(current);
                preference.CurrentEntry = current;
            }

            if (current != null)
            {
                if (current.State != MediaListEntryState.InProgress)
                    current.FinishedOn ??= Clean(entry.FinishedOn);
                current.StartedOn ??= StartBefore(Clean(entry.StartedOn), current.FinishedOn);
                current.CharactersRead ??= Characters(entry.CharactersRead);
                imported++;
            }

            if (repeats == 0)
                continue;

            UserMediaListEntry? lastRepeat = null;
            for (var i = 0; i < repeats; i++)
            {
                lastRepeat = new UserMediaListEntry { UserId = userId, DeckId = entry.DeckId, State = MediaListEntryState.Completed };
                userContext.UserMediaListEntries.Add(lastRepeat);
                imported++;
            }

            if (describesLastCompletion)
            {
                lastRepeat!.FinishedOn = Clean(entry.FinishedOn);
                lastRepeat.StartedOn = StartBefore(Clean(entry.StartedOn), lastRepeat.FinishedOn);
                lastRepeat.CharactersRead = Characters(entry.CharactersRead);
                // Kept current like a completion is when the title is dropped or planned for a reread after finishing it.
                preference.CurrentEntry ??= lastRepeat;
            }
            else if (entry.Status.IsInProgress())
                lastRepeat!.FinishedOn = Clean(entry.FinishedOn);
        }

        return imported;
    }

    private sealed record ProgressSubdecks(
        List<(int DeckId, DeckStatus Status)> Keep,
        List<(int DeckId, DeckStatus Status)> Overwrite,
        int OversizedDecks);

    /// <summary>Turns per-entry unit progress into the subdecks it covers</summary>
    private async Task<ProgressSubdecks> ResolveProgressSubdecksAsync(IReadOnlyCollection<MediaListImportEntry> entries)
    {
        // A Completed parent already counts every unit, so it expands to nothing.
        var expandable = entries
                         .Where(e => e.Progress is > 0 && e.Status != DeckStatus.Completed)
                         .ToDictionary(e => e.DeckId);

        if (expandable.Count == 0)
            return new ProgressSubdecks([], [], 0);

        var parentIds = expandable.Keys.ToList();
        var children = await jitenContext.Decks
                                         .AsNoTracking()
                                         .Where(d => d.ParentDeckId != null && parentIds.Contains(d.ParentDeckId.Value))
                                         .Select(d => new { d.DeckId, ParentDeckId = d.ParentDeckId!.Value, d.DeckOrder })
                                         .ToListAsync();

        var keep = new List<(int, DeckStatus)>();
        var overwrite = new List<(int, DeckStatus)>();
        var oversized = 0;
        var budget = MaxProgressSubdecksPerRequest;

        foreach (var group in children.GroupBy(c => c.ParentDeckId))
        {
            var ordered = group.OrderBy(c => c.DeckOrder).ThenBy(c => c.DeckId).ToList();
            if (ordered.Count > MaxProgressSubdecksPerDeck)
            {
                oversized++;
                continue;
            }

            var entry = expandable[group.Key];
            var take = Math.Min(entry.Progress!.Value, Math.Min(ordered.Count, budget));
            budget -= take;

            var target = entry.OverwriteSubdecks ? overwrite : keep;
            foreach (var child in ordered.Take(take))
                target.Add((child.DeckId, DeckStatus.Completed));
        }

        return new ProgressSubdecks(keep, overwrite, oversized);
    }

    [HttpPost("deck-preferences/bulk")]
    [SwaggerOperation(Summary = "Bulk-edit deck preferences")]
    public async Task<IResult> BulkDeckPreferences([FromBody] BulkDeckPreferencesRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var operations = (request.Status.HasValue ? 1 : 0) + (request.IsFavourite.HasValue ? 1 : 0) + (request.Remove ? 1 : 0);
        if (operations != 1)
            return Results.BadRequest(new { message = "Provide exactly one operation: status, isFavourite or remove." });

        var deckIds = request.DeckIds.Where(id => id > 0).Distinct().ToList();
        if (deckIds.Count == 0)
            return Results.BadRequest(new { message = "No decks selected." });
        if (deckIds.Count > MaxBulkPreferenceDecks)
            return Results.BadRequest(new { message = $"Too many decks in one request (max {MaxBulkPreferenceDecks})." });

        int affected, skipped;
        var totalsChanged = false;

        if (request.Status.HasValue)
        {
            if (!Enum.IsDefined(request.Status.Value))
                return Results.BadRequest(new { message = "Unknown status." });

            // Bulk changes leave dates unknown, since they mostly catalogue past reading, and never reopen a completion.
            var outcome = await DeckPreferenceHelper.ApplyStatusesAsync(userContext, jitenContext, userId,
                                                                        deckIds.Select(id => (id, request.Status.Value)).ToList(),
                                                                        overwriteExisting: true, skipIgnored: true, newEntry: true);
            affected = outcome.Added + outcome.Updated;
            skipped = outcome.Unchanged + outcome.SkippedIgnored + outcome.SkippedFull;
            totalsChanged = outcome.TotalsChanged;
        }
        else
        {
            var preferences = await userContext.UserDeckPreferences
                                               .Where(p => p.UserId == userId && deckIds.Contains(p.DeckId))
                                               .ToDictionaryAsync(p => p.DeckId);

            affected = 0;
            skipped = 0;

            if (request.Remove)
            {
                foreach (var preference in preferences.Values)
                {
                    if (preference.Status == DeckStatus.None && (preference.IsFavourite || preference.IsIgnored))
                        continue;

                    MediaListEntryHelper.ClearStatus(userContext, preference);
                    affected++;
                }

                await MediaListEntryHelper.RemoveReadsAsync(userContext, userId, preferences.Keys.ToList());
                totalsChanged |= affected > 0;
                skipped = deckIds.Count - affected;
            }
            else
            {
                var favourite = request.IsFavourite!.Value;
                foreach (var deckId in deckIds)
                {
                    if (!preferences.TryGetValue(deckId, out var preference))
                    {
                        if (!favourite)
                        {
                            skipped++;
                            continue;
                        }

                        preference = new UserDeckPreference { UserId = userId, DeckId = deckId };
                        userContext.UserDeckPreferences.Add(preference);
                        preferences[deckId] = preference;
                    }

                    if (favourite && preference.IsIgnored)
                    {
                        skipped++;
                        continue;
                    }

                    if (preference.IsFavourite == favourite)
                    {
                        skipped++;
                        continue;
                    }

                    preference.IsFavourite = favourite;
                    affected++;
                }
            }
        }

        await userContext.SaveChangesAsync();
        await smartDeckDirty.MarkDirty(userId);

        if (totalsChanged)
            backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserAccomplishments(userId));

        logger.LogInformation("Bulk deck preferences: UserId={UserId}, Decks={Decks}, Affected={Affected}, Skipped={Skipped}",
                              userId, deckIds.Count, affected, skipped);

        var listEntries = request.Status is { } newStatus && newStatus != DeckStatus.None
            ? await MediaListEntryHelper.BuildSummariesAsync(userContext, jitenContext, userId, deckIds)
            : null;

        return Results.Ok(new { affected, skipped, listEntries });
    }

    /// <summary>
    /// Returns the caller's tracked media list as title/cover/status rows only, for pickers that need
    /// to list the decks without the cost of full deck DTOs and coverage.
    /// </summary>
    [HttpGet("media-list")]
    [SwaggerOperation(Summary = "Get the caller's tracked media list in a slim form")]
    public async Task<IResult> GetOwnMediaList()
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var entries = (await BuildMediaListAsync(userId))
                      .OrderBy(e => e.Display.OriginalTitle)
                      .Select(e => new
                                   {
                                       deckId = e.Display.DeckId,
                                       originalTitle = e.Display.OriginalTitle,
                                       romajiTitle = e.Display.RomajiTitle,
                                       englishTitle = e.Display.EnglishTitle,
                                       mediaType = e.Display.MediaType,
                                       coverName = e.Display.CoverName,
                                       status = e.Status,
                                       isFavourite = e.IsFavourite,
                                   })
                      .ToList();

        return Results.Ok(entries);
    }

    /// <summary>
    /// Exports the caller's tracked media list. CSV ships UTF-8 with BOM so Japanese titles open cleanly in Excel.
    /// </summary>
    [HttpGet("media-list/export")]
    [SwaggerOperation(Summary = "Export the tracked media list as CSV or JSON")]
    public async Task<IResult> ExportMediaList([FromQuery] string format = "csv")
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        format = format.ToLowerInvariant();
        if (format is not ("csv" or "json"))
            return Results.BadRequest(new { message = "Format must be csv or json." });

        var list = await BuildMediaListAsync(userId);

        var childIds = list.SelectMany(e => e.Display.Children.Select(c => c.DeckId)).ToList();
        var completedChildIds = await MediaListEntryHelper.CompletedDeckIdsAsync(userContext, userId, childIds);

        var exportedIds = list.Select(e => e.Display.DeckId).Concat(childIds).ToList();
        var entriesByDeck = (await userContext.UserMediaListEntries
                                            .AsNoTracking()
                                            .Where(r => r.UserId == userId && exportedIds.Contains(r.DeckId))
                                            .ToListAsync())
                          .GroupBy(r => r.DeckId)
                          .ToDictionary(g => g.Key, g => g.ToList());
        var childPreferences = await userContext.UserDeckPreferences
                                                .AsNoTracking()
                                                .Where(p => p.UserId == userId && childIds.Contains(p.DeckId) && p.Status != DeckStatus.None)
                                                .ToDictionaryAsync(p => p.DeckId, p => p.Status);
        var currentEntryIds = (await userContext.UserDeckPreferences
                                               .AsNoTracking()
                                               .Where(p => p.UserId == userId && exportedIds.Contains(p.DeckId) && p.CurrentEntryId != null)
                                               .Select(p => p.CurrentEntryId!.Value)
                                               .ToListAsync()).ToHashSet();

        List<UserMediaListEntry> Ordered(int deckId) =>
            (entriesByDeck.GetValueOrDefault(deckId) ?? []).OrderBy(r => r.StartedOn ?? r.FinishedOn ?? DateOnly.MinValue).ThenBy(r => r.Id).ToList();

        // A volume entry names the series entry it was read under by its position in the series' exported history; ids mean nothing elsewhere.
        Dictionary<long, int> Positions(int deckId) => Ordered(deckId).Select((r, i) => (r.Id, i)).ToDictionary(x => x.Id, x => x.i);

        static int? PositionOf(long? entryId, IReadOnlyDictionary<long, int>? positions) =>
            entryId is { } id && positions != null && positions.TryGetValue(id, out var position) ? position : null;

        List<object> History(int deckId, IReadOnlyDictionary<long, int>? seriesPositions = null) =>
            Ordered(deckId)
            .Select(r => (object)new
                                 {
                                     state = r.State.ToString(), startedOn = r.StartedOn, finishedOn = r.FinishedOn, charactersRead = r.CharactersRead,
                                     isCurrent = currentEntryIds.Contains(r.Id), seriesEntry = PositionOf(r.SeriesEntryId, seriesPositions)
                                 })
            .ToList();

        var entries = list
                      .OrderBy(e => e.Display.OriginalTitle)
                      .Select(e =>
                      {
                          var completedUnits = e.Display.Children.Count(c => completedChildIds.Contains(c.DeckId));
                          var deckEntries = entriesByDeck.GetValueOrDefault(e.Display.DeckId) ?? [];
                          var listEntry = deckEntries.Count > 0 ? MediaListEntryHelper.Summarise(deckEntries, currentEntryIds) : null;
                          var seriesPositions = Positions(e.Display.DeckId);
                          return new
                                 {
                                     deckId = e.Display.DeckId,
                                     originalTitle = e.Display.OriginalTitle,
                                     romajiTitle = e.Display.RomajiTitle,
                                     englishTitle = e.Display.EnglishTitle,
                                     mediaType = e.Display.MediaType.ToString(),
                                     status = e.Status.ToString(),
                                     progress = completedUnits > 0 ? completedUnits : (int?)null,
                                     isFavourite = e.IsFavourite,
                                     jitenUrl = $"https://jiten.moe/decks/media/{e.Display.DeckId}",
                                     externalLinks = e.Display.Links.Select(l => l.Url).ToList(),
                                     startedOn = listEntry?.StartedOn,
                                     finishedOn = listEntry?.FinishedOn,
                                     timesCompleted = listEntry?.CompletedCount ?? 0,
                                     charactersRead = listEntry?.CharactersRead,
                                     history = History(e.Display.DeckId),
                                     volumes = e.Display.Children
                                                .Where(c => childPreferences.ContainsKey(c.DeckId))
                                                .Select(c => new
                                                             {
                                                                 deckId = c.DeckId, status = childPreferences[c.DeckId].ToString(),
                                                                 history = History(c.DeckId, seriesPositions)
                                                             })
                                                .ToList(),
                                 };
                      })
                      .ToList();

        if (format == "json")
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(entries, new JsonSerializerOptions { WriteIndented = true });
            return Results.File(json, "application/json", "jiten-media-list.json");
        }

        var sb = new StringBuilder();
        sb.AppendLine("DeckId,OriginalTitle,RomajiTitle,EnglishTitle,MediaType,Status,Progress,IsFavourite,JitenUrl,ExternalLinks," +
                      "StartedOn,FinishedOn,TimesCompleted,CharactersRead");
        foreach (var e in entries)
        {
            sb.AppendLine(string.Join(',',
                                      e.deckId.ToString(),
                                      CsvField(e.originalTitle),
                                      CsvField(e.romajiTitle),
                                      CsvField(e.englishTitle),
                                      CsvField(e.mediaType),
                                      CsvField(e.status),
                                      e.progress?.ToString() ?? string.Empty,
                                      e.isFavourite.ToString(),
                                      CsvField(e.jitenUrl),
                                      CsvField(string.Join(" | ", e.externalLinks)),
                                      e.startedOn?.ToString("yyyy-MM-dd") ?? string.Empty,
                                      e.finishedOn?.ToString("yyyy-MM-dd") ?? string.Empty,
                                      e.timesCompleted.ToString(),
                                      e.charactersRead?.ToString() ?? string.Empty));
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return Results.File(bytes, "text/csv", "jiten-media-list.csv");
    }

    private static string CsvField(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

}
