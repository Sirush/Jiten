namespace Jiten.Core.Data.JMDict;

/// <summary>Transform table for <see cref="DerivationBuilder"/>. Transforms run separately on the kanji and
/// kana sides, because some suffixes change reading on attachment (気味/ぎみ, 真っ/まっ).</summary>
internal static class DerivationRules
{
    internal sealed record Rule(
        DerivationCategory Category,
        string[] BasePos,
        string[] DerivedPos,
        Func<HashSet<string>, string, bool, IEnumerable<string>> Transform);

    private static readonly string[] GodanPos =
        ["v5u", "v5k", "v5g", "v5s", "v5t", "v5n", "v5b", "v5m", "v5r", "v5aru", "v5k-s", "v5u-s", "v5r-i"];

    private static readonly string[] IchidanPos = ["v1", "v1-s"];

    private static readonly string[] AnyVerbPos = [..GodanPos, ..IchidanPos];

    private static readonly string[] IAdjPos = ["adj-i", "adj-ix"];

    private static readonly string[] NounLike = ["n", "adj-na"];

    /// <summary>Ending each godan class must actually show, so a v5r tag can never license a く transform.</summary>
    private static readonly Dictionary<string, char> GodanEndings = new()
    {
        ["v5u"] = 'う', ["v5u-s"] = 'う', ["v5k"] = 'く', ["v5k-s"] = 'く', ["v5g"] = 'ぐ', ["v5s"] = 'す',
        ["v5t"] = 'つ', ["v5n"] = 'ぬ', ["v5b"] = 'ぶ', ["v5m"] = 'む', ["v5r"] = 'る', ["v5r-i"] = 'る',
        ["v5aru"] = 'る'
    };

    private static readonly Dictionary<char, char> PotentialStem = new()
    {
        ['う'] = 'え', ['つ'] = 'て', ['る'] = 'れ', ['む'] = 'め', ['ぶ'] = 'べ',
        ['ぬ'] = 'ね', ['く'] = 'け', ['ぐ'] = 'げ', ['す'] = 'せ'
    };

    private static readonly Dictionary<char, char> MasuStemRow = new()
    {
        ['う'] = 'い', ['つ'] = 'ち', ['る'] = 'り', ['む'] = 'み', ['ぶ'] = 'び',
        ['ぬ'] = 'に', ['く'] = 'き', ['ぐ'] = 'ぎ', ['す'] = 'し'
    };

    private static readonly Dictionary<char, char> MizenkeiRow = new()
    {
        ['う'] = 'わ', ['つ'] = 'た', ['る'] = 'ら', ['む'] = 'ま', ['ぶ'] = 'ば',
        ['ぬ'] = 'な', ['く'] = 'か', ['ぐ'] = 'が', ['す'] = 'さ'
    };

    private static readonly Dictionary<char, string> TeFormEnding = new()
    {
        ['う'] = "って", ['つ'] = "って", ['る'] = "って", ['む'] = "んで", ['ぶ'] = "んで",
        ['ぬ'] = "んで", ['く'] = "いて", ['ぐ'] = "いで", ['す'] = "して"
    };

