using System.Collections.Concurrent;
using BunnyCDN.Net.Storage;
using Microsoft.Extensions.Configuration;

namespace Jiten.Core;

public class BunnyCdnHelper
{
    private static string? _secret;
    private static string? _storageZoneName;
    private static string? _cdnBaseUrl;
    private static string? _apiKey;
    private static string? _securePullZoneUrl;
    private static string? _tokenAuthKey;
    private static string? _userStorageZoneName;
    private static string? _userSecret;
    private static int _signedUrlFallbackWarned;
    private static readonly HttpClient _httpClient = new();

    // BunnyCDNStorage owns an HttpClient, so a fresh instance per call leaks sockets under bulk-import throughput.
    private static readonly ConcurrentDictionary<string, BunnyCDNStorage> _storageClients = new();

    private static BunnyCDNStorage GetStorage(string zoneName, string secret) =>
        _storageClients.GetOrAdd(zoneName, _ => new BunnyCDNStorage(zoneName, secret, "de"));

    static BunnyCdnHelper()
    {
        var configuration = new ConfigurationBuilder()
                            .SetBasePath(Directory.GetCurrentDirectory())
                            .AddJsonFile(Path.Combine(Environment.CurrentDirectory, "..", "Shared", "sharedsettings.json"), optional: true)
                            .AddJsonFile(Path.Combine(Environment.CurrentDirectory, "Shared", "sharedsettings.json"), optional: true)
                            .AddJsonFile("sharedsettings.json", optional: true)
                            .AddJsonFile("appsettings.json", optional: true)
                            .AddEnvironmentVariables()
                            .Build();

        _secret = configuration.GetValue<string>("BunnyCdnSecret");
        _storageZoneName = configuration.GetValue<string>("BunnyCdnStorageZone");
        _cdnBaseUrl = configuration.GetValue<string>("CdnBaseUrl");
        _apiKey = configuration.GetValue<string>("BunnyCdnApiKey");
        _securePullZoneUrl = configuration.GetValue<string>("CdnSecurePullZoneUrl");
        _tokenAuthKey = configuration.GetValue<string>("CdnTokenAuthKey");
        _userStorageZoneName = configuration.GetValue<string>("BunnyCdnUserStorageZone");
        _userSecret = configuration.GetValue<string>("BunnyCdnUserSecret");
    }

    /// <summary>
    /// Picks the storage-zone credentials and public base URL for an operation. User media (<paramref
    /// name="secure"/> = true) lives in a separate storage zone served by the token-authed pull zone
    /// </summary>
    private static (string ZoneName, string Secret, string BaseUrl) ResolveTarget(bool secure)
    {
        if (secure && !string.IsNullOrEmpty(_userStorageZoneName) && !string.IsNullOrEmpty(_userSecret))
            return (_userStorageZoneName!, _userSecret!, _securePullZoneUrl ?? _cdnBaseUrl!);

        return (_storageZoneName!, _secret!, _cdnBaseUrl!);
    }

    public BunnyCdnHelper()
    {
    }

    public static async Task<string> UploadFile(byte[] file, string fileName, bool secure = false)
    {
        var (zoneName, secret, baseUrl) = ResolveTarget(secure);
        var bunnyCDNStorage = GetStorage(zoneName, secret);

        var stream = new MemoryStream(file);
        await bunnyCDNStorage.UploadAsync(stream, $"{zoneName}/{fileName}");

        return $"{baseUrl}/{fileName}";
    }

    public static async Task DeleteFile(string storagePath, bool secure = false)
    {
        var (zoneName, secret, _) = ResolveTarget(secure);
        var bunnyCDNStorage = GetStorage(zoneName, secret);
        await bunnyCDNStorage.DeleteObjectAsync($"{zoneName}/{storagePath}");
    }

    public static async Task<byte[]?> DownloadFile(string storagePath, bool secure = false)
    {
        var (zoneName, secret, _) = ResolveTarget(secure);
        var bunnyCDNStorage = GetStorage(zoneName, secret);
        try
        {
            await using var stream = await bunnyCDNStorage.DownloadObjectAsStreamAsync($"{zoneName}/{storagePath}");
            if (stream == null)
                return null;

            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return ms.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string GetCdnUrl(string storagePath) => $"{_cdnBaseUrl}/{storagePath}";

    /// <summary>
    /// Token-authenticated URL for a file served from the secured pull zone. When the secure-zone config is
    /// missing (dev), logs a one-time warning and returns the plain CDN URL so playback still works locally.
    /// </summary>
    public static string GetSignedUrl(string storagePath, TimeSpan ttl)
    {
        if (string.IsNullOrEmpty(_securePullZoneUrl) || string.IsNullOrEmpty(_tokenAuthKey))
        {
            if (Interlocked.Exchange(ref _signedUrlFallbackWarned, 1) == 0)
                Console.WriteLine($"[{DateTime.UtcNow:O}] Warning: CdnSecurePullZoneUrl/CdnTokenAuthKey not configured; " +
                                  "serving unsigned CDN URLs for card media.");
            return GetCdnUrl(storagePath);
        }

        var urlPath = "/" + storagePath.TrimStart('/');
        var expires = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds();
        return BunnyTokenAuth.BuildSignedUrl(_securePullZoneUrl, _tokenAuthKey, urlPath, expires);
    }

    /// <summary>
    /// Purges the pull-zone cache for a single URL so an overwritten file is refreshed immediately.
    /// Best-effort: silently no-ops when no account API key is configured and never throws.
    /// </summary>
    public static async Task PurgeUrl(string cdnUrl)
    {
        if (string.IsNullOrEmpty(_apiKey) || string.IsNullOrEmpty(cdnUrl))
            return;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                                                 $"https://api.bunny.net/purge?url={Uri.EscapeDataString(cdnUrl)}&async=false");
            request.Headers.Add("AccessKey", _apiKey);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                Console.WriteLine($"[{DateTime.UtcNow:O}] Warning: CDN purge for {cdnUrl} returned {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.UtcNow:O}] Warning: CDN purge for {cdnUrl} failed: {ex.Message}");
        }
    }
}