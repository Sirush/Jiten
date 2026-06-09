namespace Jiten.Api.Dtos;

public class MediaRequestCommentDto
{
    public int Id { get; set; }
    public string? Text { get; set; }
    public required string Role { get; set; }
    public bool IsOwnComment { get; set; }
    public bool IsAdminComment { get; set; }
    public string? UserName { get; set; }
    public object? Upload { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public List<MediaRequestCommentDto>? AdminComments { get; set; }
}
