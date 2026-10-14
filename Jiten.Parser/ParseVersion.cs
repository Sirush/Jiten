namespace Jiten.Parser;

/// <summary>Version clients store with saved reader parses and compare by equality; a mismatch means re-parse.</summary>
public static class ParseVersion
{
    /// <summary>Bump for segmentation changes readers should pick up; each bump re-parses every chapter readers open.</summary>
    public const int ParserRevision = 1;

    public static string Format(int dataEpoch) => $"{ParserRevision}.{dataEpoch}";
}
