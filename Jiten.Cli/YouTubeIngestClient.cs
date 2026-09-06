using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jiten.Core.YouTube;

namespace Jiten.Cli;

/// <summary>
/// Talks to the API's ingest endpoints with the shared YouTube:IngestKey, for machines that can reach YouTube
/// but not the production database.
/// </summary>
public class YouTubeIngestClient
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public YouTubeIngestClient(string baseUrl, string key)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.Add("X-Ingest-Key", key);
    }

    public record PendingSource(int DeckId, string ChannelName, YouTubeSourceFilters Filters, List<PendingVideo> Videos);

    public record PendingVideo(string VideoId, string Title, int? RuntimeSeconds);

    public async Task<List<PendingSource>> GetPendingAsync(int? deckId, int max)
    {
        var query = deckId != null ? $"?deckId={deckId}&max={max}" : $"?max={max}";
        return await _http.GetFromJsonAsync<List<PendingSource>>($"api/ingest/youtube/pending{query}", Json) ?? [];
    }

    public async Task<int> RegisterSourceAsync(YouTubeSourceInfo source, YouTubeSourceFilters filters)
    {
        using var response = await _http.PostAsJsonAsync("api/ingest/youtube/sources", new { source, filters }, Json);
        await EnsureOkAsync(response);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return body.GetProperty("deckId").GetInt32();
    }

    public record Registration(int Id, string Url, bool HasReleaseDate);

    public async Task<Registration> GetRegistrationAsync(int id)
    {
        using var response = await _http.GetAsync($"api/ingest/youtube/registrations/{id}");
        await EnsureOkAsync(response);
        return (await response.Content.ReadFromJsonAsync<Registration>(Json))!;
    }

    public async Task<int> CompleteRegistrationAsync(int id, YouTubeSourceInfo source)
    {
        using var response = await _http.PostAsJsonAsync($"api/ingest/youtube/registrations/{id}/complete", new { source }, Json);
        await EnsureOkAsync(response);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return body.GetProperty("deckId").GetInt32();
    }

    public async Task SkipAsync(int deckId, string videoId, YouTubeFetchOutcome outcome)
    {
        using var response = await _http.PostAsJsonAsync($"api/ingest/youtube/videos/{deckId}/{videoId}/skip",
                                                         new { status = outcome.Status.ToString(), skipReason = outcome.SkipReason, info = outcome.Info }, Json);
        await EnsureOkAsync(response);
    }

    public async Task<int?> UploadFetchedAsync(int deckId, string videoId, YouTubeFetchOutcome outcome)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(outcome.Info, Json)), "info");

        var file = new ByteArrayContent(await File.ReadAllBytesAsync(outcome.CleanedSrtPath!));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/x-subrip");
        form.Add(file, "subtitles", $"{videoId}.clean.srt");

        using var response = await _http.PostAsync($"api/ingest/youtube/videos/{deckId}/{videoId}/fetched", form);
        await EnsureOkAsync(response);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return body.TryGetProperty("childDeckId", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt32() : null;
    }

    public async Task ImportFetchedAsync(int deckId)
    {
        using var response = await _http.PostAsync($"api/ingest/youtube/sources/{deckId}/import", null);
        await EnsureOkAsync(response);
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync();
        throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }
}
