namespace Jiten.Core.Data.JMDict;

/// <summary>Word-level frequency within one media type. Absent row means the word was never observed in that type.</summary>
public class JmDictWordFrequencyByType
{
    public MediaType MediaType { get; set; }
    public int WordId { get; set; }
    public int FrequencyRank { get; set; }
    public int UsedInMediaAmount { get; set; }
    public double ObservedFrequency { get; set; }
}
