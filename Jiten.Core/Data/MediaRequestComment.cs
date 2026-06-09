namespace Jiten.Core.Data;

public class MediaRequestComment
{
    public int Id { get; set; }
    public int MediaRequestId { get; set; }
    public required string UserId { get; set; }
    public string? Text { get; set; }
    public int? ParentCommentId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public MediaRequest? MediaRequest { get; set; }
    public MediaRequestUpload? Upload { get; set; }
    public MediaRequestComment? ParentComment { get; set; }
    public ICollection<MediaRequestComment> AdminComments { get; set; } = new List<MediaRequestComment>();
}
