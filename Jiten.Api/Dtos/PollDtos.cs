namespace Jiten.Api.Dtos;

public class PollOptionDto
{
    public int Id { get; set; }
    public required string Text { get; set; }
    public int SortOrder { get; set; }

    /// <summary>Null until the caller may see results.</summary>
    public int? VoteCount { get; set; }
}

public class PollDto
{
    public int Id { get; set; }
    public required string Question { get; set; }
    public string? DescriptionMarkdown { get; set; }
    public int MaxSelections { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime? ClosesAt { get; set; }
    public bool IsClosed { get; set; }
    public List<int> MyOptionIds { get; set; } = [];
    public bool ResultsVisible { get; set; }

    /// <summary>Distinct voters</summary>
    public int? TotalVoters { get; set; }

    public List<PollOptionDto> Options { get; set; } = [];
}

public class AdminPollOptionDto
{
    public int Id { get; set; }
    public required string Text { get; set; }
    public int SortOrder { get; set; }
    public int VoteCount { get; set; }
}

public class AdminPollDto
{
    public int Id { get; set; }
    public required string Question { get; set; }
    public string? DescriptionMarkdown { get; set; }
    public int MaxSelections { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime? ClosesAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public bool IsClosed { get; set; }
    public int TotalVoters { get; set; }
    public List<AdminPollOptionDto> Options { get; set; } = [];
}
