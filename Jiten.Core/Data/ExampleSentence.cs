using Jiten.Core.Data.JMDict;

namespace Jiten.Core.Data;

public class ExampleSentence
{
    public long SentenceId { get; set; }
    public int DeckId { get; set; }
    public required string Text { get; set; }
    
    /// <summary>
    /// Position i.e. id of the sentence it appears in
    /// </summary>
    public int Position { get; set; }
    public float Difficulty { get; set; }
    public List<ExampleSentenceWord> Words { get; set; } = new();

    public Deck? Deck { get; set; }
}

public class ExampleSentenceWord
{
    public long ExampleSentenceId { get; set; }
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public byte Position { get; set; }
    public byte Length { get; set; }
    
    public ExampleSentence? ExampleSentence { get; set; }
    public JmDictWord? Word { get; set; }
}