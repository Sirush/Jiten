namespace Jiten.Core.Data;

public class DeckRawText
{
    public int DeckId { get; set; }
    public string RawText { get; set; } = string.Empty;
    
    public Deck Deck { get; set; } = null!;

    public DeckRawText()
    {
        
    }

    public DeckRawText(string rawText)
    {
        RawText = rawText;
    }
}