    public static readonly IReadOnlyList<Rule> All =
    [
        new(DerivationCategory.SaNominal, IAdjPos, ["n"],
            (pos, text, _) => Suffix(AdjStem(pos, text), "さ")),

        new(DerivationCategory.NaSaNominal, ["adj-na"], ["n"],
            (_, text, _) => Suffix(text, "さ")),

        new(DerivationCategory.GeAdjective, IAdjPos, ["adj-na", "n"],
            (pos, text, _) => Suffix(AdjStem(pos, text), "げ")),

        new(DerivationCategory.MiNominal, IAdjPos, ["n"],
            (pos, text, _) => Suffix(AdjStem(pos, text), "み")),

        new(DerivationCategory.Garu, [..IAdjPos, "n", "adj-na"], ["v5r"],
            (pos, text, _) => Suffix(AdjStem(pos, text), "がる")
                .Concat(pos.Overlaps(NounLike) ? Suffix(text, "がる") : Array.Empty<string>())),

        new(DerivationCategory.Gari, IAdjPos, ["n"],
            (pos, text, _) => Suffix(AdjStem(pos, text), "がり")),

        new(DerivationCategory.Sou, IAdjPos, ["adj-na", "n"],
            (pos, text, _) => Suffix(AdjStem(pos, text), "そう")),

        new(DerivationCategory.KuAdverb, IAdjPos, ["adv", "adv-to"],
            (pos, text, _) => Suffix(AdjStem(pos, text), "く")),

        new(DerivationCategory.NiAdverb, ["adj-na"], ["adv", "adv-to"],
            (_, text, _) => Suffix(text, "に")),

        new(DerivationCategory.MeModerate, IAdjPos, ["n", "adj-no", "adj-na", "n-suf"],
            (pos, text, _) => Suffix(AdjStem(pos, text), "め")),

        new(DerivationCategory.Potential, GodanPos, ["v1"],
            (pos, text, _) => Potential(pos, text)),

        new(DerivationCategory.MasuStemNoun, AnyVerbPos, ["n"],
            (pos, text, _) => Suffix(MasuStem(pos, text), "")),

        new(DerivationCategory.Sugiru, [..AnyVerbPos, ..IAdjPos], ["v1", "n"],
            (pos, text, kanji) => StemForSuffix(pos, text)
                .SelectMany(stem => kanji
                    ? new[] { stem + "過ぎる", stem + "過ぎ", stem + "すぎる", stem + "すぎ" }
                    : new[] { stem + "すぎる", stem + "すぎ" })),

        new(DerivationCategory.Ppoi, [..AnyVerbPos, ..IAdjPos, "n", "adj-na"], ["adj-i"],
            (pos, text, _) => StemForSuffix(pos, text).Select(stem => stem + "っぽい")
                .Concat(pos.Overlaps(NounLike) ? new[] { text + "っぽい" } : Array.Empty<string>())),

        new(DerivationCategory.Gachi, [..AnyVerbPos, "n"], ["adj-na", "n", "n-suf", "adj-no"],
            (pos, text, kanji) => StemForSuffix(pos, text)
                .Concat(pos.Contains("n") ? new[] { text } : Array.Empty<string>())
                .SelectMany(stem => kanji ? new[] { stem + "がち", stem + "勝ち" } : new[] { stem + "がち" })),

        new(DerivationCategory.Gimi, [..AnyVerbPos, "n"], ["n-suf", "n", "adj-na", "adj-no"],
            (pos, text, kanji) => StemForSuffix(pos, text)
                .Concat(pos.Contains("n") ? new[] { text } : Array.Empty<string>())
                .Select(stem => kanji ? stem + "気味" : stem + "ぎみ")),

        new(DerivationCategory.LexicalPassive, GodanPos, ["v1"],
            (pos, text, _) => Mizenkei(pos, text).Select(stem => stem + "れる")),

        new(DerivationCategory.HonorificPrefix, ["n", "adj-na", "vs"], ["n", "adj-na", "exp"],
            (_, text, kanji) => kanji
                ? ["お" + text, "ご" + text, "御" + text]
                : ["お" + text, "ご" + text]),

        new(DerivationCategory.TeAdverb, AnyVerbPos, ["adv", "adv-to", "exp", "conj"],
            (pos, text, _) => TeForm(pos, text)),

        new(DerivationCategory.MaIntensifier, [..IAdjPos, "n"], ["n", "adj-na", "adj-no"],
            (pos, text, kanji) => AdjStem(pos, text).Concat(pos.Contains("n") ? new[] { text } : Array.Empty<string>())
                .Select(stem => kanji ? "真っ" + stem : "まっ" + stem)),

        new(DerivationCategory.CausativeDoublet, ["v1"], ["v5s"],
            (_, text, _) => text.EndsWith("せる", StringComparison.Ordinal) && text.Length > 2
                ? new[] { text[..^2] + "す" }
                : Array.Empty<string>()),

        new(DerivationCategory.ZuruJiru, ["v1"], ["vz"],
            (_, text, _) => text.EndsWith("じる", StringComparison.Ordinal) && text.Length > 2
                ? new[] { text[..^2] + "ずる" }
                : Array.Empty<string>()),

        new(DerivationCategory.ClassicalAdjective, IAdjPos, ["adj-f", "adj-pn", "adj-nari", "adj-ku", "adj-shiku"],
            (pos, text, _) => Suffix(AdjStem(pos, text), "き"))
    ];

    private static IEnumerable<string> Suffix(IEnumerable<string> stems, string suffix)
        => stems.Select(stem => stem + suffix);

    private static IEnumerable<string> Suffix(string? stem, string suffix)
        => stem == null ? [] : [stem + suffix];

    private static IEnumerable<string> AdjStem(HashSet<string> pos, string text)
    {
        if (!pos.Overlaps(IAdjPos)) yield break;
        if (text.Length < 2 || text[^1] != 'い') yield break;
        yield return text[..^1];
    }

    private static IEnumerable<string> MasuStem(HashSet<string> pos, string text)
    {
        if (text.Length < 2) yield break;

        foreach (var tag in pos)
        {
            if (GodanEndings.TryGetValue(tag, out var ending) && text[^1] == ending &&
                MasuStemRow.TryGetValue(ending, out var stemChar))
            {
                yield return text[..^1] + stemChar;
            }
            else if (IchidanPos.Contains(tag) && text[^1] == 'る')
            {
                yield return text[..^1];
            }
        }
    }

    private static IEnumerable<string> StemForSuffix(HashSet<string> pos, string text)
        => MasuStem(pos, text).Concat(AdjStem(pos, text));

    private static IEnumerable<string> Potential(HashSet<string> pos, string text)
    {
        if (text.Length < 2) yield break;

        foreach (var tag in pos)
        {
            if (!GodanEndings.TryGetValue(tag, out var ending) || text[^1] != ending) continue;
            if (!PotentialStem.TryGetValue(ending, out var stemChar)) continue;
            yield return text[..^1] + stemChar + "る";
        }
    }

    private static IEnumerable<string> Mizenkei(HashSet<string> pos, string text)
    {
        if (text.Length < 2) yield break;

        foreach (var tag in pos)
        {
            if (!GodanEndings.TryGetValue(tag, out var ending) || text[^1] != ending) continue;
            if (!MizenkeiRow.TryGetValue(ending, out var stemChar)) continue;
            yield return text[..^1] + stemChar;
        }
    }

    private static IEnumerable<string> TeForm(HashSet<string> pos, string text)
    {
        if (text.Length < 2) yield break;

        foreach (var tag in pos)
        {
            if (IchidanPos.Contains(tag) && text[^1] == 'る')
            {
                yield return text[..^1] + "て";
                continue;
            }

            if (!GodanEndings.TryGetValue(tag, out var ending) || text[^1] != ending) continue;

            // 行く/いく is the one godan verb whose て-form breaks its class map.
            if (ending == 'く' && (text.EndsWith("行く", StringComparison.Ordinal) || text == "いく"))
                yield return text[..^1] + "って";
            else if (TeFormEnding.TryGetValue(ending, out var teEnding))
                yield return text[..^1] + teEnding;
        }
    }
}
