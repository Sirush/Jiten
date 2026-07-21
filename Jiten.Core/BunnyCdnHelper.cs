using BunnyCDN.Net.Storage;
using Microsoft.Extensions.Configuration;

namespace Jiten.Core;

public class BunnyCdnHelper
{
    private static string? _secret;
    private static string? _storageZoneName;
    private static string? _cdnBaseUrl;

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
}