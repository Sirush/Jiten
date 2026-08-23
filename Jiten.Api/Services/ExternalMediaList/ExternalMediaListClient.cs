using System.Net;
using System.Text;
using System.Text.Json;
using Jiten.Core.Data;

namespace Jiten.Api.Services.ExternalMediaList;

public class ExternalMediaListClient(IHttpClientFactory httpClientFactory, ExternalFetchGate gate, ILogger<ExternalMediaListClient> logger)
    : IExternalMediaListClient
{
    private const int VndbPageSize = 100;
    private const int VndbMaxPages = 50;
    private const int MaxRateLimitRetries = 2;
    private static readonly TimeSpan AnilistPagingDelay = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan PagingDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(5);

    public async Task<ExternalListFetchResult> FetchListAsync(ExternalListProvider provider, string username,
                                                              CancellationToken ct = default)
    {
        if (provider is not (ExternalListProvider.Anilist or ExternalListProvider.Vndb))
            return ExternalListFetchResult.Fail("Unknown provider.");

        if (!await gate.EnterAsync(provider, ct))
        {
            logger.LogInformation("External list fetch gate timed out: Provider={Provider}", provider);
            return ExternalListFetchResult.Fail($"{ProviderName(provider)} imports are busy right now. Please try again shortly.");
        }

        try
        {
            return provider == ExternalListProvider.Anilist
                ? await FetchAnilistAsync(username, ct)
                : await FetchVndbAsync(username, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(e, "External list fetch failed: Provider={Provider}", provider);
            return ExternalListFetchResult.Fail($"Could not reach {ProviderName(provider)}. Please try again in a moment.");
        }
        finally
        {
            gate.Exit(provider);
        }
    }

    private static string ProviderName(ExternalListProvider provider) =>
        provider == ExternalListProvider.Anilist ? "AniList" : "VNDB";

    private static string RateLimitedMessage(ExternalListProvider provider) =>
        $"{ProviderName(provider)} is rate limiting us. Please try again in a minute.";

    /// <summary>Honours a 429's Retry-After within a bounded total wait; null means the provider is still refusing.</summary>
    private async Task<HttpResponseMessage?> SendAsync(ExternalListProvider provider, HttpClient http,
                                                       Func<HttpRequestMessage> request, CancellationToken ct)
    {
        var budget = MaxRateLimitWait;

        for (var attempt = 0;; attempt++)
        {
            var response = await http.SendAsync(request(), ct);
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
                return response;

            var wait = ReadRetryAfter(response) ?? DefaultRetryAfter;
            response.Dispose();

            if (attempt >= MaxRateLimitRetries || wait > budget)
                return null;

            budget -= wait;
            logger.LogInformation("External list fetch rate limited: Provider={Provider}, Wait={Wait}", provider, wait);
            await Task.Delay(wait, ct);
        }
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter == null)
            return null;

        var wait = retryAfter.Delta ?? (retryAfter.Date.HasValue ? retryAfter.Date.Value - DateTimeOffset.UtcNow : null);
        return wait is { } value && value > TimeSpan.Zero ? value : null;
    }

    private async Task<ExternalListFetchResult> FetchAnilistAsync(string username, CancellationToken ct)
    {
        var entries = new List<ExternalListEntry>();

        foreach (var type in new[] { "ANIME", "MANGA" })
        {
            var requestBody = new
                              {
                                  query = """
                                          query ($userName: String, $type: MediaType) {
                                            MediaListCollection(userName: $userName, type: $type) {
                                              lists {
                                                entries {
                                                  status
                                                  progress
                                                  progressVolumes
                                                  completedAt { year month day }
                                                  startedAt { year month day }
                                                  media { id title { native romaji } }
                                                }
                                              }
                                            }
                                          }
                                          """,
                                  variables = new { userName = username, type },
                              };

            var http = httpClientFactory.CreateClient();
            var payload = JsonSerializer.Serialize(requestBody);
            var response = await SendAsync(ExternalListProvider.Anilist, http,
                                           () => new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co")
                                                 {
                                                     Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                                                 }, ct);

            if (response == null)
                return ExternalListFetchResult.Fail(RateLimitedMessage(ExternalListProvider.Anilist));

            var body = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array &&
                errors.GetArrayLength() > 0)
            {
                var message = errors[0].TryGetProperty("message", out var msg) ? msg.GetString() : null;

                if (IsAnilistRateLimitError(errors[0], message))
                    return ExternalListFetchResult.Fail(RateLimitedMessage(ExternalListProvider.Anilist));

                // "Private User" / "User not found" both mean we cannot read the list.
                return ExternalListFetchResult.Fail(message is { Length: > 0 }
                                                        ? $"AniList said: {message}. Check the username and that the list is public."
                                                        : "AniList user not found, or their list is private.");
            }

            if (!response.IsSuccessStatusCode)
                return ExternalListFetchResult.Fail("Could not reach AniList. Please try again in a moment.");

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("MediaListCollection", out var collection) ||
                collection.ValueKind != JsonValueKind.Object ||
                !collection.TryGetProperty("lists", out var lists) ||
                lists.ValueKind != JsonValueKind.Array)
                continue;

            var urlSegment = type == "ANIME" ? "anime" : "manga";

            foreach (var list in lists.EnumerateArray())
            {
                if (!list.TryGetProperty("entries", out var listEntries) || listEntries.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var entry in listEntries.EnumerateArray())
                {
                    var status = entry.TryGetProperty("status", out var s) ? s.GetString() : null;
                    if (status == null || !TryMapAnilistStatus(status, out var mapped))
                        continue;

                    if (!entry.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Object)
                        continue;

                    var id = media.GetProperty("id").GetInt32();
                    string? title = null;
                    if (media.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.Object)
                    {
                        title = titleEl.TryGetProperty("native", out var native) ? native.GetString() : null;
                        title ??= titleEl.TryGetProperty("romaji", out var romaji) ? romaji.GetString() : null;
                    }

                    // Users often leave the finished date unset; the start date is the next best signal.
                    var finishedAt = ReadAnilistFuzzyDate(entry, "completedAt") ?? ReadAnilistFuzzyDate(entry, "startedAt");

                    // Manga chapter counts never map to Jiten subdecks, which are volumes.
                    var progressField = type == "ANIME" ? "progress" : "progressVolumes";
                    int? progress = entry.TryGetProperty(progressField, out var progressEl) && progressEl.ValueKind == JsonValueKind.Number
                                        ? progressEl.GetInt32()
                                        : null;
                    if (progress is <= 0)
                        progress = null;

                    entries.Add(new ExternalListEntry(id.ToString(), title ?? $"AniList #{id}",
                                                      $"https://anilist.co/{urlSegment}/{id}", status, mapped, finishedAt, progress));
                }
            }

            await Task.Delay(AnilistPagingDelay, ct);
        }

        // Custom lists repeat entries already present in the status lists.
        var deduped = entries
                      .GroupBy(e => e.ExternalId)
                      .Select(g => g.OrderByDescending(e => StatusRank(e.MappedStatus)).First())
                      .ToList();

        return new ExternalListFetchResult(deduped, null);
    }

    private static DateOnly? ReadAnilistFuzzyDate(JsonElement entry, string property)
    {
        if (!entry.TryGetProperty(property, out var date) || date.ValueKind != JsonValueKind.Object ||
            !date.TryGetProperty("year", out var year) || year.ValueKind != JsonValueKind.Number)
            return null;

        var month = date.TryGetProperty("month", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 1;
        var day = date.TryGetProperty("day", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 1;
        return new DateOnly(year.GetInt32(), Math.Clamp(month, 1, 12), Math.Clamp(day, 1, 28));
    }

    /// <summary>AniList reports its own limiter as a GraphQL error, sometimes with a 200 status.</summary>
    private static bool IsAnilistRateLimitError(JsonElement error, string? message)
    {
        if (error.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number && status.GetInt32() == 429)
            return true;

        return message != null && message.Contains("too many requests", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryMapAnilistStatus(string status, out DeckStatus mapped)
    {
        // Paused stays Ongoing: the user still intends to continue.
        mapped = status switch
                 {
                     "CURRENT" => DeckStatus.Ongoing,
                     "REPEATING" => DeckStatus.Completed,
                     "PAUSED" => DeckStatus.Ongoing,
                     "PLANNING" => DeckStatus.Planning,
                     "COMPLETED" => DeckStatus.Completed,
                     "DROPPED" => DeckStatus.Dropped,
                     _ => DeckStatus.None,
                 };
        return mapped != DeckStatus.None;
    }

    private async Task<ExternalListFetchResult> FetchVndbAsync(string username, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient();

        var userUrl = $"https://api.vndb.org/kana/user?q={Uri.EscapeDataString(username)}";
        var userResponse = await SendAsync(ExternalListProvider.Vndb, http, () => new HttpRequestMessage(HttpMethod.Get, userUrl), ct);

        if (userResponse == null)
            return ExternalListFetchResult.Fail(RateLimitedMessage(ExternalListProvider.Vndb));
        if (!userResponse.IsSuccessStatusCode)
            return ExternalListFetchResult.Fail("Could not reach VNDB. Please try again in a moment.");

        string? uid = null;
        using (var userDoc = JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync(ct)))
        {
            foreach (var prop in userDoc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("id", out var idEl))
                    uid = idEl.GetString();
            }
        }

        if (uid == null)
            return ExternalListFetchResult.Fail("VNDB user not found. Check the username or uXX id.");

        var entries = new List<ExternalListEntry>();

        for (var page = 1; page <= VndbMaxPages; page++)
        {
            var requestBody = new
                              {
                                  user = uid,
                                  fields = "id, voted, started, finished, labels{id,label}, vn{title, alttitle}",
                                  results = VndbPageSize,
                                  page,
                              };

            var payload = JsonSerializer.Serialize(requestBody);
            var response = await SendAsync(ExternalListProvider.Vndb, http,
                                           () => new HttpRequestMessage(HttpMethod.Post, "https://api.vndb.org/kana/ulist")
                                                 {
                                                     Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                                                 }, ct);

            if (response == null)
                return ExternalListFetchResult.Fail(RateLimitedMessage(ExternalListProvider.Vndb));
            if (!response.IsSuccessStatusCode)
                return ExternalListFetchResult.Fail("Could not reach VNDB. Please try again in a moment.");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var results = doc.RootElement.GetProperty("results");

            foreach (var item in results.EnumerateArray())
            {
                var vnId = item.GetProperty("id").GetString();
                if (vnId == null)
                    continue;

                var (label, mapped) = MapVndbLabels(item);
                if (mapped == DeckStatus.None)
                    continue;

                string? title = null;
                if (item.TryGetProperty("vn", out var vn) && vn.ValueKind == JsonValueKind.Object)
                {
                    title = vn.TryGetProperty("alttitle", out var alt) && alt.ValueKind == JsonValueKind.String ? alt.GetString() : null;
                    title ??= vn.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                }

                // Users often leave the finished date unset; start date, then vote date, are the next best signals.
                var finishedAt = ReadVndbDate(item, "finished") ?? ReadVndbDate(item, "started");
                if (finishedAt == null && item.TryGetProperty("voted", out var voted) && voted.ValueKind == JsonValueKind.Number)
                    finishedAt = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(voted.GetInt64()).UtcDateTime);

                entries.Add(new ExternalListEntry(vnId, title ?? $"VNDB {vnId}", $"https://vndb.org/{vnId}", label, mapped, finishedAt));
            }

            var more = doc.RootElement.TryGetProperty("more", out var moreEl) && moreEl.ValueKind == JsonValueKind.True;
            if (!more)
                break;

            await Task.Delay(PagingDelay, ct);
        }

        return new ExternalListFetchResult(entries, null);
    }

    /// <summary>VNDB dates may be partial ("2020" or "2020-01"); missing parts default to 1.</summary>
    private static DateOnly? ReadVndbDate(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.String)
            return null;

        var parts = el.GetString()!.Split('-');
        if (parts.Length == 0 || !int.TryParse(parts[0], out var year) || year < 1)
            return null;

        var month = parts.Length > 1 && int.TryParse(parts[1], out var m) ? Math.Clamp(m, 1, 12) : 1;
        var day = parts.Length > 2 && int.TryParse(parts[2], out var d) ? Math.Clamp(d, 1, 28) : 1;
        return new DateOnly(year, month, day);
    }

    private static (string Label, DeckStatus Status) MapVndbLabels(JsonElement item)
    {
        var best = (Label: "", Status: DeckStatus.None, Rank: -1);

        if (!item.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
            return (best.Label, best.Status);

        foreach (var label in labels.EnumerateArray())
        {
            if (!label.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                continue;

            // Stalled stays Ongoing, same reasoning as AniList's Paused.
            var (status, rank) = idEl.GetInt32() switch
                                 {
                                     2 => (DeckStatus.Completed, 4), // Finished
                                     1 => (DeckStatus.Ongoing, 3),   // Playing
                                     3 => (DeckStatus.Ongoing, 2),   // Stalled
                                     4 => (DeckStatus.Dropped, 1),   // Dropped
                                     5 => (DeckStatus.Planning, 0),  // Wishlist
                                     _ => (DeckStatus.None, -1),
                                 };

            if (rank <= best.Rank)
                continue;

            var text = label.TryGetProperty("label", out var labelText) ? labelText.GetString() : null;
            best = (text ?? status.ToString(), status, rank);
        }

        return (best.Label, best.Status);
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
