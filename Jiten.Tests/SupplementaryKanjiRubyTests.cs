using System.Text;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core;
using Xunit;

namespace Jiten.Tests;

public class SupplementaryKanjiRubyTests
{
    private const string Biang = "\U00030EDE";

    [Theory]
    [InlineData(0x30EDE)]
    [InlineData(0x2CEB0)]
    [InlineData(0x31350)]
    [InlineData(0x2EBF0)]
    public void IsKanjiCoversLaterExtensions(int codePoint)
    {
        JapaneseTextHelper.IsKanji(new Rune(codePoint)).Should().BeTrue();
    }

    [Fact]
    public void AnkiFuriganaKeepsSupplementaryKanjiInTheRubyRun()
    {
        DeckDownloadService.ToAnkiFurigana($"{Biang}{Biang}麺[ビャンビャンめん]")
                           .Should().Be($" {Biang}{Biang}麺[ビャンビャンめん]");
    }

    [Fact]
    public void AnkiFuriganaSpacesEachBmpRun()
    {
        DeckDownloadService.ToAnkiFurigana("食[た]べ物[もの]").Should().Be(" 食[た]べ 物[もの]");
        DeckDownloadService.ToAnkiFurigana("日本[にほん]語[ご]").Should().Be(" 日本[にほん]語[ご]");
    }

    [Fact]
    public void TtsKanaReplacesSupplementaryKanjiRun()
    {
        TtsService.RubyToTtsKana($"{Biang}{Biang}麺[びゃんびゃんめん]").Should().Be("びゃんびゃんめん");
    }
}
