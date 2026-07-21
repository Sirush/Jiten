using BunnyCDN.Net.Storage;
using Microsoft.Extensions.Configuration;

namespace Jiten.Core;

public class BunnyCdnHelper
{
    private static string? _secret;
    private static string? _storageZoneName;
    private static string? _cdnBaseUrl;
    private static string? _apiKey;
    private static readonly HttpClient _httpClient = new();

    static BunnyCdnHelper()
    {
        var configuration = new ConfigurationBuilder()
                            .SetBasePath(Directory.GetCurrentDirectory())
                            .AddJsonFile(Path.Combine(Environment.CurrentDirectory, "..", "Shared", "sharedsettings.json"), optional: true)
                            .AddJsonFile("sharedsettings.json", optional: true)
                            .AddJsonFile("appsettings.json", optional: true)
                            .AddEnvironmentVariables()
                            .Build();

        _secret = configuration.GetValue<string>("BunnyCdnSecret");
        _storageZoneName = configuration.GetValue<string>("BunnyCdnStorageZone");
        _cdnBaseUrl = configuration.GetValue<string>("CdnBaseUrl");
        _apiKey = configuration.GetValue<string>("BunnyCdnApiKey");
    }

    public BunnyCdnHelper()
    {
    }

    public static async Task<string> UploadFile(byte[] file, string fileName)
    {
        var bunnyCDNStorage = new BunnyCDNStorage(_storageZoneName, _secret, "de");

        var stream = new MemoryStream(file);
        await bunnyCDNStorage.UploadAsync(stream, $"{_storageZoneName}/{fileName}");

        return $"{_cdnBaseUrl}/{fileName}";
    }

    public static async Task DeleteFile(string storagePath)
    {
        var bunnyCDNStorage = new BunnyCDNStorage(_storageZoneName, _secret, "de");
        await bunnyCDNStorage.DeleteObjectAsync($"{_storageZoneName}/{storagePath}");
    }

    public static async Task<byte[]?> DownloadFile(string storagePath)
    {
        var bunnyCDNStorage = new BunnyCDNStorage(_storageZoneName, _secret, "de");
        try
        {
            await using var stream = await bunnyCDNStorage.DownloadObjectAsStreamAsync($"{_storageZoneName}/{storagePath}");
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