namespace Jiten.Api.Dtos;

public class MediaRequestUploadDto
{
    public int Id { get; set; }
    public long FileSize { get; set; }
    public int OriginalFileCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class MediaRequestUploadAdminDto : MediaRequestUploadDto
{
    public required string FileName { get; set; }
    public string? UploaderName { get; set; }
    public bool AdminReviewed { get; set; }
    public string? AdminNote { get; set; }
    public bool FileDeleted { get; set; }
}
