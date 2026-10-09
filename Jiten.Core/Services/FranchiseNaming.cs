using System.Globalization;

namespace Jiten.Core.Services;

public static class FranchiseNaming
{
    public const int MaxNameLength = 200;
    public const int MinPrefixLength = 3;
    public const double PrefixShare = 0.6;

    /// <summary>Release dates before this are placeholders for unknown.</summary>
    public static readonly DateOnly UnknownReleaseCutoff = new(1900, 1, 1);
    private static readonly char[] TrimmedTail = "0123456789０１２３４５６７８９IVXⅠⅡⅢⅣⅤⅥⅦⅧⅨⅩⅪⅫ：:～〜-‐－・ ~/／(（[［【「『　\t".ToCharArray();

    /// <summary>Earliest release first; unknown dates sort last.</summary>
    public static (bool Unknown, DateOnly Date) ReleaseOrder(DateOnly releaseDate) => (releaseDate < UnknownReleaseCutoff, releaseDate);

    /// <summary>
    /// The series name when the component touches exactly one series; else the longest title prefix (trailing numbering and separators trimmed, 3+ characters)
    /// shared by 60 % of the members and at least two; else the earliest member's title.
    /// </summary>
    public static string Suggest(IReadOnlyList<(string OriginalTitle, DateOnly ReleaseDate, int DeckId)> members,
                                 IReadOnlyList<string> seriesNames)
    {
        if (seriesNames.Count == 1 && !string.IsNullOrWhiteSpace(seriesNames[0]))
            return Clamp(seriesNames[0].Trim());

        var prefix = SharedPrefix(members.Select(m => m.OriginalTitle).ToList());
        if (prefix != null)
            return Clamp(prefix);

        var earliest = members.OrderBy(m => ReleaseOrder(m.ReleaseDate)).ThenBy(m => m.DeckId).FirstOrDefault();
        return Clamp(earliest.OriginalTitle?.Trim() ?? "");
    }

    /// <summary>In ordinal order, every k titles sharing a prefix sit in one window of k, so the window ends bound it.</summary>
    private static string? SharedPrefix(List<string> titles)
    {
        var sorted = titles.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Order(StringComparer.Ordinal).ToList();
        var needed = Math.Max(2, (int)Math.Ceiling(titles.Count * PrefixShare - 1e-9));
        if (sorted.Count < needed)
            return null;

        string? best = null;
        for (var i = 0; i + needed - 1 < sorted.Count; i++)
        {
            var candidate = TrimTail(CommonPrefix(sorted[i], sorted[i + needed - 1]));
            if (candidate.Length < MinPrefixLength)
                continue;
            if (best == null || candidate.Length > best.Length ||
                (candidate.Length == best.Length && string.CompareOrdinal(candidate, best) < 0))
                best = candidate;
        }

        return best;
    }

    private static string CommonPrefix(string a, string b)
    {
        var length = 0;
        while (length < a.Length && length < b.Length && a[length] == b[length])
            length++;
        return a[..length];
    }

    private static string TrimTail(string prefix)
    {
        string previous;
        do
        {
            previous = prefix;
            prefix = prefix.TrimEnd().TrimEnd(TrimmedTail);
        } while (prefix.Length != previous.Length);

        return prefix;
    }

    private static string Clamp(string name) => name.Length > MaxNameLength ? name[..MaxNameLength] : name;
}

/// <summary>Orders names by their first letter or digit, so quotes and brackets around a title do not pull it to the top; full and half width, case and kana type compare equal.</summary>
public sealed class FranchiseNameComparer : IComparer<string>
{
    private static readonly CompareInfo Culture = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;

    int IComparer<string>.Compare(string? x, string? y)
    {
        var byName = Culture.Compare(SortKey(x), SortKey(y), Options);
        return byName != 0 ? byName : string.CompareOrdinal(x, y);
    }

    private static string SortKey(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "";
        var start = 0;
        while (start < name.Length && !char.IsLetterOrDigit(name[start]))
            start++;
        return start == name.Length ? name : name[start..];
    }
}
