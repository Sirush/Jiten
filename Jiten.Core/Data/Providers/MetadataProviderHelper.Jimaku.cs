using Jiten.Core.Data.Providers;
using Jiten.Core.Data.Providers.Jimaku;

namespace Jiten.Core;

public partial class MetadataProviderHelper
{
    /// <summary>Null when the entry carries neither an AniList nor a TMDB id.</summary>
    public static async Task<Metadata?> JimakuEntryMetadataAsync(JimakuEntry entry, string tmdbApiKey)
    {
        Metadata metadata;
        if (entry.Flags.Anime && entry.AnilistId.HasValue)
        {
            metadata = await AnilistAnimeApi(entry.AnilistId.Value);
        }
        else if (entry.TmdbId != null)
        {
            metadata = entry.Flags.Movie
                ? await TmdbMovieApi(entry.TmdbId.Replace("movie:", ""), tmdbApiKey)
                : await TmdbTvApi(entry.TmdbId.Replace("tv:", ""), tmdbApiKey);
            metadata.OriginalTitle = entry.JapaneseName;
            metadata.EnglishTitle = entry.EnglishName;
            metadata.RomajiTitle = entry.Name;
        }
        else
        {
            return null;
        }

        if (string.IsNullOrEmpty(metadata.OriginalTitle))
            metadata.OriginalTitle = metadata.EnglishTitle ?? metadata.RomajiTitle ?? entry.Name;

        return metadata;
    }

    public static async Task<JimakuEntry?> JimakuGetEntryAsync(HttpClient httpClient, string apiKey, int id)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://jimaku.cc/api/entries/{id}");
        request.Headers.Add("Authorization", apiKey);
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStringAsync();
        return System.Text.Json.JsonSerializer.Deserialize<JimakuEntry>(content);
    }

    public static async Task<List<JimakuFile>?> JimakuGetFilesAsync(HttpClient httpClient, string apiKey, int id)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://jimaku.cc/api/entries/{id}/files");
        request.Headers.Add("Authorization", apiKey);
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStringAsync();
        return System.Text.Json.JsonSerializer.Deserialize<List<JimakuFile>>(content);
    }

    public static async Task<List<JimakuEntry>?> JimakuSearchAsync(
        HttpClient httpClient, string apiKey,
        int? anilistId = null, string? tmdbId = null, string? query = null, bool anime = true)
    {
        var queryParams = new List<string>();
        queryParams.Add($"anime={anime.ToString().ToLowerInvariant()}");
        if (anilistId.HasValue) queryParams.Add($"anilist_id={anilistId.Value}");
        if (tmdbId != null) queryParams.Add($"tmdb_id={tmdbId}");
        if (query != null) queryParams.Add($"query={Uri.EscapeDataString(query)}");

        var url = $"https://jimaku.cc/api/entries/search?{string.Join("&", queryParams)}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", apiKey);
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStringAsync();
        return System.Text.Json.JsonSerializer.Deserialize<List<JimakuEntry>>(content);
    }

    public static async Task JimakuDownloadFileAsync(HttpClient httpClient, string url, string filePath)
    {
        var response = await httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        await using var fs = new FileStream(filePath, FileMode.Create);
        await response.Content.CopyToAsync(fs);
    }
}
