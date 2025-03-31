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
            Regex regex = new(@"\b(part\d{4,5}\.x?html|p-\d{3}\.x?html|\d{4}\.x?html)|text\d{4,5}\.x?html|index_split_\d{3}\.x?html|a_\d_\d{2}.x?html|k\d+_\d{4}.x?html\b|kd\d+_\d{4}.x?html\b");

            var filteredChapters = book.ReadingOrder
                                       .Where(chapter => regex.IsMatch(chapter.Key));


            if (!filteredChapters.Any())
                Debugger.Break();
            
            foreach (var chapter in filteredChapters)
            {
                var parser = new HtmlParser();
                var document = parser.ParseDocument(PreprocessHtml(chapter.Content));

                var nodes = document.Body
                                    .Descendants()
                                    .Where(n => n.NodeType == AngleSharp.Dom.NodeType.Text &&
                                                !n.ParentElement?.TagName.Equals("TITLE", System.StringComparison.OrdinalIgnoreCase) ==
                                                true &&
                                                !n.ParentElement?.TagName.Equals("STYLE", System.StringComparison.OrdinalIgnoreCase) ==
                                                true &&
                                                !n.ParentElement?.TagName.Equals("SCRIPT", System.StringComparison.OrdinalIgnoreCase) ==
                                                true)
                                    .Cast<IText>();


                if (nodes != null)
                {
                    StringBuilder lineBuilder = new StringBuilder();

                    foreach (var node in nodes)
                    {
                        var parentElement = node.ParentElement;

                        if (parentElement != null && parentElement.TagName.Equals("RUBY", StringComparison.OrdinalIgnoreCase))
                        {
                            // Extract only kanji from ruby text
                            var rbNodes = parentElement.QuerySelectorAll("rb");
                            foreach (var rbNode in rbNodes)
                            {
                                lineBuilder.Append(rbNode.TextContent.Trim());
                            }
                        }
                        else if (parentElement != null &&
                                 !parentElement.TagName.Equals("RB", StringComparison.OrdinalIgnoreCase) &&
                                 !parentElement.TagName.Equals("RT", StringComparison.OrdinalIgnoreCase))
                        {
                            string text = node.TextContent.Trim();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                lineBuilder.Append(text);
                            }
                        }


                        // Check if we've reached the end of a sentence or paragraph
                        if (lineBuilder.Length <= 0 ||
                            (!node.Data.EndsWith("。") && !node.Data.EndsWith("！") && !node.Data.EndsWith("？"))) continue;

                        extractedText.AppendLine(lineBuilder.ToString());
                        lineBuilder.Clear();
                    }

                    // Add any remaining text
                    if (lineBuilder.Length > 0)
                    {
                        extractedText.AppendLine(lineBuilder.ToString());
                    }
                }
                else
                {
                    Console.WriteLine("No text or ruby nodes found in the chapter.");
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