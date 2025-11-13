namespace Jiten.Cli.NGrams;

public class NgramExtractionConfig
{
    /// <summary>
    /// Window sizes to extract (e.g., [2, 3, 5] for bigrams, trigrams, 5-grams)
    /// </summary>
    public List<int> WindowSizes { get; set; } = new() { 2, 3, 5 };
    
    /// <summary>
    /// Specific (before, after) patterns to extract
    /// If null, extracts all combinations for each window size
    /// </summary>
    public List<(int before, int after)>? SpecificPatterns { get; set; }
    
    /// <summary>
    /// Maximum tokens before the target word
    /// </summary>
    public int MaxTokensBefore { get; set; } = 3;
    
    /// <summary>
    /// Maximum tokens after the target word
    /// </summary>
    public int MaxTokensAfter { get; set; } = 3;
}