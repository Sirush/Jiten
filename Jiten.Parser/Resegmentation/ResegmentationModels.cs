namespace Jiten.Parser.Resegmentation;

internal sealed record SpanTokenCandidate(int StartChar, int Length, List<int> WordIds);

internal sealed record SpanPath(List<SpanTokenCandidate> Segments)
{
    public bool IsComplete(int spanLength) =>
        Segments.Count > 0 && Segments[^1].StartChar + Segments[^1].Length == spanLength;
}

internal sealed class UncertainSpan
{
    public int WordIndex { get; init; }
    public string Text   { get; init; } = "";
    public int Position  { get; init; }
    public int Length    { get; init; }

    /// Flagged only because the whole-span token resolves exclusively to JMnedict name entries
    /// (一ッ岳/ひとつだけ). The trivial full-span "split" just re-finds that name, so it must be
    /// excluded — only a genuine multi-segment path (一つ + だけ) is a real resegmentation.
    public bool NameOnly { get; init; }
}
