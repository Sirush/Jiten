using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Parser;
using Jiten.Parser.Diagnostics;

namespace Jiten.Tests;

public class TokenModificationDetectionTests
{
    private static MorphologicalAnalyser.TokenSnapshot Snap(
        string text, PartOfSpeech pos = PartOfSpeech.Noun, string reading = "", string dictionaryForm = "",
        string normalizedForm = "", int? preMatchedWordId = null, byte? preMatchedReadingIndex = null, bool isInvalid = false) =>
        new(text, pos, default, reading, dictionaryForm, normalizedForm, preMatchedWordId, preMatchedReadingIndex, isInvalid);

    private static List<TokenModification> Detect(
        List<MorphologicalAnalyser.TokenSnapshot> input, List<MorphologicalAnalyser.TokenSnapshot> output) =>
        MorphologicalAnalyser.DetectModifications(input, output);

    [Fact]
    public void IdenticalListsProduceNoModifications()
    {
        var tokens = new List<MorphologicalAnalyser.TokenSnapshot> { Snap("飲ん"), Snap("だ"), Snap("から") };
        Detect(tokens, [.. tokens]).Should().BeEmpty();
    }

    [Fact]
    public void AdjacentTokensMergingIsReportedAsMerge()
    {
        var mods = Detect([Snap("飲ん"), Snap("だ"), Snap("から")], [Snap("飲んだ"), Snap("から")]);

        mods.Should().ContainSingle();
        mods[0].Type.Should().Be("merge");
        mods[0].InputTokens.Should().Equal("飲ん", "だ");
        mods[0].OutputTokens.Should().Equal("飲んだ");
        mods[0].InputIndex.Should().Be(0);
        mods[0].OutputIndex.Should().Be(0);
    }

    [Fact]
    public void TokenSplittingIsReportedAsSplit()
    {
        var mods = Detect([Snap("それ"), Snap("食べた")], [Snap("それ"), Snap("食べ"), Snap("た")]);

        mods.Should().ContainSingle();
        mods[0].Type.Should().Be("split");
        mods[0].InputTokens.Should().Equal("食べた");
        mods[0].OutputTokens.Should().Equal("食べ", "た");
        mods[0].InputIndex.Should().Be(1);
    }

    [Fact]
    public void MidListRemovalIsReported()
    {
        var mods = Detect([Snap("あ"), Snap("い"), Snap("う")], [Snap("あ"), Snap("う")]);

        mods.Should().ContainSingle();
        mods[0].Type.Should().Be("remove");
        mods[0].InputTokens.Should().Equal("い");
        mods[0].OutputTokens.Should().BeEmpty();
        mods[0].InputIndex.Should().Be(1);
    }

    [Fact]
    public void InsertionIsReported()
    {
        var mods = Detect([Snap("あ"), Snap("う")], [Snap("あ"), Snap("い"), Snap("う")]);

        mods.Should().ContainSingle();
        mods[0].Type.Should().Be("insert");
        mods[0].OutputTokens.Should().Equal("い");
        mods[0].OutputIndex.Should().Be(1);
    }

    [Fact]
    public void BoundaryMoveIsReportedAsResegment()
    {
        var mods = Detect(
            [Snap("愛さ"), Snap("れ"), Snap("て"), Snap("るってこと")],
            [Snap("愛さ"), Snap("れ"), Snap("てる"), Snap("って"), Snap("こと")]);

        mods.Should().ContainSingle();
        mods[0].Type.Should().Be("resegment");
        mods[0].InputTokens.Should().Equal("て", "るってこと");
        mods[0].OutputTokens.Should().Equal("てる", "って", "こと");
        mods[0].InputIndex.Should().Be(2);
    }

    [Fact]
    public void TextRewriteIsReportedAsReplace()
    {
        var mods = Detect([Snap("は"), Snap("ゑ")], [Snap("は"), Snap("え")]);

        mods.Should().ContainSingle();
        mods[0].Type.Should().Be("replace");
        mods[0].InputTokens.Should().Equal("ゑ");
        mods[0].OutputTokens.Should().Equal("え");
    }

    [Fact]
    public void AttributeOnlyChangeIsReportedAsReclassify()
    {
        var mods = Detect(
            [Snap("猫"), Snap("通り", reading: "トオリ")],
            [Snap("猫"), Snap("通り", reading: "ドオリ")]);

        mods.Should().ContainSingle();
        mods[0].Type.Should().Be("reclassify");
        mods[0].InputTokens.Should().Equal("通り");
        mods[0].Reason.Should().Contain("reading").And.Contain("トオリ").And.Contain("ドオリ");
    }

    [Fact]
    public void PinChangeIsReportedAsReclassify()
    {
        var mods = Detect(
            [Snap("通り")],
            [Snap("通り", preMatchedWordId: 1432930)]);

        mods.Should().ContainSingle();
        mods[0].Type.Should().Be("reclassify");
        mods[0].Reason.Should().Contain("1432930");
    }

    [Fact]
    public void RepeatedSurfacesGetDistinctIndices()
    {
        var mods = Detect(
            [Snap("は"), Snap("猫"), Snap("は")],
            [Snap("は"), Snap("猫"), Snap("は", pos: PartOfSpeech.Particle)]);

        mods.Should().ContainSingle();
        mods[0].Type.Should().Be("reclassify");
        mods[0].InputIndex.Should().Be(2);
        mods[0].OutputIndex.Should().Be(2);
    }

    [Fact]
    public void ChangesAtBothEndsSurviveTrimming()
    {
        var mods = Detect(
            [Snap("次", pos: PartOfSpeech.Prefix), Snap("の"), Snap("飲ん"), Snap("だ")],
            [Snap("次", pos: PartOfSpeech.Noun), Snap("の"), Snap("飲んだ")]);

        mods.Should().HaveCount(2);
        mods[0].Type.Should().Be("reclassify");
        mods[0].InputIndex.Should().Be(0);
        mods[1].Type.Should().Be("merge");
        mods[1].InputIndex.Should().Be(2);
        mods[1].OutputIndex.Should().Be(2);
    }
}
