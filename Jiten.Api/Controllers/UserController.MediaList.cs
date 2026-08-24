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
            if (!matchedByDeck.TryGetValue(deckId, out var existing) || StatusRank(entry.MappedStatus) > StatusRank(existing.MappedStatus))
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
                             .Select(g => g.OrderByDescending(e => StatusRank(e.Status)).First())
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

        var subdecks = await ResolveProgressSubdecksAsync(valid);

        var outcome = await DeckPreferenceHelper.ApplyStatusesAsync(userContext, userId, entries,
                                                                    request.OverwriteExisting, skipIgnored: true);

        var completedTransition = outcome.CompletedTransition;
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

            var childOutcome = await DeckPreferenceHelper.ApplyStatusesAsync(userContext, userId, children, overwrite, skipIgnored: true);
            subdecksCompleted += childOutcome.Added + childOutcome.Updated;
            completedTransition |= childOutcome.CompletedTransition;
        }

        await userContext.SaveChangesAsync();

        if (completedTransition)
            backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserAccomplishments(userId));

        logger.LogInformation("Media list import applied: UserId={UserId}, Added={Added}, Updated={Updated}, Skipped={Skipped}, " +
                              "Favourited={Favourited}, Subdecks={Subdecks}, OversizedDecks={OversizedDecks}",
                              userId, outcome.Added, outcome.Updated, outcome.SkippedIgnored + outcome.SkippedExisting + invalid,
                              favourited, subdecksCompleted, subdecks.OversizedDecks);

        return Results.Ok(new
                          {
                              added = outcome.Added,
                              updated = outcome.Updated,
                              unchanged = outcome.Unchanged,
                              skippedIgnored = outcome.SkippedIgnored,
                              skippedExisting = outcome.SkippedExisting,
                              invalid,
                              favourited,
                              subdecksCompleted,
                              oversizedDecks = subdecks.OversizedDecks,
                          });
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
        var completedTransition = false;

        if (request.Status.HasValue)
        {
            if (!Enum.IsDefined(request.Status.Value))
                return Results.BadRequest(new { message = "Unknown status." });

            var outcome = await DeckPreferenceHelper.ApplyStatusesAsync(userContext, userId,
                                                                        deckIds.Select(id => (id, request.Status.Value)).ToList(),
                                                                        overwriteExisting: true, skipIgnored: true);
            affected = outcome.Added + outcome.Updated;
            skipped = outcome.Unchanged + outcome.SkippedIgnored;
            completedTransition = outcome.CompletedTransition;
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
                    if (preference.IsFavourite || preference.IsIgnored)
                    {
                        if (preference.Status == DeckStatus.None) continue;
                        completedTransition |= preference.Status == DeckStatus.Completed;
                        preference.Status = DeckStatus.None;
                    }
                    else
                    {
                        completedTransition |= preference.Status == DeckStatus.Completed;
                        userContext.UserDeckPreferences.Remove(preference);
                    }

                    affected++;
                }

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

        if (completedTransition)
            backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserAccomplishments(userId));

        logger.LogInformation("Bulk deck preferences: UserId={UserId}, Decks={Decks}, Affected={Affected}, Skipped={Skipped}",
                              userId, deckIds.Count, affected, skipped);

        return Results.Ok(new { affected, skipped });
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
        var completedChildIds = childIds.Count == 0
            ? []
            : (await userContext.UserDeckPreferences
                                .AsNoTracking()
                                .Where(p => p.UserId == userId && p.Status == DeckStatus.Completed && childIds.Contains(p.DeckId))
                                .Select(p => p.DeckId)
                                .ToListAsync()).ToHashSet();

        var entries = list
                      .OrderBy(e => e.Display.OriginalTitle)
                      .Select(e =>
                      {
                          var completedUnits = e.Display.Children.Count(c => completedChildIds.Contains(c.DeckId));
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
                                 };
                      })
                      .ToList();

        if (format == "json")
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(entries, new JsonSerializerOptions { WriteIndented = true });
            return Results.File(json, "application/json", "jiten-media-list.json");
        }

        var sb = new StringBuilder();
        sb.AppendLine("DeckId,OriginalTitle,RomajiTitle,EnglishTitle,MediaType,Status,Progress,IsFavourite,JitenUrl,ExternalLinks");
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
                                      CsvField(string.Join(" | ", e.externalLinks))));
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

    private static int StatusRank(DeckStatus s) => s switch
                                                   {
                                                       DeckStatus.Completed => 4,
                                                       DeckStatus.Ongoing => 3,
                                                       DeckStatus.Planning => 2,
                                                       DeckStatus.Dropped => 1,
                                                       _ => 0,
                                                   };
}
