using System.Net.Http.Json;

namespace Jiten.Api.Services;

/// <summary>
/// Submits changed URLs to IndexNow (Bing/Yandex/Seznam instant indexing). No-op when no key is
/// configured. Submissions are best-effort: failures are logged, never thrown, so they can't break
/// the calling job. IndexNow must only be pinged when content genuinely changes — never in bulk over
/// the whole catalogue (Bing has a ~10k URLs/day quota and distrusts mass re-submission).
/// </summary>
public interface IIndexNowService
{
    Task SubmitDeckAsync(int deckId, CancellationToken cancellationToken = default);
    Task SubmitUrlsAsync(IReadOnlyCollection<string> urls, CancellationToken cancellationToken = default);
}

public class IndexNowService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<IndexNowService> logger) : IIndexNowService
{
    private const string Endpoint = "https://api.indexnow.org/indexnow";

    private string SiteUrl => (configuration["IndexNow:SiteUrl"] ?? "https://jiten.moe").TrimEnd('/');

    public Task SubmitDeckAsync(int deckId, CancellationToken cancellationToken = default)
        => SubmitUrlsAsync(new[] { $"{SiteUrl}/decks/media/{deckId}/detail" }, cancellationToken);

    public async Task SubmitUrlsAsync(IReadOnlyCollection<string> urls, CancellationToken cancellationToken = default)
    {
        var key = configuration["IndexNow:Key"];
        if (string.IsNullOrWhiteSpace(key) || urls.Count == 0)
            return;

        var siteUrl = SiteUrl;
        var payload = new
        {
            host = new Uri(siteUrl).Host,
            key,
            keyLocation = $"{siteUrl}/{key}.txt",
            urlList = urls,
        };

        try
        {
            var client = httpClientFactory.CreateClient();
            // Best-effort, awaited inline at the tail of parse/reparse jobs — cap the wait so a hung
            // IndexNow endpoint can't pin a queue worker for the default 100s timeout.
            client.Timeout = TimeSpan.FromSeconds(10);
            using var response = await client.PostAsJsonAsync(Endpoint, payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("IndexNow returned {Status} for {Count} URL(s)", (int)response.StatusCode, urls.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "IndexNow submission failed for {Count} URL(s)", urls.Count);
        }
    }
}
