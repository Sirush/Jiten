using FluentAssertions;
using Jiten.Core.Services;

namespace Jiten.Tests;

public class FranchiseSuggestionsTests
{
    private static List<TitleGroup> Groups(params (string Original, string? English, int? Franchise)[] decks)
    {
        var list = decks.Select((d, i) => new SuggestionDeck(i + 1, d.Original, d.English, d.Franchise)).ToList();
        return FranchiseSuggestions.Group(FranchiseSuggestions.FindPairs(list), list.ToDictionary(d => d.DeckId, d => d.FranchiseId));
    }

    private static List<TitleGroup> Groups(params string[] originals) => Groups(originals.Select(t => (t, (string?)null, (int?)null)).ToArray());

    [Fact]
    public void NumberedEntries_ShareTheirRoot()
    {
        var groups = Groups("日本統一", "日本統一2", "日本統一 北海道編", "日本統一外伝 山崎組");

        groups.Should().ContainSingle();
        groups[0].Root.Should().Be("日本統一");
        groups[0].DeckIds.Should().BeEquivalentTo([1, 2, 3, 4]);
    }

    [Fact]
    public void ShortKanaRoot_IsNotEnough() => Groups("ドラゴンクエスト", "ドラゴンボール").Should().BeEmpty();

    [Fact]
    public void RootCutInsideAKanjiWord_NeedsMore() => Groups("金田一少年の事件簿", "金田一耕助の冒険").Should().BeEmpty();

    [Fact]
    public void OneKatakanaStub_IsDropped()
    {
        Groups("リーガル・ハイ", "リーガル・ハート").Should().BeEmpty();
        Groups("リーガル・ハイ", "リーガル・ハイ スペシャル").Should().ContainSingle().Which.Root.Should().Be("リーガル・ハイ");
    }

    [Fact]
    public void LatinRoot_StopsAtAWordBoundary() => Groups("Dragon Quest", "Dragonball Quest").Should().BeEmpty();

    [Fact]
    public void LatinRoot_NeedsTwoContentWords()
    {
        Groups("Lost Friends", "Lost Friends 2 -Reason for Tears-").Should().ContainSingle().Which.Root.Should().Be("Lost Friends");
        Groups("The Promised Land", "The Promise of Spring").Should().BeEmpty();
    }

    [Fact]
    public void SharedEnglishPhrase_IsWeakerThanSharedOriginal()
    {
        Groups(("おはようコール", "Good Morning Call", null), ("おはよう先生", "Good Morning Teacher", null)).Should().BeEmpty();
    }

    [Fact]
    public void EnglishTitlesBetweenOriginals_DoNotHideThePair()
    {
        var groups = Groups(("WHITE ALBUM", "White Album", null), ("WHITE ALBUM2", "White Album 2", null));

        groups.Should().ContainSingle().Which.DeckIds.Should().BeEquivalentTo([1, 2]);
    }

    [Fact]
    public void TheatricalPrefix_IsIgnored()
    {
        var groups = Groups("劇場版 仮面ライダーアギト PROJECT G4", "仮面ライダー THE FIRST");

        groups.Should().ContainSingle().Which.Root.Should().Be("仮面ライダー");
    }

    [Fact]
    public void TrailingParticle_IsTrimmed()
    {
        Groups("ゼルダの伝説の旅", "ゼルダの伝説の夢").Should().ContainSingle().Which.Root.Should().Be("ゼルダの伝説");
    }

    [Fact]
    public void OneFranchiseAlone_IsNoSuggestion()
    {
        Groups(("星のカービィ", null, 7), ("星のカービィ2", null, 7)).Should().BeEmpty();
    }

    [Fact]
    public void IsolatedDeck_NextToAFranchise_IsSuggested()
    {
        var groups = Groups(("星のカービィ", null, 7), ("星のカービィ2", null, 7), ("星のカービィ3", null, null));

        groups.Should().ContainSingle().Which.DeckIds.Should().BeEquivalentTo([1, 2, 3]);
    }

    [Fact]
    public void TwoFranchisesWithoutAnIsolatedDeck_AreNoSuggestion()
    {
        Groups(("星のカービィ", null, 7), ("星のカービィ2", null, 8)).Should().BeEmpty();
    }

    [Fact]
    public void CommonWord_ExcludesTitlesNotTaggedAsWorks()
    {
        FranchiseSuggestions.IsCommonWord(["n"]).Should().BeTrue();
        FranchiseSuggestions.IsCommonWord(["n", "work"]).Should().BeFalse();
        FranchiseSuggestions.IsCommonWord(["place", "surname"]).Should().BeFalse();
    }
}
