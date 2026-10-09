using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Core.Services;

namespace Jiten.Tests;

public class FranchiseNamingTests
{
    private static List<(GroupTitles Titles, DateOnly ReleaseDate, int DeckId)> Members(params (string Title, int Year)[] titles) =>
        titles.Select((t, i) => (new GroupTitles(t.Title, null, null), new DateOnly(t.Year, 1, 1), i + 1)).ToList();

    private static GroupTitles Titles(string original) => new(original, null, null);

    private static string Suggest(params (string Title, int Year)[] titles) => FranchiseNaming.Suggest(Members(titles), []).OriginalTitle;

    [Fact]
    public void SingleSeries_NamesTheFranchise()
    {
        var series = new GroupTitles("ファイナルファンタジー", "Final Fantasy", "Final Fantasy");
        var titles = FranchiseNaming.Suggest(Members(("ファイナルファンタジーVII", 1997), ("クライシス コア", 2007)), [series]);

        titles.Should().Be(series);
    }

    [Fact]
    public void SeveralSeries_FallBackToTitles()
    {
        var name = FranchiseNaming.Suggest(Members(("ファイナルファンタジーVII", 1997), ("ファイナルファンタジーX", 2001)),
                                           [Titles("Final Fantasy"), Titles("Compilation")]);

        name.OriginalTitle.Should().Be("ファイナルファンタジー");
    }

    [Fact]
    public void Prefix_IsTakenPerLanguage()
    {
        var members = new List<(GroupTitles Titles, DateOnly ReleaseDate, int DeckId)>
        {
            (new GroupTitles("ペルソナ3", "Persona 3", "Persona 3"), new DateOnly(2006, 1, 1), 1),
            (new GroupTitles("ペルソナ4", "Persona 4", null), new DateOnly(2008, 1, 1), 2),
            (new GroupTitles("ペルソナ5", "Persona 5", "Persona 5"), new DateOnly(2016, 1, 1), 3)
        };

        FranchiseNaming.Suggest(members, []).Should().Be(new GroupTitles("ペルソナ", "Persona", "Persona"));
    }

    [Fact]
    public void Prefix_LeavesALanguageEmptyWhenTooFewTitlesShareIt()
    {
        var members = new List<(GroupTitles Titles, DateOnly ReleaseDate, int DeckId)>
        {
            (new GroupTitles("ペルソナ3", null, "Persona 3"), new DateOnly(2006, 1, 1), 1),
            (new GroupTitles("ペルソナ4", null, null), new DateOnly(2008, 1, 1), 2),
            (new GroupTitles("ペルソナ5", null, null), new DateOnly(2016, 1, 1), 3)
        };

        FranchiseNaming.Suggest(members, []).Should().Be(new GroupTitles("ペルソナ", null, null));
    }

    [Fact]
    public void Prefix_ThatIsAMembersTitle_TakesItsTitles()
    {
        var members = new List<(GroupTitles Titles, DateOnly ReleaseDate, int DeckId)>
        {
            (new GroupTitles("四月は君の嘘 特別編", null, "Your Lie in April: Special"), new DateOnly(2015, 1, 1), 1),
            (new GroupTitles("四月は君の嘘", "Shigatsu wa Kimi no Uso", "Your Lie in April"), new DateOnly(2014, 1, 1), 2)
        };

        FranchiseNaming.Suggest(members, []).Should().Be(new GroupTitles("四月は君の嘘", "Shigatsu wa Kimi no Uso", "Your Lie in April"));
    }

    [Fact]
    public void TranslatedPrefix_MustBeSharedByAllAndEndOnAWord()
    {
        var members = new List<(GroupTitles Titles, DateOnly ReleaseDate, int DeckId)>
        {
            (new GroupTitles("ATRI -My Dear Moments-", "ATRI -My Dear Moments-", "ATRI -My Dear Moments-"), new DateOnly(2020, 1, 1), 1),
            (new GroupTitles("ATRI -My Dear Moments- 外伝", "ATRIA", "The Side Story"), new DateOnly(2021, 1, 1), 2),
            (new GroupTitles("ATRI -My Dear Moments- 前日譚", null, "The Prequel"), new DateOnly(2022, 1, 1), 3)
        };

        FranchiseNaming.Suggest(members, []).Should().Be(new GroupTitles("ATRI -My Dear Moments", null, null));
    }

