using Jiten.Parser.Scoring;

namespace Jiten.Parser.Resegmentation;

internal sealed record SpanTokenCandidate(int StartChar, int Length, List<int> WordIds)
{
    public bool IsGap => WordIds.Count == 0;
}

internal sealed record SpanPath(List<SpanTokenCandidate> Segments)
{
    public bool IsComplete(int spanLength) =>
        Segments.Count > 0 && Segments[^1].StartChar + Segments[^1].Length == spanLength;

    public int GapChars => Segments.Where(s => s.IsGap).Sum(s => s.Length);

    public int GapCost => GapChars * Constants.UncoveredCharPenalty;
}

internal sealed class UncertainSpan
{
    public int WordIndex { get; init; }
    public string Text   { get; init; } = "";
    public int Position  { get; init; }
    public int Length    { get; init; }
}
