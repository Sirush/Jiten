using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jiten.Core.Jimaku;

public sealed record EpisodeAiring(int? Total, int? Aired, bool Finished);

/// <summary>
/// Episode totals for tracked shows. AniList answers for fifty shows per request; TMDB costs one request per show.
/// </summary>
public class EpisodeAiringLookup(HttpClient http, string tmdbApiKey, ILogger<EpisodeAiringLookup> logger)
{
    private const int AnilistPageSize = 50;
    private static readonly TimeSpan AnilistSpacing = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TmdbSpacing = TimeSpan.FromMilliseconds(300);

    private const string AnilistQuery = """
                                        query ($ids: [Int]) {
                                          Page(perPage: 50) {
                                            media(id_in: $ids, type: ANIME) {
                                              id
                                              episodes
                                              status
                                              nextAiringEpisode { episode }
                                            }
                                          }
                                        }
                                        """;

    public async Task<Dictionary<int, EpisodeAiring>> AnilistAsync(IReadOnlyCollection<int> anilistIds,
                                                                   CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<int, EpisodeAiring>();
        var chunks = anilistIds.Distinct().Chunk(AnilistPageSize).ToList();

        for (var i = 0; i < chunks.Count; i++)
        {
            if (i > 0)
                await Task.Delay(AnilistSpacing, cancellationToken);

            using var document = await PostAnilistAsync(chunks[i], cancellationToken);
            if (document == null)
                continue;

            foreach (var media in document.RootElement.GetProperty("data").GetProperty("Page").GetProperty("media").EnumerateArray())
            {
                var total = Int(media, "episodes");
                var status = media.TryGetProperty("status", out var s) ? s.GetString() : null;
                var finished = status is "FINISHED" or "CANCELLED";
                var next = media.TryGetProperty("nextAiringEpisode", out var n) && n.ValueKind == JsonValueKind.Object
                    ? Int(n, "episode")
                    : null;

                var aired = next != null ? next - 1 : finished ? total : null;
                result[media.GetProperty("id").GetInt32()] = new EpisodeAiring(total, aired, finished);
            }
        }

        return result;
    }

    private async Task<JsonDocument?> PostAnilistAsync(int[] ids, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var body = JsonSerializer.Serialize(new { query = AnilistQuery, variables = new { ids } });
            using var response = await http.PostAsync("https://graphql.anilist.co",
                                                      new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 1)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                logger.LogWarning("AniList rate limit hit, waiting {Seconds:0}s", wait.TotalSeconds);
                await Task.Delay(wait > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : wait, cancellationToken);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("AniList episode lookup failed with {Status}", (int)response.StatusCode);
                return null;
            }

            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        return null;
    }

    /// <param name="tmdbId">Jimaku encoding, tv:1234</param>
    public async Task<EpisodeAiring?> TmdbAsync(string tmdbId, int? season, CancellationToken cancellationToken = default)
    {
        var id = tmdbId.StartsWith("tv:", StringComparison.OrdinalIgnoreCase) ? tmdbId[3..] : tmdbId;
        if (!int.TryParse(id, out var numericId))
            return null;

        await Task.Delay(TmdbSpacing, cancellationToken);
        using var response = await http.GetAsync($"https://api.themoviedb.org/3/tv/{numericId}?api_key={tmdbApiKey}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("TMDB episode lookup for {TmdbId} failed with {Status}", tmdbId, (int)response.StatusCode);
            return null;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ParseTmdb(document.RootElement, season);
    }

    internal static EpisodeAiring ParseTmdb(JsonElement show, int? season)
    {
        var status = show.TryGetProperty("status", out var s) ? s.GetString() : null;
        var ended = status is "Ended" or "Canceled";

        var seasons = show.TryGetProperty("seasons", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
                  .Select(e => (Number: Int(e, "season_number") ?? 0, Episodes: Int(e, "episode_count")))
                  .Where(e => e.Number > 0)
                  .ToList()
            : [];

        var target = season ?? (seasons.Count == 1 ? seasons[0].Number : null);

        int? lastSeason = null, lastEpisode = null;
        if (show.TryGetProperty("last_episode_to_air", out var last) && last.ValueKind == JsonValueKind.Object)
        {
            lastSeason = Int(last, "season_number");
            lastEpisode = Int(last, "episode_number");
        }

        if (target is not { } wanted)
            return new EpisodeAiring(Int(show, "number_of_episodes"), null, ended);

        var total = seasons.Where(e => e.Number == wanted).Select(e => e.Episodes).FirstOrDefault();
        int? aired = lastSeason == null ? null : lastSeason > wanted ? total : lastSeason == wanted ? lastEpisode : 0;
        var finished = ended || lastSeason > wanted || (total != null && aired >= total && !HasNextEpisodeIn(show, wanted));

        return new EpisodeAiring(total, aired, finished);
    }

    private static bool HasNextEpisodeIn(JsonElement show, int season) =>
        show.TryGetProperty("next_episode_to_air", out var next) && next.ValueKind == JsonValueKind.Object &&
        Int(next, "season_number") == season;

    private static int? Int(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
}
