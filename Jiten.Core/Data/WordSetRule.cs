namespace Jiten.Core.Data;

public enum WordSetScript
{
    Any = 0,
    KatakanaWord = 1,
    KatakanaForm = 2
}

public enum WordSetDictionary
{
    Any = 0,
    Vocabulary = 1,
    Names = 2
}

public class WordSetRule
{
    public List<string> IncludePos { get; set; } = [];
    public List<string> ExcludePos { get; set; } = [];
    public WordSetScript Script { get; set; }
    public WordSetDictionary Dictionary { get; set; }
    public List<WordOrigin> Origins { get; set; } = [];
    public List<string> SourceLanguages { get; set; } = [];
    public int? MaxFrequencyRank { get; set; }
    public bool AddKanaForm { get; set; } = true;

    /// <summary>A rule without any criterion would match the whole dictionary.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasCriteria => IncludePos.Count > 0
                               || Script != WordSetScript.Any
                               || Origins.Count > 0
                               || SourceLanguages.Count > 0
                               || MaxFrequencyRank != null;
}
