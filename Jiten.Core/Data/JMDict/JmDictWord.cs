namespace Jiten.Core.Data.JMDict;

public class JmDictWord
{
    public int WordId { get; set; }
    public List<string> Readings { get; set; } = new();
    public List<string> ReadingsFurigana { get; set; } = new();
    public List<JmDictReadingType> ReadingTypes { get; set; } = new();
    public List<string>? ObsoleteReadings { get; set; } = new();
    public List<string> PartsOfSpeech { get; set; } = new();
    public List<JmDictDefinition> Definitions { get; set; } = new();
    public List<JmDictLookup> Lookups { get; set; } = new();
    public List<int>? PitchAccents { get; set; } = new();
}