using FluentAssertions;
using Jiten.Parser;
using Xunit;

namespace Jiten.Tests;

public class SentenceSplitTests
{
    private static async Task<List<string>> Split(string text) =>
        (await new MorphologicalAnalyser().Parse(text)).Select(s => s.Text).ToList();

    [Fact]
    public async Task LineFinalParenthesisEndsTheSentence()
    {
        var sentences = await Split("（それなら、行かないで欲しい）\n（でも何度言ったってきっと同じ）\n「……\n」\n");

        sentences.Should().Equal("（それなら、行かないで欲しい）", "（でも何度言ったってきっと同じ）", "「…」");
    }

    [Fact]
    public async Task MidLineParenthesisStaysInItsSentence()
    {
        var sentences = await Split("（助かるかしら）すぐそう思われるほどな重傷なのである。\n総帥（グランドマスター）への紹介状も書いて貰えるかもしれない。\n");

        sentences.Should().Equal("（助かるかしら）すぐそう思われるほどな重傷なのである。", "総帥（グランドマスター）への紹介状も書いて貰えるかもしれない。");
    }

    [Theory]
    [InlineData("「‥‥」\n", "「…」")]
    [InlineData("そうだったのか……\n", "そうだったのか。")]
    public async Task EllipsisNormalisation(string text, string expected)
    {
        (await Split(text)).Should().Equal(expected);
    }

    [Theory]
    [InlineData("「え、私みたいに。？」", "「え、私みたいに？」")]
    [InlineData("同じことを何度も何度も。。", "同じことを何度も何度も。")]
    [InlineData("そう。ですか？", "そう。ですか？")]
    public void ExampleTextDropsPeriodFromLineFinalEllipsis(string text, string expected)
    {
        ExampleSentenceExtractor.NormalizeTrailingPunctuation(text).Should().Be(expected);
    }
}
