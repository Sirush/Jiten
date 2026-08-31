namespace Jiten.Parser;

public sealed class DeconjugationForm : IEquatable<DeconjugationForm>
{
    // Arrays are never mutated after construction; exposed directly as IReadOnlyList.
    private readonly string[] _tags;
    private readonly string[] _process;
    private readonly HashSet<string> _seenText;
    private readonly int _hashCode;

    public IReadOnlyList<string> Tags => _tags;
    public string Text { get; }
    public string OriginalText { get; }
    public IReadOnlySet<string> SeenText => _seenText;
    public IReadOnlyList<string> Process => _process;

    public DeconjugationForm(
        string text,
        string originalText,
        IEnumerable<string>? tags,
        IEnumerable<string>? seenText,
        IEnumerable<string>? process)
    {
        Text = text;
        OriginalText = originalText;
        _tags = tags?.Where(t => !string.IsNullOrEmpty(t)).ToArray() ?? [];
        _process = process?.Where(p => !string.IsNullOrEmpty(p)).ToArray() ?? [];
        _seenText = seenText != null
            ? new HashSet<string>(seenText.Where(s => !string.IsNullOrEmpty(s)), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        _hashCode = ComputeHash(Text, OriginalText, _tags, _process, _seenText);
    }

    internal DeconjugationForm(
        string text,
        string originalText,
        string[] tags,
        HashSet<string> seenText,
        string[] process)
    {
        Text = text;
        OriginalText = originalText;
        _tags = tags;
        _process = process;
        _seenText = seenText;

        _hashCode = ComputeHash(text, originalText, tags, process, seenText);
    }

    private static int ComputeHash(string text, string originalText, string[] tags, string[] process, HashSet<string> seenText)
    {
        var hash = new HashCode();
        hash.Add(text, StringComparer.Ordinal);
        hash.Add(originalText, StringComparer.Ordinal);

        foreach (var tag in tags)
            hash.Add(tag, StringComparer.Ordinal);

        foreach (var step in process)
            hash.Add(step, StringComparer.Ordinal);

        int seenTextHash = 0;
        foreach (var seen in seenText)
            seenTextHash ^= StringComparer.Ordinal.GetHashCode(seen);
        hash.Add(seenTextHash);

        return hash.ToHashCode();
    }

    public bool Equals(DeconjugationForm? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return Text == other.Text &&
               OriginalText == other.OriginalText &&
               _tags.AsSpan().SequenceEqual(other._tags) &&
               _process.AsSpan().SequenceEqual(other._process) &&
               SetsEqual(_seenText, other._seenText);
    }

    public override bool Equals(object? obj) => Equals(obj as DeconjugationForm);

    public override int GetHashCode() => _hashCode;

    private static bool SetsEqual(IReadOnlySet<string> left, IReadOnlySet<string> right)
    {
        if (left.Count != right.Count)
            return false;

        foreach (var item in left)
        {
            if (!right.Contains(item))
                return false;
        }

        return true;
    }
}
