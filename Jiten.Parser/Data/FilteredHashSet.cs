namespace Jiten.Parser;

/// <summary>Exact set of 64-bit hashes behind a 4 MB bit filter; most probes miss, and the filter stays in cache where the set cannot.</summary>
internal sealed class FilteredHashSet
{
    private const int FilterBits = 25;

    private readonly HashSet<long> _set;
    private readonly ulong[] _filter = new ulong[(1 << FilterBits) / 64];

    public FilteredHashSet(int capacity) => _set = new HashSet<long>(capacity);

    public void Add(long hash)
    {
        if (!_set.Add(hash)) return;
        ulong slot = Slot(hash);
        _filter[slot >> 6] |= 1UL << (int)slot;
    }

    public bool Contains(long hash)
    {
        ulong slot = Slot(hash);
        return (_filter[slot >> 6] & (1UL << (int)slot)) != 0 && _set.Contains(hash);
    }

    private static ulong Slot(long hash) => unchecked((ulong)hash * 0x9E3779B97F4A7C15UL) >> (64 - FilterBits);
}
