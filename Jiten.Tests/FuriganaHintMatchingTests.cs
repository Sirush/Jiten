using FluentAssertions;
using Jiten.Parser;
using Xunit;

namespace Jiten.Tests;

public class FuriganaHintMatchingTests
{
    private static WordInfo Token(string text, int startOffset) => new() { Text = text, StartOffset = startOffset };

    private static string? ReadingFor(WordInfo token, params FuriganaHint[] hints) =>
        Jiten.Parser.Parser.FindMatchingHint(hints, token)?.Reading;

    [Fact]
    public void WholeTokenHint_IsReturnedAsIs()
    {
        ReadingFor(Token("羚羊", 3), new FuriganaHint(3, 2, "かもしか")).Should().Be("かもしか");
    }

    [Fact]
    public void PerKanjiHints_AreJoined()
    {
        ReadingFor(Token("羚羊", 3), new FuriganaHint(3, 1, "かも"), new FuriganaHint(4, 1, "しか")).Should().Be("かもしか");
    }

    [Fact]
    public void PerKanjiHints_KeepTrailingOkurigana()
    {
        ReadingFor(Token("微笑む", 0), new FuriganaHint(0, 1, "ほほ"), new FuriganaHint(1, 1, "え")).Should().Be("ほほえむ");
    }

    [Fact]
    public void Hints_KeepKanaBetweenAndBeforeThem()
    {
        ReadingFor(Token("お取り寄せ", 0), new FuriganaHint(1, 1, "と"), new FuriganaHint(3, 1, "よ")).Should().Be("おとりよせ");
    }

    [Fact]
    public void SingleLeadingHint_KeepsOkurigana()
    {
        ReadingFor(Token("開く", 5), new FuriganaHint(5, 1, "あ")).Should().Be("あく");
    }

    [Fact]
    public void HintsOfNeighbouringTokens_AreIgnored()
    {
        var hints = new[] { new FuriganaHint(0, 2, "とうきょう"), new FuriganaHint(2, 1, "と") };

        Jiten.Parser.Parser.FindMatchingHint(hints, Token("都", 2))!.Value.Reading.Should().Be("と");
        Jiten.Parser.Parser.FindMatchingHint(hints, Token("東京", 0))!.Value.Reading.Should().Be("とうきょう");
    }

    [Fact]
    public void HintSpanningPastTheToken_IsIgnored()
    {
        ReadingFor(Token("大", 0), new FuriganaHint(0, 3, "だいにんき")).Should().BeNull();
    }

    [Fact]
    public void TokenWithoutHints_ReturnsNull()
    {
        ReadingFor(Token("猫", 10), new FuriganaHint(0, 2, "かんじ")).Should().BeNull();
    }
}
