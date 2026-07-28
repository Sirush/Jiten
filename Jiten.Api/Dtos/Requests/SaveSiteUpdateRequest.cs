using System.ComponentModel.DataAnnotations;

namespace Jiten.Api.Dtos.Requests;

public class SaveSiteUpdateRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public required string Title { get; set; }

    [Required]
    [StringLength(50000, MinimumLength = 1)]
    public required string BodyMarkdown { get; set; }

    [StringLength(300)]
    public string? NotificationTeaser { get; set; }
}
