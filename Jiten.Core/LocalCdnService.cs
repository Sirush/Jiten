using Microsoft.Extensions.Configuration;

namespace Jiten.Core;

/// <summary>
/// Local-development replacement for Bunny. Files are stored below StaticFilesPath and served by the
/// API's /static middleware. This is intentionally selected only when UseBunnyCdn is false; production
/// should use BunnyCdnService so user media remains private and CDN-backed.
/// </summary>
public sealed class LocalCdnService(IConfiguration configuration) : ICdnService
{
    private readonly string root = Path.GetFullPath(
        configuration["StaticFilesPath"] ?? throw new InvalidOperationException("StaticFilesPath is required for local CDN storage."));
    // The web app runs on a different origin in development, so a root-relative URL would be handled
    // by Nuxt/Vue Router instead of the API's static-file middleware.
    private readonly string baseUrl = (configuration["ApiBaseUrl"]
                                      ?? configuration["urls"]?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                                      ?? configuration["ASPNETCORE_URLS"]?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                                      ?? "https://localhost:7299").TrimEnd('/');

    public async Task<string> UploadFile(byte[] file, string fileName, bool secure = false)
    {
        var path = ResolvePath(fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, file);
        return GetCdnUrl(fileName);
    }

    public Task DeleteFile(string storagePath, bool secure = false)
    {
        var path = ResolvePath(storagePath);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public string GetCdnUrl(string storagePath) => $"{baseUrl}/static/{QuotePath(storagePath)}";

    public string GetSignedUrl(string storagePath, TimeSpan ttl) => GetCdnUrl(storagePath);

    public Task PurgeUrl(string cdnUrl) => Task.CompletedTask;

    public async Task<byte[]?> DownloadFile(string storagePath, bool secure = false)
    {
        var path = ResolvePath(storagePath);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
    }

    private string ResolvePath(string storagePath)
    {
        var relative = storagePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !string.Equals(path, root, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid local CDN path.");
        return path;
    }

    private static string QuotePath(string storagePath) => string.Join('/', storagePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
}
