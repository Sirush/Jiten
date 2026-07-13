using AngleSharp.Dom;

namespace Jiten.Core;

public static class RubyHtmlHelper
{
    /// <summary>
    /// Replaces every &lt;ruby&gt; element under <paramref name="root"/> with the parser's inline furigana
    /// format ({base'reading}), so readings survive into DeckRawText and feed FuriganaHintExtractor.
    /// Handles both &lt;rb&gt;-wrapped bases (epub) and bare text-node bases (Syosetu).
    /// </summary>
    public static void InlineRubyAnnotations(IElement root, IDocument document)
    {
        foreach (var rubyElement in root.QuerySelectorAll("ruby").ToList())
        {
            var rbElements = rubyElement.QuerySelectorAll("rb");
            var baseText = rbElements.Any()
                ? string.Concat(rbElements.Select(rb => rb.TextContent))
                : string.Concat(rubyElement.ChildNodes
                                           .Where(cn => cn.NodeType == NodeType.Text ||
                                                        (cn is IElement el &&
                                                         !el.TagName.Equals("RT", StringComparison.OrdinalIgnoreCase) &&
                                                         !el.TagName.Equals("RP", StringComparison.OrdinalIgnoreCase)))
                                           .Select(cn => cn.TextContent));

            var rtText = string.Concat(rubyElement.QuerySelectorAll("rt").Select(rt => rt.TextContent)).Trim();
            var trimmedBase = baseText.Trim();

            var replacement = !string.IsNullOrEmpty(rtText) && trimmedBase.Length > 0
                ? $"{{{trimmedBase}'{rtText}}}"
                : trimmedBase;

            rubyElement.Parent?.ReplaceChild(document.CreateTextNode(replacement), rubyElement);
        }
    }
}
