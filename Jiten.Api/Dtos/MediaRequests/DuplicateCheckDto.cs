using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class DuplicateCheckResultDto
{
    public List<DuplicateCheckDeckDto> ExistingDecks { get; set; } = [];
    public List<DuplicateCheckRequestDto> ExistingRequests { get; set; } = [];

    /// <summary>Active Update requests already filed against the same target deck, by anyone.</summary>
    public List<DuplicateCheckRequestDto> ExistingUpdateRequests { get; set; } = [];
}

public class DuplicateCheckDeckDto
{
    public int DeckId { get; set; }
    public required string Title { get; set; }
    public MediaType MediaType { get; set; }
}

public class DuplicateCheckRequestDto
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public MediaType MediaType { get; set; }
    public MediaRequestStatus Status { get; set; }
    public int UpvoteCount { get; set; }
}
