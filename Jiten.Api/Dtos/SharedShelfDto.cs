using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class SharedShelfDto
{
    public bool ViewerHasList { get; set; }
    public int SharedCount { get; set; }
    public int TheirsOnlyCount { get; set; }
    public List<SharedShelfItemDto> Shared { get; set; } = [];

    /// <summary>Most ready first by the viewer's coverage.</summary>
    public List<SharedShelfItemDto> TheirsOnly { get; set; } = [];
}

public class SharedShelfItemDto
{
    /// <summary>Coverage on the deck is always the viewer's; the profile owner's coverage is never exposed.</summary>
    public DeckCardDto Deck { get; set; } = new();

    public DeckStatus YourStatus { get; set; }
}
