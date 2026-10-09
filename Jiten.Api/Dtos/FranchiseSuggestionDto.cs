using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

/// <summary>Decks whose titles share a root, at least one of them outside any franchise.</summary>
public class FranchiseSuggestionDto
{
    public string Root { get; set; } = "";
    public string RootKey { get; set; } = "";

    /// <summary>Deck the franchise builder opens on: the best-represented franchise's earliest deck, else the earliest deck.</summary>
    public int AnchorDeckId { get; set; }

    /// <summary>Earliest release first.</summary>
    public List<FranchiseSuggestionDeckDto> Decks { get; set; } = new();
}

public class FranchiseSuggestionDeckDto
{
    public FranchiseNodeDto Deck { get; set; } = new();
    public int? FranchiseId { get; set; }
    public string? FranchiseName { get; set; }
    public List<LinkType> LinkTypes { get; set; } = new();
}

public enum FranchiseSuggestionScope
{
    All = 0,

    /// <summary>At least one deck already has a franchise the others could join.</summary>
    Franchise = 1,

    /// <summary>No deck has a franchise yet.</summary>
    Unlinked = 2
}
