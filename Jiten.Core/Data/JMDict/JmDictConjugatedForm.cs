namespace Jiten.Core.Data.JMDict;

public class JmDictConjugatedForm
{
    public int Id { get; set; }
    public string Surface { get; set; } = string.Empty;
    public int WordId { get; set; }
    public List<string> ConjugationChain { get; set; } = new();
    public short FormIndex { get; set; }
}
