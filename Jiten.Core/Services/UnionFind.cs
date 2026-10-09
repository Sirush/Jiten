namespace Jiten.Core.Services;

public sealed class UnionFind
{
    private readonly Dictionary<int, int> _parent = new();

    public int Find(int x)
    {
        if (_parent.TryAdd(x, x))
            return x;

        var root = x;
        while (_parent[root] != root)
            root = _parent[root];

        while (_parent[x] != root)
        {
            var next = _parent[x];
            _parent[x] = root;
            x = next;
        }

        return root;
    }

    public void Add(int x) => Find(x);

    public void Union(int a, int b)
    {
        var ra = Find(a);
        var rb = Find(b);
        if (ra != rb)
            _parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
    }

    public IEnumerable<List<int>> Components() =>
        _parent.Keys.ToList().GroupBy(Find).Select(g => g.ToList());
}
