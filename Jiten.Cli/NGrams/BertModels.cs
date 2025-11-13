using Jiten.Core.Data.JMDict;

namespace Jiten.Cli.NGrams;

public class BertEmbedding
{
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public int TokenCount { get; set; }
    public int[] TokenIds { get; set; } = Array.Empty<int>();
}

public class CandidateScore
{
    public JmDictWord Candidate { get; set; } = null!;
    public float BertScore { get; set; }
    public int PriorityScore { get; set; }
}

public class DisambiguationResult
{
    public JmDictWord SelectedWord { get; set; } = null!;
    public float Confidence { get; set; }
    public string Method { get; set; } = string.Empty;
}
