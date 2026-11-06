namespace Jiten.Core.Data.JMDict;

public class JmDictWordForm
{
    public int WordId { get; set; }
    public short ReadingIndex { get; set; }
    public string Text { get; set; } = "";
    public string RubyText { get; set; } = "";
    public JmDictFormType FormType { get; set; }
    public bool IsActiveInLatestSource { get; set; } = true;
    public List<string>? Priorities { get; set; }
    public List<string>? InfoTags { get; set; }
    public bool IsObsolete { get; set; }
    public bool IsNoKanji { get; set; }
    public bool IsSearchOnly { get; set; }
    public List<int>? PitchAccents { get; set; }
}
