using Jiten.Parser;

namespace Jiten.Tests;

using Xunit;
using FluentAssertions;

public class FuriganaHintExtractorTests
{
    [Fact]
    public void Extract_BasicAnnotations()
    {
        var (cleanText, hints) = FuriganaHintExtractor.Extract("{漢字'かんじ}を{勉強'べんきょう}する");

        cleanText.Should().Be("漢字を勉強する");
        hints.Should().HaveCount(2);

        hints[0].Offset.Should().Be(0);
        hints[0].Length.Should().Be(2);
        hints[0].Reading.Should().Be("かんじ");

        hints[1].Offset.Should().Be(3);
        hints[1].Length.Should().Be(2);
        hints[1].Reading.Should().Be("べんきょう");
    }

    [Fact]
    public void Strip_KeepsBaseOfRealAnnotations()
    {
        FuriganaHintExtractor.Strip("{漢字'かんじ}を{お前'希真理}と読む")
                             .Should().Be("漢字をお前と読む");
    }

    [Fact]
    public void Strip_LeavesEngineScriptIntact()
    {
        const string script = "{ f.計算評価 = 'Ｓ';}";

        FuriganaHintExtractor.Strip(script).Should().Be(script);
    }

    [Fact]
    public void Strip_LeavesMultiLineBraceBlocksIntact()
    {
        var block = "{セリフ" + Environment.NewLine + "「これは'テスト」" + Environment.NewLine + "}";

        FuriganaHintExtractor.Strip(block).Should().Be(block);
    }

    [Fact]
    public void Extract_IgnoresEngineScript()
    {
        const string script = "素晴らしい{ f.計算評価 = 'A';}一日";

        var (cleanText, hints) = FuriganaHintExtractor.Extract(script);

        cleanText.Should().Be(script);
        hints.Should().BeEmpty();
    }

    [Fact]
    public void Extract_DoesNotSpanBraceBlocks()
    {
        var text = "{セリフ" + Environment.NewLine + "「これは'テスト」" + Environment.NewLine + "}";

        var (cleanText, hints) = FuriganaHintExtractor.Extract(text);

        cleanText.Should().Be(text);
        hints.Should().BeEmpty();
    }

    [Fact]
    public void Extract_NoAnnotations()
    {
        var (cleanText, hints) = FuriganaHintExtractor.Extract("普通のテキスト");

        cleanText.Should().Be("普通のテキスト");
        hints.Should().BeEmpty();
    }

    [Fact]
    public void Extract_AdjacentAnnotations()
    {
        var (cleanText, hints) = FuriganaHintExtractor.Extract("{日'にち}{本'ほん}");

        cleanText.Should().Be("日本");
        hints.Should().HaveCount(2);
        hints[0].Offset.Should().Be(0);
        hints[0].Length.Should().Be(1);
        hints[1].Offset.Should().Be(1);
        hints[1].Length.Should().Be(1);
    }

    [Fact]
    public void Extract_MalformedNotation_TreatedAsPlainText()
    {
        var (cleanText, hints) = FuriganaHintExtractor.Extract("前 {未完 と {入れ子'よみ}} 後");

        hints.Where(h => h.Reading == "よみ").Should().NotBeEmpty();
        cleanText.Should().Contain("前");
        cleanText.Should().Contain("後");
    }

    [Fact]
    public void Extract_EmptyBase_NotMatched()
    {
        var input = "before {'reading}after";
        var (cleanText, hints) = FuriganaHintExtractor.Extract(input);

        hints.Should().BeEmpty();
        cleanText.Should().Be(input);
    }

    [Fact]
    public void Extract_EmptyReading_NotMatched()
    {
        var input = "before {漢字'}after";
        var (cleanText, hints) = FuriganaHintExtractor.Extract(input);

        hints.Should().BeEmpty();
        cleanText.Should().Be(input);
    }

    [Fact]
    public void Annotate_Roundtrip()
    {
        var original = "{漢字'かんじ}を{勉強'べんきょう}する";
        var (cleanText, hints) = FuriganaHintExtractor.Extract(original);
        var result = FuriganaHintExtractor.Annotate(cleanText, hints);

        result.Should().Be(original);
    }

    [Fact]
    public void Annotate_NoHints_ReturnsOriginal()
    {
        var result = FuriganaHintExtractor.Annotate("テスト", []);
        result.Should().Be("テスト");
    }

    [Fact]
    public void RelocateToCleanedOriginal_Identity()
    {
        var cleanText = "漢字を勉強する";
        var hints = new[]
        {
            new FuriganaHint(0, 2, "かんじ"),
            new FuriganaHint(3, 2, "べんきょう")
        };

        var relocated = FuriganaHintExtractor.RelocateToCleanedOriginal(cleanText, hints, cleanText);

        relocated.Should().HaveCount(2);
        relocated[0].Offset.Should().Be(0);
        relocated[1].Offset.Should().Be(3);
    }

    [Fact]
    public void RelocateToCleanedOriginal_WithShift()
    {
        var cleanText = "あああ漢字する";
        var hints = new[]
        {
            new FuriganaHint(3, 2, "かんじ")
        };

        // Simulate preprocessing removing stuttering: あああ → あ
        var cleanedOriginal = "あ漢字する";
        var relocated = FuriganaHintExtractor.RelocateToCleanedOriginal(cleanedOriginal, hints, cleanText);

        relocated.Should().HaveCount(1);
        relocated[0].Offset.Should().Be(1);
        relocated[0].Reading.Should().Be("かんじ");
    }

    [Fact]
    public void RelocateToCleanedOriginal_DroppedHint()
    {
        var cleanText = "テスト漢字";
        var hints = new[]
        {
            new FuriganaHint(3, 2, "かんじ")
        };

        // cleanedOriginal doesn't contain the base text at all
        var cleanedOriginal = "テスト";
        var relocated = FuriganaHintExtractor.RelocateToCleanedOriginal(cleanedOriginal, hints, cleanText);

        relocated.Should().BeEmpty();
    }

    [Fact]
    public void Extract_MixedAnnotatedAndPlainText()
    {
        var (cleanText, hints) = FuriganaHintExtractor.Extract("今日は{天気'てんき}が良い");

        cleanText.Should().Be("今日は天気が良い");
        hints.Should().HaveCount(1);
        hints[0].Offset.Should().Be(3);
        hints[0].Length.Should().Be(2);
        hints[0].Reading.Should().Be("てんき");
    }
}
