using Jiten.Core;

namespace Jiten.Parser.Tests.Integration.Infrastructure;

public class StubCdnService : ICdnService
{
    public List<(byte[] File, string FileName)> Uploads { get; } = [];
    public List<string> Deletions { get; } = [];

    public Task<string> UploadFile(byte[] file, string fileName)
    {
        Uploads.Add((file, fileName));
        return Task.FromResult($"https://cdn.test/{fileName}");
    }

    public Task DeleteFile(string storagePath)
    {
        Deletions.Add(storagePath);
        return Task.CompletedTask;
    }

    public string GetCdnUrl(string storagePath) => $"https://cdn.test/{storagePath}";

    public Task<byte[]?> DownloadFile(string storagePath)
    {
        var match = Uploads.LastOrDefault(u => u.FileName == storagePath);
        return Task.FromResult(match.File);
    }
}
