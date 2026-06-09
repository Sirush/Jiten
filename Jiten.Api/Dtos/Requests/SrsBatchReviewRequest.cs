using System.ComponentModel.DataAnnotations;
using Jiten.Core.Data.FSRS;

namespace Jiten.Api.Dtos.Requests;

public class SrsBatchReviewRequest
{
    [Required]
    public List<SrsBatchReviewItem> Reviews { get; set; } = new();
    public string? SessionId { get; set; }
}

public class SrsBatchReviewItem
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public FsrsRating Rating { get; set; }
}
