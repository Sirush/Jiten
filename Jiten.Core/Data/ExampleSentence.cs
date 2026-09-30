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

    /// <summary>Packed resolved tokens, see <see cref="ExampleSentenceTokens"/>.</summary>
    public required byte[] Tokens { get; set; }

    public required int[] WordKeys { get; set; }

    public Deck? Deck { get; set; }
}
