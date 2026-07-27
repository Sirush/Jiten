namespace Jiten.Core;

public interface ICdnService
{
    /// <param name="secure">
    /// When true, targets the user-media storage zone served by the token-authed pull zone instead of the
    /// public zone. Reads of these files must use <see cref="GetSignedUrl"/>.
    /// </param>
    Task<string> UploadFile(byte[] file, string fileName, bool secure = false);

    Task DeleteFile(string storagePath, bool secure = false);
    string GetCdnUrl(string storagePath);

    /// <summary>
    /// Returns a token-authenticated URL for a private (secured pull zone) file that expires after
    /// <paramref name="ttl"/>. Falls back to the plain CDN URL when secure-zone config is absent so dev
    /// environments keep working.
    /// </summary>
    string GetSignedUrl(string storagePath, TimeSpan ttl);

    /// <summary>
    /// Purges the pull-zone cache for a single CDN URL so an overwritten file is refreshed immediately.
    /// Best-effort: never throws.
    /// </summary>
    Task PurgeUrl(string cdnUrl);

    /// <summary>
    /// Fetches a file's bytes straight from the storage zone (bypassing the pull-zone cache, which is not
    /// purged when a file is overwritten). Returns null when the file doesn't exist.
    /// </summary>
    Task<byte[]?> DownloadFile(string storagePath, bool secure = false);
}
