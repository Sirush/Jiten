using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class DeckDetailDto
{
    public DeckDto? ParentDeck { get; set; }
    public DeckDto MainDeck { get; set; } = new();
    public List<DeckDto> SubDecks { get; set; } = new();

    /// <summary>The signed-in viewer's score, note, privacy and pin for the title, as the deck details endpoint returns them; null without a preference row on the title's main entry.</summary>
    public DeckDetailsDto? ViewerDetails { get; set; }
}