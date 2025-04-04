using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class DeckWordDto
{
    public int DeckWordId { get; set; }
    public int DeckId { get; set; }
    public int WordId { get; set; }
    public string OriginalText { get; set; } = string.Empty;
    public byte ReadingIndex { get; set; }
    public int Occurrences { get; set; }
    public Deck Deck { get; set; } = new();
    public List<string> Conjugations { get; set; } = new();

    public DeckWordDto(string originalText)
    {
        OriginalText = originalText;
    }

    public DeckWordDto(DeckWord deckWord)
    {
        DeckWordId = deckWord.DeckWordId;
        DeckId = deckWord.DeckId;
        WordId = deckWord.WordId;
        OriginalText = deckWord.OriginalText;
        ReadingIndex = deckWord.ReadingIndex;
        Occurrences = deckWord.Occurrences;
        Deck = deckWord.Deck;
        Conjugations = deckWord.Conjugations;
    }
}