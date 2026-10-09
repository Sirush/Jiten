namespace Jiten.Core.Data;

/// <summary>The three titles of a franchise or series, shaped like a deck's so clients localise them the same way.</summary>
public sealed record GroupTitles(string OriginalTitle, string? RomajiTitle, string? EnglishTitle)
{
    public const int MaxLength = 200;

    /// <summary>Trimmed and clamped; blank romaji and English titles become null.</summary>
    public static GroupTitles Of(string? originalTitle, string? romajiTitle, string? englishTitle) =>
        new(Clamp(originalTitle?.Trim() ?? ""), Optional(romajiTitle), Optional(englishTitle));

    private static string? Optional(string? title) => string.IsNullOrWhiteSpace(title) ? null : Clamp(title.Trim());

    private static string Clamp(string title) => title.Length > MaxLength ? title[..MaxLength] : title;
}
