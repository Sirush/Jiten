using Jiten.Core;

namespace Jiten.Parser.Tests.Integration.Infrastructure;

public class StubCdnService : ICdnService
{
    public List<(byte[] File, string FileName)> Uploads { get; } = [];
    public List<string> Deletions { get; } = [];
    public List<string> Purges { get; } = [];

    public Task<string> UploadFile(byte[] file, string fileName, bool secure = false)
    {
        Uploads.Add((file, fileName));
        return Task.FromResult($"https://cdn.test/{fileName}");
    }

    public Task DeleteFile(string storagePath, bool secure = false)
    {
        Deletions.Add(storagePath);
        return Task.CompletedTask;
    }

    public string GetCdnUrl(string storagePath) => $"https://cdn.test/{storagePath}";

    public string GetSignedUrl(string storagePath, TimeSpan ttl) =>
        $"https://stub-cdn/{storagePath}?token=stub&expires=9999999999";

    public Task PurgeUrl(string cdnUrl)
    {
        Purges.Add(cdnUrl);
        return Task.CompletedTask;
    }

    public Task<byte[]?> DownloadFile(string storagePath, bool secure = false)
    {
        var match = Uploads.LastOrDefault(u => u.FileName == storagePath);
        return Task.FromResult(match.File);
    }
}
