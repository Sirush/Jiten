using AngleSharp.Html.Parser;
using FluentAssertions;
using Jiten.Core;
using Xunit;

namespace Jiten.Tests;

public class RubyHtmlHelperTests
{
    private static string Inline(string html)
    {
        var document = new HtmlParser().ParseDocument($"<body>{html}</body>");
        RubyHtmlHelper.InlineRubyAnnotations(document.Body!, document);
        return document.Body!.TextContent;
    }

    [Fact]
    public void SyosetuRuby_BecomesInlineFuriganaAnnotation()
    {
        // Syosetu wraps the base in a bare text node — there is no <rb>
        var html = "<p>俺の<ruby>愛機<rp>（</rp><rt>パソコン</rt><rp>）</rp></ruby>にバットを</p>";

        Inline(html).Should().Be("俺の{愛機'パソコン}にバットを");
    }

    [Fact]
    public void EpubRuby_WithRbElement_BecomesInlineFuriganaAnnotation()
    {
        var html = "<p><ruby><rb>漢字</rb><rp>(</rp><rt>かんじ</rt><rp>)</rp></ruby></p>";

        Inline(html).Should().Be("{漢字'かんじ}");
    }

    [Fact]
    public void RubyWithoutReading_KeepsOnlyTheBase()
    {
        Inline("<p><ruby>漢字<rt></rt></ruby></p>").Should().Be("漢字");
    }

    [Fact]
    public void MultipleRuby_AreAllConverted()
    {
        var html = "<p><ruby>古代長耳族<rp>(</rp><rt>ハイエルフ</rt><rp>)</rp></ruby>と" +
                   "<ruby>長耳族<rp>(</rp><rt>エルフ</rt><rp>)</rp></ruby></p>";

        Inline(html).Should().Be("{古代長耳族'ハイエルフ}と{長耳族'エルフ}");
    }
}
