using FluentAssertions;
using Jiten.Api.Helpers;

namespace Jiten.Tests;

public class TokenPositionHelperTests
{
    [Fact]
    public void ExactMatch_ReturnsPositionAndTokenLength()
    {
        var (pos, len) = TokenPositionHelper.FindTokenInSource("じゃない", "ない", 0);
        pos.Should().Be(2);
        len.Should().Be(2);
    }

    [Fact]
    public void ExactMatch_RespectsStartFrom()
    {
        var source = "ないものはない";
        var (pos, len) = TokenPositionHelper.FindTokenInSource(source, "ない", 3);
        pos.Should().Be(5);
        len.Should().Be(2);
    }

    [Fact]
    public void FuzzyMatch_SkipsInternalChoonpu()
    {
        var (pos, len) = TokenPositionHelper.FindTokenInSource("じゃなーい", "ない", 0);
        pos.Should().Be(2);
        len.Should().Be(3, "source span includes the skipped ー");
    }

    [Fact]
    public void FuzzyMatch_SkipsMultipleChoonpu()
    {
        var (pos, len) = TokenPositionHelper.FindTokenInSource("なーーい", "ない", 0);
        pos.Should().Be(0);
        len.Should().Be(4, "source span includes both skipped ー");
    }

    [Fact]
    public void FuzzyMatch_WorksInLongerText()
    {
        var source = "聞こえなーい";
        var (pos, len) = TokenPositionHelper.FindTokenInSource(source, "ない", 0);
        pos.Should().Be(3);
        len.Should().Be(3);
    }

    [Fact]
    public void FuzzyMatch_WorksWithTeFormPrefix()
    {
        var source = "作ってなーい";
        var (pos, len) = TokenPositionHelper.FindTokenInSource(source, "ない", 0);
        pos.Should().Be(3);
        len.Should().Be(3);
    }

    [Fact]
    public void ExactMatch_PreferredOverFuzzy()
    {
        // When exact match exists, should return token.Length not a longer span
        var (pos, len) = TokenPositionHelper.FindTokenInSource("ない", "ない", 0);
        pos.Should().Be(0);
        len.Should().Be(2);
    }

    [Fact]
    public void NoMatch_ReturnsNegativeOne()
    {
        var (pos, len) = TokenPositionHelper.FindTokenInSource("こんにちは", "ない", 0);
        pos.Should().Be(-1);
        len.Should().Be(0);
    }

    [Fact]
    public void FuzzyMatch_DoesNotSkipNonChoonpuCharacters()
    {
        // ー is the only character that gets skipped; other characters should not be ignored
        var (pos, _) = TokenPositionHelper.FindTokenInSource("なXい", "ない", 0);
        pos.Should().Be(-1);
    }

    [Fact]
    public void ExactMatch_TakesPriorityOverLeadingChoonpu()
    {
        // "ーない" contains an exact match for "ない" at index 1, so the exact path wins
        var (pos, len) = TokenPositionHelper.FindTokenInSource("ーない", "ない", 0);
        pos.Should().Be(1);
        len.Should().Be(2);
    }

    [Fact]
    public void ExactMatch_EmptyToken()
    {
        var (pos, len) = TokenPositionHelper.FindTokenInSource("何か", "", 0);
        pos.Should().Be(0);
        len.Should().Be(0);
    }

    [Fact]
    public void FuzzyMatch_StartFromMiddle()
    {
        var source = "なーいXXなーい";
        var (pos, len) = TokenPositionHelper.FindTokenInSource(source, "ない", 4);
        pos.Should().Be(5);
        len.Should().Be(3);
    }

    [Fact]
    public void ReversePreprocessing_TondemoNee()
    {
        var (pos, len) = TokenPositionHelper.FindTokenInSource("とんでもねえ", "とんでもない", 0);
        pos.Should().Be(0);
        len.Should().Be(6);
    }

    [Fact]
    public void ReversePreprocessing_ShougaNee()
    {
        var (pos, len) = TokenPositionHelper.FindTokenInSource("しょうがねえ", "しょうがない", 0);
        pos.Should().Be(0);
        len.Should().Be(6);
    }

    [Fact]
    public void ReversePreprocessing_KoiKatakana()
    {
        var (pos, len) = TokenPositionHelper.FindTokenInSource("来イ", "来い", 0);
        pos.Should().Be(0);
        len.Should().Be(2);
    }

    [Fact]
    public void ReversePreprocessing_See()
    {
        var (pos, len) = TokenPositionHelper.FindTokenInSource("見せぇ", "見さい", 0);
        pos.Should().Be(0);
        len.Should().Be(3);
    }

    [Fact]
    public void ReversePreprocessing_StandardFormStillWorks()
    {
        // If source has the standard form, exact match should find it
        var (pos, len) = TokenPositionHelper.FindTokenInSource("付き合っていられない", "付き合っていられない", 0);
        pos.Should().Be(0);
        len.Should().Be(10);
    }

    [Fact]
    public void ReversePreprocessing_KubiVariantKanji()
    {
        // PreprocessText normalizes 頚 → 頸; the token must still map onto variant-kanji source
        var (pos, len) = TokenPositionHelper.FindTokenInSource("切断された頚動脈から", "頸動脈", 0);
        pos.Should().Be(5);
        len.Should().Be(3);
    }

    [Fact]
    public void ReversePreprocessing_KubiStandardFormStillWorks()
    {
        var (pos, len) = TokenPositionHelper.FindTokenInSource("切断された頸動脈から", "頸動脈", 0);
        pos.Should().Be(5);
        len.Should().Be(3);
    }

    [Fact]
    public void FuzzyMatch_SkipsEllipsis()
    {
        var (pos, len) = TokenPositionHelper.FindTokenInSource("襲撃……されて", "襲撃されて", 0);
        pos.Should().Be(0);
        len.Should().Be(7, "source span includes the skipped ……");
    }

    [Fact]
    public void FuzzyMatch_SkipsEllipsisMultipleGroups()
    {
        var source = "脱走……したそう";
        var (pos, len) = TokenPositionHelper.FindTokenInSource(source, "脱走したそう", 0);
        pos.Should().Be(0);
        len.Should().Be(8, "source span includes the skipped ……");
    }
}
