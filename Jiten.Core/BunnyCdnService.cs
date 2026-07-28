namespace Jiten.Core;

public class BunnyCdnService : ICdnService
{
    public Task<string> UploadFile(byte[] file, string fileName, bool secure = false)
        => BunnyCdnHelper.UploadFile(file, fileName, secure);

    public Task DeleteFile(string storagePath, bool secure = false)
        => BunnyCdnHelper.DeleteFile(storagePath, secure);

    public string GetCdnUrl(string storagePath)
        => BunnyCdnHelper.GetCdnUrl(storagePath);

    public string GetSignedUrl(string storagePath, TimeSpan ttl)
        => BunnyCdnHelper.GetSignedUrl(storagePath, ttl);

    public Task<byte[]?> DownloadFile(string storagePath, bool secure = false)
        => BunnyCdnHelper.DownloadFile(storagePath, secure);

    public Task PurgeUrl(string cdnUrl)
        => BunnyCdnHelper.PurgeUrl(cdnUrl);
}
