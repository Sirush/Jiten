namespace Jiten.Core.Data.JMDict;

public class JmDictDefinition
{
    public int DefinitionId { get; init; }
    public int WordId { get; init; }
    public List<string> PartsOfSpeech { get; init; } = new();
    public List<string> EnglishMeanings { get; init; } = new();

    public int SenseIndex { get; set; }
    public List<string> Pos { get; set; } = [];
    public List<string> Misc { get; set; } = [];
    public List<string> Field { get; set; } = [];
    public List<string> Dial { get; set; } = [];

    /// <summary>s_inf sense notes ("usu. written in kana", register/nuance). One entry per &lt;s_inf&gt;.</summary>
    public List<string> SenseInfo { get; set; } = [];

    /// <summary>g_type per gloss, index-aligned with <see cref="EnglishMeanings"/>. "" = plain gloss; otherwise lit/fig/expl/tm.</summary>
    public List<string> GlossTypes { get; set; } = [];

    public List<short>? RestrictedToReadingIndices { get; set; }
    public bool IsActiveInLatestSource { get; set; } = true;
}
