using System.ComponentModel.DataAnnotations;
using Jiten.Core.Data;

namespace Jiten.Api.Dtos.Requests;

public class CreateWordSetRequest
{
    [Required]
    [MaxLength(50)]
    [RegularExpression(@"^[a-z0-9]+(-[a-z0-9]+)*$", ErrorMessage = "Slug must be lowercase alphanumeric with hyphens")]
    public required string Slug { get; set; }

    [Required]
    [MaxLength(100)]
    public required string Name { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }
}

public class UpdateWordSetRequest
{
    [Required]
    [MaxLength(50)]
    [RegularExpression(@"^[a-z0-9]+(-[a-z0-9]+)*$", ErrorMessage = "Slug must be lowercase alphanumeric with hyphens")]
    public required string Slug { get; set; }

    [Required]
    [MaxLength(100)]
    public required string Name { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }
}

public class AddWordSetMembersRequest
{
    [Required]
    [MaxLength(500)]
    public required List<WordSetMemberInput> Members { get; set; }
}

public class RemoveWordSetMembersRequest
{
    [Required]
    [MaxLength(500)]
    public required List<WordSetMemberInput> Members { get; set; }
}

public class WordSetMemberInput
{
    public int WordId { get; set; }
    public short ReadingIndex { get; set; }
}

public enum UnmatchedMemberAction
{
    Keep = 0,
    MarkManual = 1,
    Remove = 2
}

public class PreviewWordSetRuleRequest
{
    [Required]
    public required WordSetRule Rule { get; set; }
}

public class ApplyWordSetRuleRequest
{
    [Required]
    public required WordSetRule Rule { get; set; }

    public UnmatchedMemberAction Unmatched { get; set; }
}
