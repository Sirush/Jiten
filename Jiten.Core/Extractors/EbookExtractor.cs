using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using VersOne.Epub;
using VersOne.Epub.Options;

namespace Jiten.Core;

public class EbookExtractor
{
    private List<string> _bannedChapterNames =
    [
        "cover", "toc", "fmatter", "credit", "illust", "colophon", "cover", "toc", "fmatter"
    ];

    private static readonly string[] _boilerplateKeywords =
    [
        "ISBN", "©", "発行", "印刷", "出版", "CONTENTS", "目次", "初版", "奥付",
        "無断で複製", "第三者に譲渡", "転載", "配信", "角川書店", "KADOKAWA",
        "ＫＡＤＯＫＡＷＡ", "電子書籍", "プリント版", "デザイン事務所", "特別収録",
        "初出", "BOOK☆WALKER", "本作品", "ＣＯＮＴＥＮＴＳ"
    ];

    private const int BoilerplateMaxLength = 500;

    public async Task<string> ExtractTextFromEbook(string? filePath)
    {
        try
        {
            var readerOptions = new EpubReaderOptions
            {
                SpineReaderOptions = new SpineReaderOptions { IgnoreMissingManifestItems = true }
            };
            EpubBook book = await EpubReader.ReadBookAsync(filePath!, readerOptions);
            return await ExtractFromReadingOrder(book.ReadingOrder.Select(c => (c.Key, c.Content)));
        }
        catch (Exception e)
        {
            Console.WriteLine($"VersOne.Epub failed, trying manual extraction: {e.Message}");
            try
            {
                return await ExtractManually(filePath!);
            }
            catch (Exception e2)
            {
                Console.WriteLine(e2);
                return "";
            }
        }
    }

    private async Task<string> ExtractManually(string filePath)
    {
        using var zip = ZipFile.OpenRead(filePath);

        var containerEntry = zip.GetEntry("META-INF/container.xml")!;
        using var containerStream = containerEntry.Open();
        var containerDoc = await XDocument.LoadAsync(containerStream, LoadOptions.None, CancellationToken.None);
        XNamespace cns = "urn:oasis:names:tc:opendocument:xmlns:container";
        var opfPath = containerDoc.Descendants(cns + "rootfile").First().Attribute("full-path")!.Value;

        var contentDir = opfPath.Contains('/') ? opfPath[..(opfPath.LastIndexOf('/') + 1)] : "";

        var opfEntry = zip.GetEntry(opfPath)!;
        using var opfStream = opfEntry.Open();
        var opfDoc = await XDocument.LoadAsync(opfStream, LoadOptions.None, CancellationToken.None);
        XNamespace opfNs = "http://www.idpf.org/2007/opf";

        var manifest = opfDoc.Descendants(opfNs + "item")
            .ToDictionary(
                item => item.Attribute("id")!.Value,
                item => (Href: item.Attribute("href")!.Value, MediaType: item.Attribute("media-type")!.Value,
                         Fallback: item.Attribute("fallback")?.Value));

        var spineItems = opfDoc.Descendants(opfNs + "itemref")
            .Select(item => item.Attribute("idref")!.Value)
            .ToList();

        var chapters = new List<(string Key, string Content)>();
        foreach (var idref in spineItems)
        {
            if (!manifest.TryGetValue(idref, out var item))
                continue;

            var href = item.Href;
            var mediaType = item.MediaType;

            if (mediaType.Contains("svg") || !mediaType.Contains("html"))
            {
                if (item.Fallback != null && manifest.TryGetValue(item.Fallback, out var fallbackItem) &&
                    fallbackItem.MediaType.Contains("html"))
                {
                    href = fallbackItem.Href;
                }
                else
                    continue;
            }

            var entryPath = contentDir + href;
            var entry = zip.GetEntry(entryPath);
            if (entry == null) continue;

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var content = await reader.ReadToEndAsync();
            chapters.Add((href, content));
        }

        return await ExtractFromReadingOrder(chapters);
    }

    private async Task<string> ExtractFromReadingOrder(IEnumerable<(string Key, string Content)> chapters)
    {
        StringBuilder extractedText = new();
        Regex regex = new(@"\b.*?\d{1,6}.*?\.x?html\b");

        var filteredChapters = chapters
            .Where(chapter => regex.IsMatch(chapter.Key) &&
                              !_bannedChapterNames.Any(bannedName => chapter.Key.Contains(bannedName, StringComparison.OrdinalIgnoreCase)));

        foreach (var chapter in filteredChapters)
        {
            var chapterText = await ExtractTextFromChapter(chapter.Content);
            if (IsBoilerplate(chapterText))
                continue;

            extractedText.Append(chapterText);
        }

        return extractedText.ToString();
    }

    private static bool IsBoilerplate(string text)
    {
        if (text.Length > BoilerplateMaxLength)
            return false;

        return _boilerplateKeywords.Any(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> ExtractTextFromChapter(string htmlContent)
    {
        var parser = new HtmlParser();
        var document = await parser.ParseDocumentAsync(PreprocessHtml(htmlContent));

        var body = document.Body;
        if (body == null)
            return "";

        RubyHtmlHelper.InlineRubyAnnotations(body, document);

        var textNodes = body.Descendants<IText>()
                            .Where(n => n.ParentElement != null &&
                                        !n.ParentElement.TagName.Equals("TITLE", StringComparison.OrdinalIgnoreCase) &&
                                        !n.ParentElement.TagName.Equals("STYLE", StringComparison.OrdinalIgnoreCase) &&
                                        !n.ParentElement.TagName.Equals("SCRIPT", StringComparison.OrdinalIgnoreCase));

        StringBuilder extractedText = new();
        StringBuilder lineBuilder = new();

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

        if (lineBuilder.Length > 0)
        {
            extractedText.AppendLine(lineBuilder.ToString());
        }

        return extractedText.ToString();
    }


    private string PreprocessHtml(string htmlContent)
    {
        htmlContent = Regex.Replace(htmlContent, @"<script[^>]*>[\s\S]*?<\/script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        htmlContent = Regex.Replace(htmlContent, @"<script[^>]*/>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        htmlContent = Regex.Replace(htmlContent, @"<style[^>]*>[\s\S]*?<\/style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        htmlContent = Regex.Replace(htmlContent, @"<style[^>]*/>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        return htmlContent;
    }
}
