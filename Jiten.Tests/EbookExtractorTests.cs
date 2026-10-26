using FluentAssertions;
using Jiten.Core;

namespace Jiten.Tests;

public class EbookExtractorTests
{
    private const string Prose = "朝になると、彼は駅まで歩いた。雨はもう止んでいた。";

    private static string Page(string body) => $"<html><head><title>t</title></head><body>{body}</body></html>";

    private static Task<string> Extract(params (string Key, string Content)[] chapters) =>
        new EbookExtractor().ExtractFromReadingOrder(chapters);

    [Fact]
    public async Task ChapterWithoutDigitInFileName_IsExtracted()
    {
        var text = await Extract(("OEBPS/cAV.xhtml", Page($"<p>{Prose}</p>")));

        text.Should().Contain("雨はもう止んでいた。");
    }

    [Fact]
    public async Task LinkedTableOfContents_IsSkipped_EvenWithSentenceEndings()
    {
        var toc = string.Concat(Enumerable.Range(1, 6).Select(i => $"<p><a href=\"c{i}.xhtml\">第{i}話　旅に出た。</a></p>"));

        var text = await Extract(("OEBPS/cAB.xhtml", Page(toc)), ("OEBPS/cAC.xhtml", Page($"<p>{Prose}</p>")));

        text.Should().NotContain("旅に出た").And.Contain("雨はもう止んでいた。");
    }

    [Fact]
    public async Task ChapterWithSelfClosingBackLink_IsKept()
    {
        var chapter = $"<p><a id=\"m2\"/><a id=\"n1\" href=\"part0002.xhtml#t1\"/>第一章</p><p>{Prose}</p><p>{Prose}</p>";

        var text = await Extract(("Text/part0003.xhtml", Page(chapter)));

        text.Should().Contain("雨はもう止んでいた。");
    }

    [Fact]
    public async Task UnlinkedPageUnderContentsHeading_IsSkipped()
    {
        var text = await Extract(("xhtml/nav.xhtml", Page("<h1>目　次</h1><p>プロローグ</p><p>第一章　出立</p>")));

        text.Should().BeEmpty();
    }

    [Fact]
    public async Task WholeBookInOneFileUnderContentsHeading_IsKept()
    {
        var book = "<h1>目次</h1><p>第一章</p>" + string.Concat(Enumerable.Repeat($"<p>{Prose}</p>", 200));

        var text = await Extract(("xhtml/0001.xhtml", Page(book)));

        text.Should().StartWith("目次");
    }

    [Theory]
    [InlineData("xhtml/p-caution.xhtml")]
    [InlineData("xhtml/p-titlepage.xhtml")]
    [InlineData("xhtml/p-author.xhtml")]
    [InlineData("Text/p-fmatter-003.xhtml")]
    public async Task FrontAndBackMatterFileNames_AreSkipped(string key)
    {
        var text = await Extract((key, Page($"<p>{Prose}</p>")));

        text.Should().BeEmpty();
    }

    [Fact]
    public async Task ShortAuthorProfile_IsSkipped()
    {
        var text = await Extract(("xhtml/p-031.xhtml", Page("<p>著者　山田太郎</p><p>１９８０年東京生まれ。猫が好き。</p>")));

        text.Should().BeEmpty();
    }
}
