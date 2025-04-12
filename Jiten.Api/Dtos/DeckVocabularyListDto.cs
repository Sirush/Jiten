using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class DeckVocabularyListDto
{
    public DeckDto? ParentDeck { get; set; }
    public Deck Deck { get; set; } = new();
    public List<WordDto> Words { get; set; } = new();
}