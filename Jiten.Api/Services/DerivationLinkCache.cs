using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public class DerivationLinkCache : IDerivationLinkCache
{
    private readonly IDbContextFactory<JitenDbContext> _contextFactory;
    private readonly ILogger<DerivationLinkCache> _logger;

    private readonly struct Edge(long baseKey, long derivedKey, DerivationCategory category, bool conductsInReverse)
    {
        public readonly long BaseKey = baseKey;
        public readonly long DerivedKey = derivedKey;
        public readonly DerivationCategory Category = category;

        /// <summary>False on one-way pairs, and on kanji-base/kana-derived rows: walking those backwards would
        /// let a kana form confer knowledge of a kanji one.</summary>
        public readonly bool ConductsInReverse = conductsInReverse;
    }

    private sealed class Graph
    {
        public Edge[] Edges = [];
        public Dictionary<long, int[]> Incident = new();
        public Dictionary<DerivationCategory, int> PairCounts = new();
    }

    private volatile Graph _graph = new();

    public DerivationLinkCache(IDbContextFactory<JitenDbContext> contextFactory, ILogger<DerivationLinkCache> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
        TryLoadData();
    }

    public bool IsEmpty => _graph.Edges.Length == 0;

    public IReadOnlyDictionary<DerivationCategory, int> PairCounts => _graph.PairCounts;

    public void Reload() => TryLoadData();

    private void TryLoadData()
    {
        try
        {
            LoadData();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DerivationLinkCache failed to load data from DB - serving with empty cache");
        }
    }

    private void LoadData()
    {
        using var context = _contextFactory.CreateDbContext();

        var rows = context.WordDerivations
                          .AsNoTracking()
                          .Select(d => new
                          {
                              d.BaseWordId, d.BaseReadingIndex, d.DerivedWordId, d.DerivedReadingIndex,
                              d.Category, d.Direction
                          })
                          .ToList();

        var kanaForms = LoadKanaForms(context, rows.Select(r => r.BaseWordId).Concat(rows.Select(r => r.DerivedWordId)));

        var edges = new Edge[rows.Count];
        var incident = new Dictionary<long, List<int>>();
        var pairs = new Dictionary<DerivationCategory, HashSet<(int, int)>>();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var baseKey = Key(row.BaseWordId, row.BaseReadingIndex);
            var derivedKey = Key(row.DerivedWordId, row.DerivedReadingIndex);
            var crossesIntoKana = !kanaForms.Contains(baseKey) && kanaForms.Contains(derivedKey);
            edges[i] = new Edge(baseKey, derivedKey, row.Category,
                                row.Direction == DerivationDirection.Bidirectional && !crossesIntoKana);

            Attach(incident, baseKey, i);
            Attach(incident, derivedKey, i);

            if (!pairs.TryGetValue(row.Category, out var categoryPairs))
                pairs[row.Category] = categoryPairs = [];
            categoryPairs.Add((row.BaseWordId, row.DerivedWordId));
        }

        _graph = new Graph
        {
            Edges = edges,
            Incident = incident.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
            PairCounts = pairs.ToDictionary(kv => kv.Key, kv => kv.Value.Count)
        };

        _logger.LogInformation("DerivationLinkCache loaded {Rows} links over {Keys} forms", rows.Count, incident.Count);
    }

    private static HashSet<long> LoadKanaForms(JitenDbContext context, IEnumerable<int> wordIds)
    {
        var ids = wordIds.Distinct().ToList();
        var forms = context.WordForms
                           .AsNoTracking()
                           .Where(f => ids.Contains(f.WordId) && f.FormType == JmDictFormType.KanaForm)
                           .Select(f => new { f.WordId, f.ReadingIndex })
                           .ToList();

        return forms.Where(f => f.ReadingIndex is >= 0 and <= byte.MaxValue)
                    .Select(f => Key(f.WordId, (byte)f.ReadingIndex))
                    .ToHashSet();
    }

    private static void Attach(Dictionary<long, List<int>> incident, long key, int edgeIndex)
    {
        if (!incident.TryGetValue(key, out var list))
            incident[key] = list = [];
        list.Add(edgeIndex);
    }

    private static long Key(int wordId, byte readingIndex) => ((long)wordId << 8) | readingIndex;

    private static int WordIdOf(long key) => (int)(key >> 8);

    private static byte ReadingIndexOf(long key) => (byte)(key & 0xFF);

    public IReadOnlyList<DerivationCover> GetCoveringKeys(int wordId, byte readingIndex,
                                                           IReadOnlySet<DerivationCategory> categories)
        => Walk(wordId, readingIndex, categories, towardsBase: true);

    public IReadOnlyList<DerivationCover> GetCoveredKeys(int wordId, byte readingIndex,
                                                          IReadOnlySet<DerivationCategory> categories)
        => Walk(wordId, readingIndex, categories, towardsBase: false);

    private IReadOnlyList<DerivationCover> Walk(int wordId, byte readingIndex,
                                                 IReadOnlySet<DerivationCategory> categories, bool towardsBase)
    {
        var graph = _graph;
        var start = Key(wordId, readingIndex);
        if (categories.Count == 0 || !graph.Incident.ContainsKey(start))
            return [];

        var visited = new Dictionary<long, DerivationCategory>();
        var queue = new Queue<long>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!graph.Incident.TryGetValue(current, out var edgeIndexes)) continue;

            foreach (var edgeIndex in edgeIndexes)
            {
                var edge = graph.Edges[edgeIndex];
                if (!categories.Contains(edge.Category)) continue;

                // A base always covers its derived form; the opposite way needs the edge to conduct in reverse.
                var onDerivedSide = edge.DerivedKey == current;
                var freeDirection = towardsBase ? onDerivedSide : !onDerivedSide;
                if (!freeDirection && !edge.ConductsInReverse) continue;

                var next = onDerivedSide ? edge.BaseKey : edge.DerivedKey;
                if (next == start || visited.ContainsKey(next)) continue;

                // The category recorded is the one on the first hop out of the start key, which is the
                // grammar point the UI names regardless of how far the other card actually sits.
                visited[next] = current == start ? edge.Category : visited[current];
                queue.Enqueue(next);
            }
        }

        return visited.Select(kv => new DerivationCover(WordIdOf(kv.Key), ReadingIndexOf(kv.Key), kv.Value)).ToList();
    }

    public IReadOnlyList<DerivationLink> GetBaseLinks(int wordId, byte readingIndex)
        => Links(wordId, readingIndex, derivedSide: true);

    public IReadOnlyList<DerivationLink> GetDerivedLinks(int wordId, byte readingIndex)
        => Links(wordId, readingIndex, derivedSide: false);

    private IReadOnlyList<DerivationLink> Links(int wordId, byte readingIndex, bool derivedSide)
    {
        var graph = _graph;
        var key = Key(wordId, readingIndex);
        if (!graph.Incident.TryGetValue(key, out var edgeIndexes))
            return [];

        var result = new List<DerivationLink>();
        foreach (var edgeIndex in edgeIndexes)
        {
            var edge = graph.Edges[edgeIndex];
            var matches = derivedSide ? edge.DerivedKey == key : edge.BaseKey == key;
            if (!matches) continue;

            var other = derivedSide ? edge.BaseKey : edge.DerivedKey;
            result.Add(new DerivationLink(WordIdOf(other), ReadingIndexOf(other), edge.Category,
                                          edge.ConductsInReverse
                                              ? DerivationDirection.Bidirectional
                                              : DerivationDirection.BaseToDerivedOnly));
        }

        return result;
    }
}
