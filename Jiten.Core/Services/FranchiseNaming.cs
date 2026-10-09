using System.Globalization;
using Jiten.Core.Data;

namespace Jiten.Core.Services;

public static class FranchiseNaming
{
    public const int MaxNameLength = GroupTitles.MaxLength;
    public const int MinPrefixLength = 3;
    public const double PrefixShare = 0.6;

    /// <summary>Release dates before this are placeholders for unknown.</summary>
    public static readonly DateOnly UnknownReleaseCutoff = new(1900, 1, 1);
    private static readonly char[] TrimmedTail = "0123456789０１２３４５６７８９IVXⅠⅡⅢⅣⅤⅥⅦⅧⅨⅩⅪⅫ：:～〜-‐－・ ~/／(（[［【「『　\t".ToCharArray();

    /// <summary>Earliest release first; unknown dates sort last.</summary>
    public static (bool Unknown, DateOnly Date) ReleaseOrder(DateOnly releaseDate) => (releaseDate < UnknownReleaseCutoff, releaseDate);

    /// <summary>
    /// The series titles when the component touches exactly one series; else the longest original title prefix (trailing numbering and separators trimmed,
    /// 3+ characters) shared by 60 % of the members and at least two; else the earliest member's titles.
    /// A prefix that is a member's whole original title takes that member's other titles.
    /// </summary>
    public static GroupTitles Suggest(IReadOnlyList<(GroupTitles Titles, DateOnly ReleaseDate, int DeckId)> members,
                                      IReadOnlyList<GroupTitles> series)
    {
        if (series.Count == 1 && !string.IsNullOrWhiteSpace(series[0].OriginalTitle))
            return GroupTitles.Of(series[0].OriginalTitle, series[0].RomajiTitle, series[0].EnglishTitle);

        var ordered = members.OrderBy(m => ReleaseOrder(m.ReleaseDate)).ThenBy(m => m.DeckId).Select(m => m.Titles).ToList();
        var prefix = SharedPrefix(ordered.Select(t => (string?)t.OriginalTitle).ToList());
        if (prefix == null)
            return ordered.Count == 0 ? GroupTitles.Of("", null, null) : Copy(ordered[0]);

        if (ordered.FirstOrDefault(t => t.OriginalTitle?.Trim() == prefix) is { } named)
            return Copy(named);

        var sharing = ordered.Where(t => t.OriginalTitle?.Trim().StartsWith(prefix, StringComparison.Ordinal) == true).ToList();
        return GroupTitles.Of(prefix, WholeWordPrefix(sharing.Select(t => t.RomajiTitle)), WholeWordPrefix(sharing.Select(t => t.EnglishTitle)));
    }

    private static GroupTitles Copy(GroupTitles titles) => GroupTitles.Of(titles.OriginalTitle, titles.RomajiTitle, titles.EnglishTitle);

    /// <summary>Prefix of every given title, cut at a word boundary; stricter than <see cref="SharedPrefix"/> as translations often share only "The" or "A Kiss".</summary>
    private static string? WholeWordPrefix(IEnumerable<string?> titles)
    {
        var present = titles.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim()).ToList();
        if (present.Count < 2)
            return null;

        var prefix = TrimTail(present.Aggregate(CommonPrefix));
        if (prefix.Length < MinPrefixLength || prefix.Equals("The", StringComparison.OrdinalIgnoreCase))
            return null;

        return present.All(t => t.Length == prefix.Length || !char.IsLetterOrDigit(t[prefix.Length])) ? prefix : null;
    }

    /// <summary>In ordinal order, every k titles sharing a prefix sit in one window of k, so the window ends bound it.</summary>
    private static string? SharedPrefix(List<string?> titles)
    {
        var sorted = titles.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim()).Order(StringComparer.Ordinal).ToList();
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
