using FluentAssertions;
using Jiten.Parser.SpeechBoundaries;
using Xunit;

namespace Jiten.Tests;

public class YouTubeSpeechTextTests
{
    private static string Punctuated(int politeLines) =>
        string.Join('\n', Enumerable.Range(0, politeLines).Select(i => $"今日は{i}回目の練習をしました。"));

    [Fact]
    public void TrustsVideoThatPunctuatesItsPoliteEndings()
    {
        var (_, boundaries) = YouTubeSpeechText.Prepare(Punctuated(12));

        boundaries.Should().NotBeNull();
    }

    [Fact]
    public void DoesNotTrustVideoWithTooFewPoliteLines()
    {
        var (_, boundaries) = YouTubeSpeechText.Prepare(Punctuated(5));

        boundaries.Should().BeNull();
    }

    [Fact]
    public void DoesNotTrustVideoThatSkipsPunctuationOnPoliteEndings()
    {
        var lines = Enumerable.Range(0, 12).Select(i => i % 3 == 0 ? $"練習{i}をしました。" : $"練習{i}をしました");

        var (text, boundaries) = YouTubeSpeechText.Prepare(string.Join('\n', lines));

        boundaries.Should().BeNull();
        text.Should().Contain("練習1をしました\n");
    }

    [Fact]
    public void JoinsWrappedLinesUntilPunctuationEndsTheSentence()
    {
        var raw = Punctuated(10) + "\n鯉のぼりは、鯉という魚が\nモチーフになっています。\nはい。やっ\nてくよ。";

        var (text, _) = YouTubeSpeechText.Prepare(raw);

        text.Should().Contain("鯉のぼりは、鯉という魚がモチーフになっています。\n");
        text.Should().EndWith("はい。やってくよ。\n");
    }

    [Fact]
    public void ModelSegmentsUnpunctuatedVideo()
    {
        var raw = "まずはおにぎりです\nおにぎりはご飯を三角や丸の形に握って\nのりを巻いた食べ物です\n中には色々な具が入っています\n" +
                  "次はお味噌汁です\n温かくて体がホッとします";

        YouTubeSpeechText.Prepare(raw).Boundaries.Should().BeNull();

        var (text, boundaries) = YouTubeSpeechText.Prepare(raw, SpeechBoundaryModel.YouTube);

        boundaries.Should().NotBeNull();
        text.Should().Be("まずはおにぎりです。\nおにぎりはご飯を三角や丸の形に握ってのりを巻いた食べ物です。\n中には色々な具が入っています。\n" +
                         "次はお味噌汁です。\n温かくて体がホッとします。\n");
    }

    [Theory]
    [InlineData("紅白歌合戦です。」", true)]
    [InlineData("本当？ｗｗ", true)]
    [InlineData("「あれが欲しい」", false)]
    [InlineData("えー…", false)]
    [InlineData("最初の方はね、", false)]
    public void PunctuationDecidesTheBoundary(string line, bool ends)
    {
        var bits = YouTubeSpeechText.PunctuationBoundaries([line, "次の行です。"]);

        bits[0].Should().Be(ends);
        bits[1].Should().BeTrue();
    }

    [Fact]
    public void BackchannelLineStandsAlone()
    {
        var bits = YouTubeSpeechText.PunctuationBoundaries(["絶対に使うフレーズだし", "うん、うん。", "次に行こう", "はい", "最後です。"]);

        bits.Cast<bool>().Should().Equal(true, true, true, true, true);
    }

    [Fact]
    public void StripsRepeatedSpeakerLabels()
    {
        var raw = "堀/いやあ、ちょっとね\n水/喋り方の話？\n堀/頼むよ。\n水/はい。\n堀/えー。\n水/そうですね。";

        YouTubeSpeechText.Normalise(raw).Should().Be("いやあ、ちょっとね\n喋り方の話？\n頼むよ。\nはい。\nえー。\nそうですね。");
    }

    [Fact]
    public void KeepsPrefixSeenOnlyOnce()
    {
        var raw = "結論：大事なのは続けること\n次に行きます";

        YouTubeSpeechText.Normalise(raw).Should().Be(raw);
    }

    [Fact]
    public void IgnoresNumericAndUrlPrefixes()
    {
        var raw = "1) 最初\n1) 次\n1) 最後\nhttps://example.com\nhttps://example.com\nhttps://example.com";

        YouTubeSpeechText.Normalise(raw).Should().Be(raw);
    }

    [Fact]
    public void DropsLaughLinesWithoutChangingLineCount()
    {
        var raw = "フジ：最低っ\nｗｗｗｗｗ\nほら見て、あははww\nフジ：そうだね\nフジ：うん";

        var normalised = YouTubeSpeechText.Normalise(raw);

        normalised.Should().Be("最低っ\n\nほら見て、あはは\nそうだね\nうん");
        normalised.Split('\n').Should().HaveCount(5);
    }
}
