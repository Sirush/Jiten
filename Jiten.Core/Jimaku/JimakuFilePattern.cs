using System.Text.RegularExpressions;

namespace Jiten.Core.Jimaku;

public static class JimakuFilePattern
{
    public static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa"];
    public static readonly string[] ArchiveExtensions = [".zip", ".rar", ".7z"];

    /// <summary>A pattern of wildcards alone would ingest every file of the entry</summary>
    public static bool IsValid(string? pattern) =>
        !string.IsNullOrWhiteSpace(pattern) && pattern.Trim().Replace("*", "").Trim().Length >= 2;

    public static Regex Compile(string pattern)
    {
        var literals = Regex.Split(pattern.Trim(), @"\*+").Select(Regex.Escape);
        return new Regex($"^{string.Join(".*", literals)}$",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
                         TimeSpan.FromSeconds(1));
    }

    public static bool IsSubtitle(string fileName) => HasExtension(fileName, SubtitleExtensions);

    public static bool IsArchive(string fileName) => HasExtension(fileName, ArchiveExtensions);

    private static bool HasExtension(string fileName, string[] extensions) =>
        extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);
}
