using System.Text;

namespace Jiten.Api.Services;

/// <summary>Reduces a title to the letters and digits that identify it, so "Steins;Gate" and "STEINS GATE" compare equal but "Steins;Gate 0" does not.</summary>
public static class TitleNormalizer
{
    private const int MinLatinKeyLength = 4;
    private const int MinCjkKeyLength = 2;

    public static string Normalize(string title)
    {
        var folded = title.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(folded.Length);
        foreach (var c in folded)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Short Latin keys are mostly abbreviations ("sg", "sao") that several works share, so they cannot prove a duplicate.
    /// Two kanji or kana already make a real title.
    /// </summary>
    public static bool IsDistinctive(string key) =>
        key.Length >= (key.Any(IsCjkOrKana) ? MinCjkKeyLength : MinLatinKeyLength);

    public static HashSet<string> DistinctiveKeys(IEnumerable<string?> titles) =>
        titles.Where(t => !string.IsNullOrWhiteSpace(t))
              .Select(t => Normalize(t!))
              .Where(IsDistinctive)
              .ToHashSet(StringComparer.Ordinal);

    private static bool IsCjkOrKana(char c) =>
        c is >= '぀' and <= 'ヿ' or >= '㐀' and <= '䶿' or >= '一' and <= '鿿' or >= 'ｦ' and <= 'ﾟ';
}
