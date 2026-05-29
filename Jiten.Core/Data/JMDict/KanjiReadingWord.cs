namespace Jiten.Core.Data.JMDict;

public class KanjiReadingWord
{
    public string KanjiCharacter { get; set; } = "";
    public string Reading { get; set; } = "";
    public int WordId { get; set; }
    public short ReadingIndex { get; set; }
}
