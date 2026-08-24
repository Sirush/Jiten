using System.ComponentModel.DataAnnotations;

namespace Jiten.Api.Dtos.Requests;

public class SavePollOptionRequest
{
    public int? Id { get; set; }

    [Required]
    [StringLength(200, MinimumLength = 1)]
    public required string Text { get; set; }

    public int SortOrder { get; set; }
}

public class SavePollRequest
{
    [Required]
    [StringLength(300, MinimumLength = 1)]
    public required string Question { get; set; }

    [StringLength(2000)]
    public string? DescriptionMarkdown { get; set; }

    [Range(1, 20)]
    public int MaxSelections { get; set; } = 1;

    public DateTime? ClosesAt { get; set; }

    [MinLength(2)]
    public List<SavePollOptionRequest> Options { get; set; } = [];
}

public class SubmitPollVoteRequest
{
    public List<int> OptionIds { get; set; } = [];
}
