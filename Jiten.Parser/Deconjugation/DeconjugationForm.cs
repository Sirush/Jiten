namespace Jiten.Parser;

public sealed class DeconjugationForm : IEquatable<DeconjugationForm>
{
    // Arrays are never mutated after construction; exposed directly.
    private readonly string[] _tags;
    private readonly string[] _process;
    // Texts reached along the chain, root first, duplicate-free. Stored as a persistent chain so a
    // child form shares its parent's nodes instead of copying them; compared as a set.
    private readonly SeenTextNode? _seen;
    private string[]? _seenArray;
    private readonly int _hashCode;

    public string[] Tags => _tags;
    public string Text { get; }
    public string OriginalText { get; }
    public string[] Process => _process;

    /// Chain order matters to callers: compound matching sorts these with an unstable sort.
    public string[] SeenText => _seenArray ??= SeenTextNode.ToArray(_seen);

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
        if (seenText != null)
            foreach (var s in seenText)
                if (!string.IsNullOrEmpty(s))
                    _seen = SeenTextNode.Append(_seen, s);

        _hashCode = ComputeHash(Text, OriginalText, _tags, _process, _seen);
    }

    internal DeconjugationForm(
        string text,
        string originalText,
        string[] tags,
        SeenTextNode? seen,
        string[] process)
    {
        Text = text;
        OriginalText = originalText;
        _tags = tags;
        _process = process;
        _seen = seen;

        _hashCode = ComputeHash(text, originalText, tags, process, seen);
    }

    internal SeenTextNode? SeenChain => _seen;

    private static int ComputeHash(string text, string originalText, string[] tags, string[] process, SeenTextNode? seen)
    {
        var hash = new HashCode();
        hash.Add(text, StringComparer.Ordinal);
        hash.Add(originalText, StringComparer.Ordinal);

        foreach (var tag in tags)
            hash.Add(tag, StringComparer.Ordinal);

        foreach (var step in process)
            hash.Add(step, StringComparer.Ordinal);

        hash.Add(seen?.SetHash ?? 0);

        return hash.ToHashCode();
    }

    public bool Equals(DeconjugationForm? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return _hashCode == other._hashCode &&
               Text == other.Text &&
               OriginalText == other.OriginalText &&
               _tags.AsSpan().SequenceEqual(other._tags) &&
               _process.AsSpan().SequenceEqual(other._process) &&
               SeenTextNode.SetEquals(_seen, other._seen);
    }

    public override bool Equals(object? obj) => Equals(obj as DeconjugationForm);

    public override int GetHashCode() => _hashCode;
}

/// Immutable linked set of chain texts; the newest text is the head, so appending never copies.
internal sealed class SeenTextNode
{
    public readonly string Text;
    public readonly SeenTextNode? Prev;
    public readonly int Count;
    /// XOR of the members' ordinal hashes: order-independent, so equal sets hash equal.
    public readonly int SetHash;

    private SeenTextNode(string text, SeenTextNode? prev)
    {
        Text = text;
        Prev = prev;
        Count = (prev?.Count ?? 0) + 1;
        SetHash = (prev?.SetHash ?? 0) ^ StringComparer.Ordinal.GetHashCode(text);
    }

    public static bool Contains(SeenTextNode? node, string text)
    {
        for (; node != null; node = node.Prev)
            if (string.Equals(node.Text, text, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// Returns the same chain when the text is already a member.
    public static SeenTextNode Append(SeenTextNode? chain, string text) =>
        Contains(chain, text) ? chain! : new SeenTextNode(text, chain);

    public static bool SetEquals(SeenTextNode? a, SeenTextNode? b)
    {
        if (ReferenceEquals(a, b)) return true;
        int countA = a?.Count ?? 0, countB = b?.Count ?? 0;
        if (countA != countB || (a?.SetHash ?? 0) != (b?.SetHash ?? 0)) return false;
        for (var node = a; node != null; node = node.Prev)
            if (!Contains(b, node.Text))
                return false;
        return true;
    }

    public static string[] ToArray(SeenTextNode? chain)
    {
        if (chain == null) return [];
        var result = new string[chain.Count];
        for (var node = chain; node != null; node = node.Prev)
            result[node.Count - 1] = node.Text;
        return result;
    }
}
