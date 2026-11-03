using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;

namespace Jiten.Core.WebNovel;

/// <summary>
/// One gate per site, process-wide: every source on a site waits on the same instance, so two providers on one
/// host (Syosetu and its R18 mirror) never double the request rate.
/// </summary>
public sealed class WebNovelThrottle
{
    private static readonly ConcurrentDictionary<string, WebNovelThrottle> BySite = new();

    private readonly WebNovelPoliteness _politeness;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private int _requestCount;

    internal WebNovelThrottle(WebNovelPoliteness politeness)
    {
        _politeness = politeness;
    }

    public static WebNovelThrottle For(WebNovelSite site) =>
        BySite.GetOrAdd(site.Key, _ => new WebNovelThrottle(site.Politeness));

    /// <summary>
    /// Never waits; for tests that drive a source against a fake handler
    /// </summary>
    internal static WebNovelThrottle None() => new(new WebNovelPoliteness(TimeSpan.Zero, 0, TimeSpan.Zero));

    public async Task WaitAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var sinceLast = DateTimeOffset.UtcNow - _lastRequest;
            if (sinceLast < _politeness.RequestInterval)
                await Task.Delay(_politeness.RequestInterval - sinceLast, ct);

            if (_politeness.PauseEvery > 0 && ++_requestCount % _politeness.PauseEvery == 0)
                await Task.Delay(_politeness.Pause, ct);

            _lastRequest = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// Throttled GETs with backoff on 429 and 503 (Syosetu's "Too many access"). Any other failure throws an
/// <see cref="HttpRequestException"/> carrying the status, so callers can tell a deleted work (404) from an outage.
/// </summary>
public sealed class WebNovelHttp(
    IHttpClientFactory httpClientFactory,
    string clientName,
    WebNovelThrottle throttle,
    ILogger logger,
    Action<HttpRequestMessage>? configureRequest = null)
{
    private const int MaxRetries = 4;

    public async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await throttle.WaitAsync(ct);

            var client = httpClientFactory.CreateClient(clientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            configureRequest?.Invoke(request);

            var response = await client.SendAsync(request, ct);

            if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests &&
                attempt < MaxRetries)
            {
                response.Dispose();

                var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                logger.LogWarning("Webnovel source throttled us on {Url}, backing off {Backoff}s (attempt {Attempt})",
                                  url, backoff.TotalSeconds, attempt + 1);
                await Task.Delay(backoff, ct);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"Request to {url} failed with status {(int)status} ({status}).",
                                               inner: null, statusCode: status);
            }

            return response;
        }
    }
}
