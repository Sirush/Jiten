using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class DeckVocabularyListDto
{
    public DeckDto? ParentDeck { get; set; }
    public Deck Deck { get; set; } = new();
    public List<WordDto> Words { get; set; } = new();

    /// <summary>Media type the ranks were read from; null means the site-wide ranking</summary>
    public MediaType? AppliedFrequencySource { get; set; }
}