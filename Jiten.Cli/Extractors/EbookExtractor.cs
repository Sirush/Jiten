using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using VersOne.Epub;

namespace Jiten.Cli;

public class EbookExtractor
{
    public async Task<string> ExtractTextFromEbook(string? filePath)
    {
        try
        {
            EpubBook book = await EpubReader.ReadBookAsync(filePath);

            StringBuilder extractedText = new();

            // Select only the chapters with key partXXXX.html or p-XXX.html or XXXX.html
            // I'm not sure if all epub files have this format, but it works for now
            // I keep having to add new ones, help me
            Regex regex =
                new(@"\b(part\d{4,5}\.x?html|p-\d{3}\.x?html|\d{4}\.x?html)|text\d{4,5}\.x?html|index_split_\d{3}\.x?html|a_\d_\d{2}.x?html|k\d+_\d{4}.x?html\b|kd\d+_\d{4}.x?html\b");

            var filteredChapters = book.ReadingOrder
                                       .Where(chapter => regex.IsMatch(chapter.Key));


            if (!filteredChapters.Any())
                Debugger.Break();

            foreach (var chapter in filteredChapters)
            {
                var parser = new HtmlParser();
                var document = await parser.ParseDocumentAsync(PreprocessHtml(chapter.Content));

                var body = document.Body;
                if (body == null)
                {
                    Console.WriteLine("No body found in the chapter.");
                    continue;
                }

                foreach (var rubyElement in body.QuerySelectorAll("ruby").ToList()) // ToList avoids modification issues during iteration
                {
                    string baseText = "";
                    // Prioritize <rb> if it exists
                    var rbElements = rubyElement.QuerySelectorAll("rb");
                    if (rbElements.Any())
                    {
                        baseText = string.Concat(rbElements.Select(rb => rb.TextContent));
                    }
                    else
                    {
                        // Fallback: Get direct text nodes, excluding <rt> and <rp> content
                        baseText = string.Concat(
                                                 rubyElement.ChildNodes
                                                            .Where(cn => cn.NodeType == NodeType.Text || (cn is IElement el &&
                                                                       !el.TagName.Equals("RT", StringComparison.OrdinalIgnoreCase) &&
                                                                       !el.TagName.Equals("RP", StringComparison.OrdinalIgnoreCase)))
                                                            .Select(cn => cn.TextContent)
                                                );
                    }

                    // Replace the <ruby> element with a simple text node containing the base text
                    rubyElement.Parent?.ReplaceChild(document.CreateTextNode(baseText.Trim()), rubyElement);
                }

                var textNodes = body.Descendants<IText>()
                                    .Where(n => n.ParentElement != null &&
                                                !n.ParentElement.TagName.Equals("TITLE", StringComparison.OrdinalIgnoreCase) &&
                                                !n.ParentElement.TagName.Equals("STYLE", StringComparison.OrdinalIgnoreCase) &&
                                                !n.ParentElement.TagName.Equals("SCRIPT", StringComparison.OrdinalIgnoreCase));


                StringBuilder lineBuilder = new StringBuilder();

                foreach (var node in textNodes)
                {
                    string text = node.TextContent;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        lineBuilder.Append(text.Trim());
                        if (lineBuilder.Length > 0 &&
                            (text.EndsWith('。') || text.EndsWith('！') || text.EndsWith('？')))
                        {
                            extractedText.AppendLine(lineBuilder.ToString());
                            lineBuilder.Clear();
                        }
                    }
                }

                // Add any remaining text
                if (lineBuilder.Length > 0)
                {
                    extractedText.AppendLine(lineBuilder.ToString());
                }
            }

            return extractedText.ToString();
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return "";
        }
    }


    /// <summary>
    /// Preprocess malformed html
    /// </summary>
    /// <param name="htmlContent"></param>
    /// <returns></returns>
    private string PreprocessHtml(string htmlContent)
    {
        htmlContent = Regex.Replace(htmlContent, @"<script[^>]*>[\s\S]*?<\/script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        htmlContent = Regex.Replace(htmlContent, @"<script[^>]*/>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        htmlContent = Regex.Replace(htmlContent, @"<style[^>]*>[\s\S]*?<\/style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        htmlContent = Regex.Replace(htmlContent, @"<style[^>]*/>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        return htmlContent;
    }
}