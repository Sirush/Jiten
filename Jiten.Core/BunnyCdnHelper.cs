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
}