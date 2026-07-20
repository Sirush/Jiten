using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class MediaRequestDto
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public MediaType MediaType { get; set; }
    public string? ExternalUrl { get; set; }
    public LinkType? ExternalLinkType { get; set; }
    public string? Description { get; set; }
    public MediaRequestStatus Status { get; set; }
    public string? AdminNote { get; set; }
    public int? FulfilledDeckId { get; set; }
    public string? FulfilledDeckTitle { get; set; }
    public int UpvoteCount { get; set; }
    public int BoostCount { get; set; }
    public int CommentCount { get; set; }
    public int UploadCount { get; set; }
    public bool HasUserUpvoted { get; set; }
    public bool HasUserBoosted { get; set; }
    public bool IsSubscribed { get; set; }
    public bool IsOwnRequest { get; set; }
    public string? RequesterName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
