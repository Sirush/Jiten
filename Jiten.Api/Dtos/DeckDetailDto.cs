using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class DeckDetailDto
{
    public DeckDto? ParentDeck { get; set; }
    public DeckDto MainDeck { get; set; } = new();
    public List<DeckDto> SubDecks { get; set; } = new();
}