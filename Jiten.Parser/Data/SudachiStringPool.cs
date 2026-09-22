using System.Text;

namespace Jiten.Parser;

/// <summary>Dedupes the strings decoded from one Sudachi output stream; token forms repeat heavily.</summary>
public sealed class SudachiStringPool
{
    private readonly HashSet<string> _strings = new(StringComparer.Ordinal);
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    public SudachiStringPool() => _lookup = _strings.GetAlternateLookup<ReadOnlySpan<char>>();

    public string GetString(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
            return string.Empty;

        Span<char> chars = utf8.Length <= 256 ? stackalloc char[utf8.Length] : new char[utf8.Length];
        chars = chars[..Encoding.UTF8.GetChars(utf8, chars)];
        if (!_lookup.TryGetValue(chars, out var s))
        {
            s = new string(chars);
            _strings.Add(s);
        }
        return s;
    }
}
