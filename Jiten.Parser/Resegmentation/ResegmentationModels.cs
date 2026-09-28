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

    /// <summary>Span resolves only to JMnedict names (ひとつだけ → 一ッ岳); only a multi-segment path (一つ+だけ) counts.</summary>
    public bool NameOnly { get; init; }
}
