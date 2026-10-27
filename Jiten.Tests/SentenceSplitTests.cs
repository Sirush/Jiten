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
    [InlineData("マジ!?\nそうだよ\n", new[] { "マジ！？", "そうだよ" })]
    [InlineData("え!本当?\n", new[] { "え！", "本当？" })]
    [InlineData("すごい︕\nうん\n", new[] { "すごい！", "うん" })]
    [InlineData("ほんと‼\nうん\n", new[] { "ほんと！！", "うん" })]
    public async Task AsciiMarksEndTheSentence(string text, string[] expected)
    {
        (await Split(text)).Should().Equal(expected);
    }

    [Theory]
    [InlineData("もう寝ようかなあ・・・\nおやすみ\n", new[] { "もう寝ようかなあ。", "おやすみ" })]
    [InlineData("もう寝ようかなあ･･･\nおやすみ\n", new[] { "もう寝ようかなあ。", "おやすみ" })]
    [InlineData("もう寝ようかなあ...\nおやすみ\n", new[] { "もう寝ようかなあ。", "おやすみ" })]
    [InlineData("もう寝ようかなあ⋯⋯\nおやすみ\n", new[] { "もう寝ようかなあ。", "おやすみ" })]
    [InlineData("サント・アンヌ号に乗った\n", new[] { "サント・アンヌ号に乗った" })]
    public async Task DotRunsAreEllipses(string text, string[] expected)
    {
        (await Split(text)).Should().Equal(expected);
    }

    [Theory]
    [InlineData("『もしもし』\n『聞こえる？』\n", new[] { "『もしもし』", "『聞こえる？』" })]
    [InlineData("あいつは『本』\nだと言った\n", new[] { "あいつは『本』", "だと言った" })]
    [InlineData("「あの『本』\nだよ」\n", new[] { "「あの『本』だよ」" })]
    [InlineData("「『本』\n」\n", new[] { "「『本』」" })]
    public async Task LineFinalDoubleQuoteEndsTheSentence(string text, string[] expected)
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
