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
    /// <summary>Matched as substrings of the file name; short words like "ad" or "nav" are left out because base-36 file names such as c7AD.xhtml contain them.</summary>
    private static readonly string[] _bannedChapterNames =
    [
        "cover", "toc", "fmatter", "frontmatter", "credit", "illust", "colophon", "titlepage", "tobira", "hyoushi", "sankou",
        "caution", "proviso", "disclaimer", "bookwalker", "profile", "author", "auther", "creator",
        "character", "okuzuke", "okuduke", "shoshutsu", "shoshutu", "mokuji", "navigation",
        "copyright", "feedback", "funpage"
    ];

    private static readonly string[] _boilerplateKeywords =
    [
        "ISBN", "©", "発行", "印刷", "出版", "CONTENTS", "目次", "初版", "奥付",
        "無断で複製", "第三者に譲渡", "転載", "配信", "角川書店", "KADOKAWA",
        "ＫＡＤＯＫＡＷＡ", "電子書籍", "プリント版", "デザイン事務所", "特別収録",
        "初出", "BOOK☆WALKER", "本作品", "ＣＯＮＴＥＮＴＳ", "著者", "プロフィール",
        "刊行", "ファンレター", "ご意見", "ご感想", "あて先", "編集部", "Illustration", "イラスト"
    ];

    private static readonly string[] _tableOfContentsHeadings = ["目次", "目　次", "もくじ", "CONTENTS", "ＣＯＮＴＥＮＴＳ"];

    private const int BoilerplateMaxLength = 500;

    /// <summary>Longest page that can be a heading-only ToC; whole books shipped as one file also open with 目次.</summary>
    private const int HeadedTableOfContentsMaxLength = 3000;

    /// <summary>XHTML allows &lt;a/&gt;, but the HTML parser leaves it open and the anchor would swallow the rest of the page.</summary>
    private static readonly Regex SelfClosingAnchorRegex = new(@"<a\b([^>]*?)/>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private record ChapterPage(string Text, bool IsTableOfContents);

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

    internal async Task<string> ExtractFromReadingOrder(IEnumerable<(string Key, string Content)> chapters)
    {
        StringBuilder extractedText = new();

        foreach (var chapter in chapters.Where(chapter => !IsBannedChapterName(chapter.Key)))
        {
            var page = await ExtractTextFromChapter(chapter.Content);
            if (page.IsTableOfContents || IsBoilerplate(page.Text))
                continue;

            extractedText.Append(page.Text);
        }

        return extractedText.ToString();
    }

    private static bool IsBannedChapterName(string key)
    {
        var fileName = Path.GetFileName(key);
        return _bannedChapterNames.Any(bannedName => fileName.Contains(bannedName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A page whose text is mostly links, or a short page under a 目次/CONTENTS heading, is a table of contents even when its entries end in 。.</summary>
    private static bool IsTableOfContents(int textLength, int linkTextLength, int linkCount, string text)
    {
        if (textLength == 0)
            return false;

        if (linkTextLength * 2 >= textLength)
            return true;

        if (linkCount >= 5 && linkTextLength * 5 >= textLength && text.Count(c => c == '。') <= 2)
            return true;

        var start = text.TrimStart().TrimStart('{');
        return textLength < HeadedTableOfContentsMaxLength &&
               _tableOfContentsHeadings.Any(heading => start.StartsWith(heading, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBoilerplate(string text)
    {
        if (text.Length > BoilerplateMaxLength)
            return false;

        return _boilerplateKeywords.Any(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ChapterPage> ExtractTextFromChapter(string htmlContent)
    {
        var parser = new HtmlParser();
        var document = await parser.ParseDocumentAsync(PreprocessHtml(htmlContent));

        var body = document.Body;
        if (body == null)
            return new ChapterPage("", false);

        RubyHtmlHelper.InlineRubyAnnotations(body, document);

        var textNodes = body.Descendants<IText>()
                            .Where(n => n.ParentElement != null &&
                                        !n.ParentElement.TagName.Equals("TITLE", StringComparison.OrdinalIgnoreCase) &&
                                        !n.ParentElement.TagName.Equals("STYLE", StringComparison.OrdinalIgnoreCase) &&
                                        !n.ParentElement.TagName.Equals("SCRIPT", StringComparison.OrdinalIgnoreCase));

        StringBuilder extractedText = new();
        StringBuilder lineBuilder = new();
        int textLength = 0;
        int linkTextLength = 0;

        foreach (var node in textNodes)
        {
            string text = node.TextContent;
            if (!string.IsNullOrWhiteSpace(text))
            {
                int visibleLength = text.Count(c => !char.IsWhiteSpace(c));
                textLength += visibleLength;
                if (node.ParentElement!.Closest("a[href]") != null)
                    linkTextLength += visibleLength;

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

        var chapterText = extractedText.ToString();
        int linkCount = body.QuerySelectorAll("a[href]").Length;
        return new ChapterPage(chapterText, IsTableOfContents(textLength, linkTextLength, linkCount, chapterText));
    }


    private string PreprocessHtml(string htmlContent)
    {
        htmlContent = Regex.Replace(htmlContent, @"<script[^>]*>[\s\S]*?<\/script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        htmlContent = Regex.Replace(htmlContent, @"<script[^>]*/>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        htmlContent = Regex.Replace(htmlContent, @"<style[^>]*>[\s\S]*?<\/style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        htmlContent = Regex.Replace(htmlContent, @"<style[^>]*/>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        htmlContent = SelfClosingAnchorRegex.Replace(htmlContent, "<a$1></a>");

        return htmlContent;
    }
}
