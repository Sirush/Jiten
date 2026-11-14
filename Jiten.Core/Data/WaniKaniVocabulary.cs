using System.Reflection;

namespace Jiten.Core.Data;

public static class WaniKaniVocabulary
{
    public const int MaxLevel = 60;

    private const string ResourceName = "Jiten.Core.Data.wanikani_vocabulary.csv";

    private sealed record Vocabulary(
        (int WordId, byte ReadingIndex)[][] FormsByLevel,
        Dictionary<string, (int WordId, byte ReadingIndex)> FormsByCharacters,
        int[] CumulativeWordCounts);

    private static readonly Lazy<Vocabulary> Loaded = new(Load);

    public static IEnumerable<(int WordId, byte ReadingIndex)> FormsUpTo(int level) =>
        Loaded.Value.FormsByLevel.Take(Math.Clamp(level, 0, MaxLevel)).SelectMany(forms => forms);

    /// <summary>Distinct words in levels 1 to N, at index N - 1.</summary>
    public static IReadOnlyList<int> CumulativeWordCounts => Loaded.Value.CumulativeWordCounts;

    /// <summary><paramref name="characters"/> is the spelling WaniKani shows, including any 〜 affix marker.</summary>
    public static bool TryResolve(string characters, out (int WordId, byte ReadingIndex) form) =>
        Loaded.Value.FormsByCharacters.TryGetValue(characters, out form);

    private static Vocabulary Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"Missing embedded resource {ResourceName}");
        using var reader = new StreamReader(stream);

        var levels = Enumerable.Range(0, MaxLevel).Select(_ => new List<(int, byte)>()).ToArray();
        var byCharacters = new Dictionary<string, (int, byte)>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || !char.IsAsciiDigit(line[0])) continue;
            var parts = line.Split(',', 4);
            var form = (int.Parse(parts[1]), byte.Parse(parts[2]));
            levels[int.Parse(parts[0]) - 1].Add(form);
            byCharacters[parts[3]] = form;
        }

        var seenWords = new HashSet<int>();
        var cumulative = levels.Select(forms =>
        {
            seenWords.UnionWith(forms.Select(f => f.Item1));
            return seenWords.Count;
        }).ToArray();

        return new Vocabulary(levels.Select(l => l.Distinct().ToArray()).ToArray(), byCharacters, cumulative);
    }
}
