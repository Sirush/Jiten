namespace Jiten.Cli.NGrams;

public class DisambiguationConfig
{
    // N-gram settings
    public int ContextSize { get; set; } = 3;  // bigram=2, trigram=3, etc.
    public int TokensBefore { get; set; } = 1; // Words before target
    public int TokensAfter { get; set; } = 1;  // Words after target
    
    // BERT settings
    public string ModelPath { get; set; } = string.Empty;
    public string VocabPath { get; set; } = string.Empty;
    public int MaxSequenceLength { get; set; } = 128;
    public float BertWeight { get; set; } = 0.6f;      // Weight for BERT score
    public float PriorityWeight { get; set; } = 0.4f;  // Weight for priority score
    
    // Performance settings
    public bool EnableBertDisambiguation { get; set; } = false; // Disabled by default until model is available
    public int BatchSize { get; set; } = 32;
    public int MaxCandidates { get; set; } = 10;  // Limit candidates for BERT
    public int MaxConcurrentInferences { get; set; } = 4;
    
    // Fallback settings
    public bool FallbackToPriority { get; set; } = true;
    public float MinConfidenceThreshold { get; set; } = 0.3f;
}
