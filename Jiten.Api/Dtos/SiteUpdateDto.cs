namespace Jiten.Api.Dtos;

public class SiteUpdateDto
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public required string BodyMarkdown { get; set; }
    public DateTime PublishedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class AdminSiteUpdateDto
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public required string BodyMarkdown { get; set; }
    public string? NotificationTeaser { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime? NotifiedAt { get; set; }
}
