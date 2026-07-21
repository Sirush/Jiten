namespace Jiten.Core;

public interface ICdnService
{
    Task<string> UploadFile(byte[] file, string fileName);
    Task DeleteFile(string storagePath);
    string GetCdnUrl(string storagePath);

    /// <summary>
    /// Fetches a file's bytes straight from the storage zone (bypassing the pull-zone cache, which is not
    /// purged when a file is overwritten). Returns null when the file doesn't exist.
    /// </summary>
    Task<byte[]?> DownloadFile(string storagePath);
}
