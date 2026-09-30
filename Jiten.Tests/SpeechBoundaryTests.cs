using System.Collections;
using FluentAssertions;
using Jiten.Parser.SpeechBoundaries;
using Xunit;

namespace Jiten.Tests;

public class SpeechBoundaryTests
{
    [Theory]
    [InlineData("ﾀﾞﾌﾞﾙﾌﾞﾚｲｶｰだ｡", "ダブルブレイカーだ。")]
    [InlineData("えっ!?", "えっ！？")]
    [InlineData("  行こう\r", "行こう")]
    [InlineData("～♪", "")]
    [InlineData("【ナレーション】 そして夜が来た", "そして夜が来た")]
    public void CleanNormalisesAndDropsNonSpoken(string raw, string expected)
    {
        SpeechLineCleaner.Clean(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("≪話になんないの！➡", "話になんないの！")]
    [InlineData("－行くぞ", "行くぞ")]
    [InlineData("《本当は好きなヤツでもいるの？》", "本当は好きなヤツでもいるの？")]
    [InlineData("戦争関連の予算が―", "戦争関連の予算が")]
    [InlineData("だから悪霊なんかは　人から生命力を奪おうとして", "だから悪霊なんかは、人から生命力を奪おうとして")]
    [InlineData("ダメだ  私は行けない", "ダメだ、私は行けない")]
    [InlineData("あっ　そうですね", "あっ、そうですね")]
    [InlineData("えっ！？　何で", "えっ！？何で")]
    [InlineData("「うん」　行こう", "「うん」行こう")]
    [InlineData("瞳をそらすな Fly again！", "瞳をそらすなFly again！")]
    [InlineData("♫ 路地のなか ♫", "路地のなか")]
    [InlineData("Ｉ ｌｏｖｅ　ｙｏｕって", "Ｉ ｌｏｖｅ ｙｏｕって")]
    public void SentenceTextDropsDisplayMarks(string cleaned, string expected)
    {
        SpeechLineCleaner.ToSentenceText(cleaned).Should().Be(expected);
    }

    [Fact]
    public void AssembleJoinsContinuedLinesAndEndsBoundaries()
    {
        string[] lines = ["闇に潜むヴァンパイアどもを", "あぶり出せ", "～♪", "よし！", "撤収"];
        var boundaries = new BitArray([false, true, false, true, false]);

        SpeechTextAssembler.Assemble(lines, boundaries)
                           .Should().Be("闇に潜むヴァンパイアどもをあぶり出せ。\nよし！\n撤収。\n");
    }

    [Fact]
    public void AssembleSkipsDroppedLinesWithoutEndingTheSentence()
    {
        string[] lines = ["東京まで", "(笑)", "まだ遠い"];
        var boundaries = new BitArray([false, false, true]);

        SpeechTextAssembler.Assemble(lines, boundaries).Should().Be("東京までまだ遠い。\n");
    }
}
