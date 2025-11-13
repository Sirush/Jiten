namespace Jiten.Cli.NGrams;

public class PrecomputedNgram
{
    public int NgramId { get; set; }
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public string ContextBefore { get; set; } = string.Empty;
    public string ContextAfter { get; set; } = string.Empty;
    public short ContextSize { get; set; }
    public short TokensBefore { get; set; }
    public short TokensAfter { get; set; }
    public string FullContext { get; set; } = string.Empty;
    public int Occurrences { get; set; } = 1;
    public float SignificanceScore { get; set; }
    public float[]? BertEmbedding { get; set; }
    public bool BertEmbeddingComputed { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
    
    // Navigation
    public List<NgramSource> Sources { get; set; } = new();
}

