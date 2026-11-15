using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jiten.Core.Jimaku;

public static class JimakuEpisodeNumbers
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Number = @"(\d{1,4}(?:\.\d)?)";

    private static readonly Regex[] Anchors =
    [
        new(@"(?<![a-z0-9])s\d{1,2}[ ._-]?e" + Number + @"(?=v\d|(?![a-z0-9]))", Options),
        new(@"第\s*" + Number + @"\s*[話回]?", Options),
        new(Number + @"\s*[話回]", Options),
        new(@"(?<![a-z])(?:episode|ep|e)[ ._#]*" + Number + @"(?=v\d|(?![a-z0-9]))", Options),
        new(@"#\s*" + Number + @"(?![a-z0-9])", Options),
        new(@"\s-\s" + Number + @"(?=v\d|(?![a-z0-9]))", Options)
    ];

    /// <summary>
    /// Episode number of each file name, read as a set: the marker style most of the names share wins, and names
    /// with no marker fall back to the first number after the text they all start with. A lone unmarked name has
    /// no set to compare against and yields null.
    /// </summary>
    public static double?[] Detect(IReadOnlyList<string> fileNames)
    {
        var names = fileNames.Select(n => Path.GetFileNameWithoutExtension(n).Normalize(NormalizationForm.FormKC)).ToList();
        if (names.Count == 0)
            return [];

        double?[]? best = null;
        var bestCount = 0;
        foreach (var anchor in Anchors)
        {
            var values = names.Select(n => Parse(anchor.Match(n))).ToArray();
            var count = values.Count(v => v != null);
            if (count > bestCount)
            {
                best = values;
                bestCount = count;
            }
        }

        if (best != null && bestCount == names.Count)
            return best;

        var positional = DetectAfterCommonPrefix(names);
        var positionalCount = positional.Count(v => v != null);
        return positionalCount > bestCount ? positional : best ?? positional;
    }

    private static double?[] DetectAfterCommonPrefix(List<string> names)
    {
        if (names.Count < 2)
            return new double?[names.Count];

        var prefix = names[0];
        foreach (var name in names)
        {
            var i = 0;
            while (i < prefix.Length && i < name.Length && prefix[i] == name[i])
                i++;
            prefix = prefix[..i];
        }

        // Episodes 40 to 49 share their first digit, which belongs to the number and not to the prefix
        var start = prefix.Length;
        while (start > 0 && char.IsAsciiDigit(prefix[start - 1]))
            start--;

        var leading = new Regex(@"\G\D{0,3}?" + Number, RegexOptions.CultureInvariant);
        return names.Select(n => Parse(leading.Match(n, start))).ToArray();
    }

    private static double? Parse(Match match) =>
        match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public static string Format(double episode) => episode.ToString("0.#", CultureInfo.InvariantCulture);
}
