using Jiten.Core.Data;

namespace Jiten.Cli.NGrams;

public class NgramSource
{
    public int NgramId { get; set; }
    public int ExampleSentenceId { get; set; }
    public short WordPosition { get; set; }

    // Navigation
    public PrecomputedNgram Ngram { get; set; } = null!;
    public ExampleSentence ExampleSentence { get; set; } = null!;
}