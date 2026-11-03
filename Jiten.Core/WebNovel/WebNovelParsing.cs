using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace Jiten.Core.WebNovel;

internal static partial class WebNovelParsing
{
    /// <summary>
    /// One line per &lt;p&gt; with ruby inlined as {base'reading}; runs of blank paragraphs collapse to one empty line.
    /// </summary>
    public static string ParagraphText(IElement block, IDocument document)
    {
        RubyHtmlHelper.InlineRubyAnnotations(block, document);

        var sb = new StringBuilder();
        foreach (var paragraph in block.QuerySelectorAll("p"))
            sb.Append(paragraph.TextContent.Trim()).Append('\n');

        return BlankLineRuns().Replace(sb.ToString(), "\n\n").Trim();
    }

    public static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLineRuns();
}
