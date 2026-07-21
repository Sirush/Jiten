namespace Jiten.Core;

public class BunnyCdnService : ICdnService
{
    public Task<string> UploadFile(byte[] file, string fileName)
        => BunnyCdnHelper.UploadFile(file, fileName);

    public Task DeleteFile(string storagePath)
        => BunnyCdnHelper.DeleteFile(storagePath);

    public string GetCdnUrl(string storagePath)
        => BunnyCdnHelper.GetCdnUrl(storagePath);

    public Task<byte[]?> DownloadFile(string storagePath)
        => BunnyCdnHelper.DownloadFile(storagePath);
}
