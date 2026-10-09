using System.Text;
using System.Text.RegularExpressions;

namespace Jiten.Core.Services;

public sealed record SuggestionDeck(int DeckId, string OriginalTitle, string? EnglishTitle, int? FranchiseId);

/// <param name="Root">Title root shared by every deck in the group, as written in the first matching title.</param>
/// <param name="Key">Normalised root, the identity dismissals are stored under.</param>
public sealed record TitleGroup(string Root, string Key, List<int> DeckIds);

/// <summary>Finds decks outside any franchise whose titles share a long enough root with other decks.</summary>
public static partial class FranchiseSuggestions
{
    /// <summary>Kanji weigh 10, kana 5, Latin letters 3: three kanji, six kana or ten Latin letters pass.</summary>
    public const int MinStrength = 30;

    /// <summary>English titles are translations, so two of them sharing a phrase is weaker evidence than two originals.</summary>
    public const int MinEnglishStrength = 40;

    /// <summary>A root that stops inside a word in both titles (魔法少|女 vs 魔法少|年, クロスファイ|ア vs クロスファイ|ト) needs this much more.</summary>
    public const int BrokenWordPenalty = 10;

    private const int MinLatinContentWords = 2;

    /// <summary>Each deck sorts up to two titles, so a deck's nearest other-deck neighbour can sit a few entries away.</summary>
    private const int NeighbourWindow = 3;

    private static readonly HashSet<string> LatinStopWords =
    [
        "the", "of", "a", "an", "and", "or", "no", "to", "in", "on", "at", "is", "are", "be", "my", "your", "his", "her", "who", "how", "why",
        "what", "when", "then", "there", "with", "from", "for", "i", "you", "we", "it", "vol", "volume", "part", "season", "episode", "chapter",
        "book", "movie", "special", "sp", "new", "ii", "iii", "iv", "vi", "vii", "viii", "ix"
    ];

    private static readonly string[] JapaneseTailParticles = ["から", "まで", "より", "の", "と", "な", "で", "に", "は", "が", "を", "も", "へ", "や", "第"];

    /// <summary>JMdict parts of speech that make a root an ordinary word (異世界, 放課後) rather than a franchise name.</summary>
    private static readonly HashSet<string> CommonWordPos =
        ["n", "exp", "adv", "adj-no", "adj-na", "adj-i", "adj-t", "adv-to", "vs", "pn", "int", "yoji", "proverb", "n-suf", "n-pref", "pref", "suf"];

    private static readonly HashSet<string> WorkPos = ["work", "product", "fict", "char"];

    [GeneratedRegex(@"^(?:劇場版\s*|映画[\s「]|ネット版\s*|gekijouban\s+|the movie:?\s+)+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingDecoration();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private enum Script { Other, Kanji, Hiragana, Katakana, Latin }

    private sealed record Entry(string Display, string Key, int DeckId, bool English);

    public sealed record Pair(string Root, string Key, int A, int B);

    /// <summary>Near neighbours in sorted order that share a strong root; every title starting with a root sits in one run, so near pairs are enough.</summary>
    public static List<Pair> FindPairs(IEnumerable<SuggestionDeck> decks)
    {
        var entries = new List<Entry>();
        foreach (var deck in decks)
        {
            var original = Normalise(deck.OriginalTitle);
            if (original != null)
                entries.Add(new Entry(original, Lower(original), deck.DeckId, false));
            var english = Normalise(deck.EnglishTitle);
            if (english != null && english != original)
                entries.Add(new Entry(english, Lower(english), deck.DeckId, true));
        }

        entries.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));

        var pairs = new List<Pair>();
        for (var i = 0; i < entries.Count; i++)
        {
            for (var j = i + 1; j < entries.Count && j <= i + NeighbourWindow; j++)
            {
                var (a, b) = (entries[i], entries[j]);
                if (a.DeckId == b.DeckId || SharedRoot(a.Key, b.Key, a.English || b.English) is not { } length)
                    continue;
                var display = a.Display[..length];
                pairs.Add(new Pair(display[display.TakeWhile(c => !char.IsLetterOrDigit(c)).Count()..], a.Key[..length], a.DeckId, b.DeckId));
            }
        }

        return pairs;
    }

    /// <summary>True when JMdict lists the root as an ordinary word and never as a title.</summary>
    public static bool IsCommonWord(IEnumerable<string> partsOfSpeech)
    {
        var pos = partsOfSpeech.ToHashSet();
        return pos.Overlaps(CommonWordPos) && !pos.Overlaps(WorkPos);
    }

