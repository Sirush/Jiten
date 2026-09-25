namespace Jiten.Core.Data;

public class DeckRawText
{
    public int DeckId { get; set; }
    public string RawText { get; set; } = string.Empty;

    /// <summary>Subtitle decks only: bit i set when a sentence ends after line i of <see cref="RawText"/>; null until first computed.</summary>
    public byte[]? SpeechBoundaries { get; set; }
    
    public Deck Deck { get; set; } = null!;

    public DeckRawText()
    {
        
    }

    public DeckRawText(string rawText)
    {
        RawText = rawText;
    }
}