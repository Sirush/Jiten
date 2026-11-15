using System.Globalization;
using System.Net;
using System.Text.Json;
using Jiten.Core.Data.Providers.Jimaku;
using Microsoft.Extensions.Logging;

namespace Jiten.Core.Jimaku;

/// <summary>
/// Jimaku limits requests per IP, so every call from the server passes through this one gate: requests run one at
/// a time, spaced apart, and wait out the window whenever the response headers say it is spent.
/// </summary>
public class JimakuClient(HttpClient http, string apiKey, ILogger<JimakuClient> logger)
{
    private const string Host = "jimaku.cc";
    private const int MaxAttempts = 4;
    private const long MaxDownloadBytes = 20 * 1024 * 1024;
    private static readonly TimeSpan MinSpacing = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;

    public async Task<JimakuEntry?> GetEntryAsync(int id, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync($"https://{Host}/api/entries/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JimakuEntry>(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public async Task<List<JimakuFile>?> GetFilesAsync(int id, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync($"https://{Host}/api/entries/{id}/files", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<List<JimakuFile>>(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public async Task<List<JimakuEntry>> SearchAsync(string query, bool anime, CancellationToken cancellationToken = default)
    {
        var url = $"https://{Host}/api/entries/search?anime={(anime ? "true" : "false")}&query={Uri.EscapeDataString(query)}";
        using var response = await SendAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<List<JimakuEntry>>(await response.Content.ReadAsStringAsync(cancellationToken)) ?? [];
    }

    /// <summary>Every entry that received a file since <paramref name="after"/>, in one request.</summary>
    public async Task<List<JimakuEntry>> RecentAsync(bool anime, DateTimeOffset after, CancellationToken cancellationToken = default)
    {
        var url = $"https://{Host}/api/entries/search?anime={(anime ? "true" : "false")}&after={after.ToUnixTimeSeconds()}";
        using var response = await SendAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<List<JimakuEntry>>(await response.Content.ReadAsStringAsync(cancellationToken)) ?? [];
    }

    public async Task DownloadAsync(string url, string filePath, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            throw new IOException($"The file is larger than {MaxDownloadBytes / (1024 * 1024)} MB.");

        await using var stream = new FileStream(filePath, FileMode.Create);
        await response.Content.CopyToAsync(stream, cancellationToken);
    }

    /// <summary>File URLs come from stored API responses; the API key must never follow one to another host.</summary>
    public static bool IsJimakuUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase);

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken cancellationToken)
    {
        if (!IsJimakuUrl(url))
            throw new InvalidOperationException($"Refusing to fetch a non-Jimaku URL: {url}");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var wait = _notBefore - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, cancellationToken);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Authorization", apiKey);

                var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                _notBefore = DateTimeOffset.UtcNow + NextDelay(response);

                if (response.StatusCode != HttpStatusCode.TooManyRequests)
                    return response;

                response.Dispose();
                if (attempt >= MaxAttempts)
                    throw new HttpRequestException("Jimaku kept answering 429 Too Many Requests.", null, HttpStatusCode.TooManyRequests);

                logger.LogWarning("Jimaku rate limit hit, waiting {Seconds:0.#}s (attempt {Attempt})",
                                  (_notBefore - DateTimeOffset.UtcNow).TotalSeconds, attempt);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TimeSpan NextDelay(HttpResponseMessage response)
    {
        TimeSpan? resetAfter = HeaderNumber(response, "x-ratelimit-reset-after") is { } seconds
            ? TimeSpan.FromSeconds(seconds)
            : response.Headers.RetryAfter?.Delta;

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return Clamp(resetAfter ?? TimeSpan.FromSeconds(15));

        return HeaderNumber(response, "x-ratelimit-remaining") is <= 1 && resetAfter != null ? Clamp(resetAfter.Value) : MinSpacing;
    }

    private static TimeSpan Clamp(TimeSpan delay) =>
        delay < MinSpacing ? MinSpacing : delay > MaxWait ? MaxWait : delay + TimeSpan.FromMilliseconds(250);

    private static double? HeaderNumber(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) &&
        double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number >= 0
            ? number
            : null;
}
