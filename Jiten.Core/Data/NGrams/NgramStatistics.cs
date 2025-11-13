namespace Jiten.Cli.NGrams;

public class NgramStatistics
{
    public int WordId { get; set; }
    public int TotalNgrams { get; set; }
    public int SignificantNgrams { get; set; }
    public float AvgSignificanceScore { get; set; }
    public int BertEmbeddingsComputed { get; set; }
    public DateTimeOffset? LastProcessed { get; set; }
    public float? AmbiguityScore { get; set; }
}