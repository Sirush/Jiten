namespace Jiten.Core.Data.JMDict;

/// <summary>Reading-level frequency within one media type. Absent row means the reading was never observed in that type.</summary>
public class JmDictWordFormFrequencyByType
{
    public MediaType MediaType { get; set; }
    public int WordId { get; set; }
    public short ReadingIndex { get; set; }
    public int FrequencyRank { get; set; }
    public double FrequencyPercentage { get; set; }
    public double ObservedFrequency { get; set; }
    public int UsedInMediaAmount { get; set; }
}
