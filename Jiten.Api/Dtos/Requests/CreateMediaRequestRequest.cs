using System.ComponentModel.DataAnnotations;
using Jiten.Core.Data;

namespace Jiten.Api.Dtos.Requests;

public class CreateMediaRequestRequest
{
    /// <summary>Optional only for update requests, where it falls back to the target deck's title.</summary>
    [MaxLength(300)]
    public string? Title { get; set; }

    [Required]
    public MediaType MediaType { get; set; }

    public MediaRequestKind Kind { get; set; } = MediaRequestKind.New;

    public int? TargetDeckId { get; set; }

    [MaxLength(500)]
    public string? ExternalUrl { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }
}

public class UpdateRequestStatusRequest
{
    [Required]
    public MediaRequestStatus Status { get; set; }

    [MaxLength(500)]
    public string? AdminNote { get; set; }

    public int? FulfilledDeckId { get; set; }
}

public class AddMediaRequestCommentRequest
{
    [MaxLength(500)]
    public string? Text { get; set; }
}

public class AdminReviewUploadRequest
{
    [Required]
    public bool AdminReviewed { get; set; }

    [MaxLength(500)]
    public string? AdminNote { get; set; }
}

public class EditRequestDescriptionRequest
{
    [MaxLength(1000)]
    public string? Description { get; set; }

    [MaxLength(500)]
    public string? ExternalUrl { get; set; }

    /// <summary>Retargets an update request. Omitted leaves the current target untouched.</summary>
    public int? TargetDeckId { get; set; }
}

public class AdminEditMediaRequestRequest
{
    [Required, MaxLength(300)]
    public required string Title { get; set; }

    [Required]
    public MediaType MediaType { get; set; }

    public MediaRequestKind Kind { get; set; } = MediaRequestKind.New;

    public int? TargetDeckId { get; set; }

    [MaxLength(500)]
    public string? ExternalUrl { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }
}