    [Fact]
    public void TranslatedPrefix_ThatIsOnlyAnArticle_IsDropped()
    {
        var members = new List<(GroupTitles Titles, DateOnly ReleaseDate, int DeckId)>
        {
            (new GroupTitles("涼宮ハルヒの憂鬱", null, "The Melancholy of Haruhi Suzumiya"), new DateOnly(2006, 1, 1), 1),
            (new GroupTitles("涼宮ハルヒの消失", null, "The Disappearance of Haruhi Suzumiya"), new DateOnly(2010, 1, 1), 2)
        };

        FranchiseNaming.Suggest(members, []).EnglishTitle.Should().BeNull();
    }

    [Fact]
    public void Fallback_KeepsTheEarliestMembersTitles()
    {
        var members = new List<(GroupTitles Titles, DateOnly ReleaseDate, int DeckId)>
        {
            (new GroupTitles("真・女神転生", "Shin Megami Tensei", " "), new DateOnly(1992, 1, 1), 1),
            (new GroupTitles("デビルサマナー", "Devil Summoner", "Devil Summoner"), new DateOnly(1995, 1, 1), 2)
        };

        FranchiseNaming.Suggest(members, []).Should().Be(new GroupTitles("真・女神転生", "Shin Megami Tensei", null));
    }

    [Fact]
    public void RomanNumerals_AreTrimmedFromThePrefix()
    {
        Suggest(("ファイナルファンタジーIV", 1991), ("ファイナルファンタジーVI", 1994), ("ファイナルファンタジーIX", 2000))
            .Should().Be("ファイナルファンタジー");
    }

    [Fact]
    public void FullWidthNumeralsAndSeparators_AreTrimmed()
    {
        Suggest(("ドラゴンクエストⅢ そして伝説へ…", 1988), ("ドラゴンクエストⅤ 天空の花嫁", 1992), ("ドラゴンクエスト１１", 2017))
            .Should().Be("ドラゴンクエスト");
        Suggest(("ポケットモンスター 赤", 1996), ("ポケットモンスター　金", 1999), ("ポケットモンスター：ルビー", 2002))
            .Should().Be("ポケットモンスター");
    }

    [Fact]
    public void Prefix_NeedsSixtyPercentOfTheMembers()
    {
        // 3 of 5 share the prefix: exactly 60 %.
        Suggest(("ペルソナ3", 2006), ("ペルソナ4", 2008), ("ペルソナ5", 2016), ("真・女神転生", 1992), ("デビルサマナー", 1995))
            .Should().Be("ペルソナ");

        // 2 of 4 is not enough; the earliest title wins.
        Suggest(("ペルソナ3", 2006), ("ペルソナ4", 2008), ("真・女神転生", 1992), ("デビルサマナー", 1995))
            .Should().Be("真・女神転生");
    }

    [Fact]
    public void ShortPrefix_IsRejected()
    {
        Suggest(("To LOVEる", 2006), ("ToHeart", 1997)).Should().Be("ToHeart");
        Suggest(("AB-2", 2002), ("AB-1", 2001)).Should().Be("AB-1");
    }

    [Fact]
    public void Fallback_PicksTheEarliestRelease_UnknownDatesLast()
    {
        var members = new List<(GroupTitles Titles, DateOnly ReleaseDate, int DeckId)>
        {
            (Titles("Unknown date"), new DateOnly(1, 1, 1), 1),
            (Titles("Later"), new DateOnly(2010, 5, 1), 2),
            (Titles("Earlier tie, higher id"), new DateOnly(2005, 1, 1), 4),
            (Titles("Earlier tie, lower id"), new DateOnly(2005, 1, 1), 3)
        };

        FranchiseNaming.Suggest(members, []).OriginalTitle.Should().Be("Earlier tie, lower id");
    }

    [Fact]
    public void LongNames_AreClamped()
    {
        var title = new string('あ', 250);
        FranchiseNaming.Suggest(Members((title, 2000)), []).OriginalTitle.Should().HaveLength(FranchiseNaming.MaxNameLength);
    }
}