    /// <summary>Groups decks joined by a strong shared root; keeps groups with a deck outside any franchise that would join another deck or franchise.</summary>
    public static List<TitleGroup> Group(IReadOnlyList<Pair> pairs, IReadOnlyDictionary<int, int?> franchiseOf)
    {
        var parent = new Dictionary<int, int>();

        int Find(int x)
        {
            while (parent.TryGetValue(x, out var p) && p != x)
            {
                var grand = parent.GetValueOrDefault(p, p);
                parent[x] = grand;
                x = grand;
            }
            return x;
        }

        foreach (var pair in pairs)
        {
            var (ra, rb) = (Find(pair.A), Find(pair.B));
            parent.TryAdd(ra, ra);
            if (ra != rb)
                parent[ra] = rb;
        }

        var groups = new List<TitleGroup>();
        foreach (var component in pairs.GroupBy(p => Find(p.A)))
        {
            var deckIds = component.SelectMany(p => new[] { p.A, p.B }).Distinct().Order().ToList();
            var isolated = deckIds.Count(id => franchiseOf.GetValueOrDefault(id) == null);
            var units = deckIds.Select(id => franchiseOf.GetValueOrDefault(id) is { } f ? -f : id).Distinct().Count();
            if (isolated == 0 || units < 2)
                continue;

            var root = component.MinBy(p => (p.Key.Length, p.Key))!;
            groups.Add(new TitleGroup(root.Root, root.Key, deckIds));
        }

        return groups;
    }

    /// <summary>NFKC, collapsed whitespace and no leading 劇場版-style marker; null when nothing is left.</summary>
    public static string? Normalise(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var text = Whitespace().Replace(title.Normalize(NormalizationForm.FormKC), " ").Trim();
        var stripped = LeadingDecoration().Replace(text, "").Trim();
        return stripped.Length > 0 ? stripped : text.Length > 0 ? text : null;
    }

    /// <summary>Char-wise so the key keeps the display string's length and indexes.</summary>
    private static string Lower(string text) => string.Create(text.Length, text, (span, s) =>
    {
        for (var i = 0; i < s.Length; i++)
            span[i] = char.ToLowerInvariant(s[i]);
    });

    /// <summary>Length of the trimmed root two keys share, or null when it is too weak to suggest.</summary>
    public static int? SharedRoot(string a, string b, bool english)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && a[n] == b[n])
            n++;
        if (n == 0)
            return null;

        var last = ScriptOf(a[n - 1]);
        var aContinues = n < a.Length && last != Script.Other && ScriptOf(a[n]) == last;
        var bContinues = n < b.Length && last != Script.Other && ScriptOf(b[n]) == last;
        var broken = aContinues && bContinues && last != Script.Latin;
        if ((last == Script.Latin && (aContinues || bContinues)) || (last == Script.Katakana && broken))
        {
            var start = n;
            while (start > 0 && ScriptOf(a[start - 1]) == last)
                start--;
            if (last == Script.Latin || n - start < 2)
            {
                n = start;
                broken = false;
            }
        }

        n = TrimTail(a, n);
        if (n == 0)
            return null;

        var root = a.AsSpan(0, n);
        if (!root.ContainsAnyInRange('぀', '鿿') && LatinContentWords(root) < MinLatinContentWords)
            return null;

        var needed = (english ? MinEnglishStrength : MinStrength) + (broken ? BrokenWordPenalty : 0);
        return Strength(root) >= needed ? n : null;
    }

    public static int Strength(ReadOnlySpan<char> root)
    {
        var total = 0;
        foreach (var c in root)
            total += ScriptOf(c) switch
            {
                Script.Kanji => 10,
                Script.Hiragana or Script.Katakana => 5,
                Script.Latin => 3,
                _ => 0
            };
        return total;
    }

    private static int TrimTail(string key, int n)
    {
        while (true)
        {
            var before = n;
            while (n > 0 && !char.IsLetter(key[n - 1]))
                n--;

            var wordStart = n;
            while (wordStart > 0 && key[wordStart - 1] is >= 'a' and <= 'z')
                wordStart--;
            if (wordStart > 0 && wordStart < n && key[wordStart - 1] == ' ' && (n - wordStart == 1 || LatinStopWords.Contains(key[wordStart..n])))
                n = wordStart;

            foreach (var particle in JapaneseTailParticles)
            {
                if (n > particle.Length + 1 && string.CompareOrdinal(key, n - particle.Length, particle, 0, particle.Length) == 0)
                {
                    n -= particle.Length;
                    break;
                }
            }

            if (n == before)
                return n;
        }
    }

    private static int LatinContentWords(ReadOnlySpan<char> root)
    {
        var count = 0;
        var i = 0;
        while (i < root.Length)
        {
            var start = i;
            while (i < root.Length && char.IsLetter(root[i]))
                i++;
            if (i - start >= 2 && !LatinStopWords.Contains(root[start..i].ToString()))
                count++;
            i = Math.Max(i, start + 1);
        }
        return count;
    }

    private static Script ScriptOf(char c) => c switch
    {
        >= '一' and <= '鿿' or >= '㐀' and <= '䶿' or '々' or '〆' or 'ヶ' => Script.Kanji,
        >= 'ぁ' and <= 'ゟ' => Script.Hiragana,
        >= 'ァ' and <= 'ヺ' or >= 'ー' and <= 'ヾ' => Script.Katakana,
        _ when char.IsLetter(c) => Script.Latin,
        _ => Script.Other
    };
}
