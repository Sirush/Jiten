using FluentAssertions;
using Jiten.Core.Services;

namespace Jiten.Tests;

public class FranchiseNamingTests
{
    private static List<(string OriginalTitle, DateOnly ReleaseDate, int DeckId)> Members(params (string Title, int Year)[] titles) =>
        titles.Select((t, i) => (t.Title, new DateOnly(t.Year, 1, 1), i + 1)).ToList();

    private static string Suggest(params (string Title, int Year)[] titles) => FranchiseNaming.Suggest(Members(titles), []);

    [Fact]
    public void SingleSeries_NamesTheFranchise()
    {
        var name = FranchiseNaming.Suggest(Members(("ファイナルファンタジーVII", 1997), ("クライシス コア", 2007)), ["Final Fantasy"]);

        name.Should().Be("Final Fantasy");
    }

    [Fact]
    public void SeveralSeries_FallBackToTitles()
    {
        var name = FranchiseNaming.Suggest(Members(("ファイナルファンタジーVII", 1997), ("ファイナルファンタジーX", 2001)), ["Final Fantasy", "Compilation"]);

        name.Should().Be("ファイナルファンタジー");
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
        var members = new List<(string OriginalTitle, DateOnly ReleaseDate, int DeckId)>
        {
            ("Unknown date", new DateOnly(1, 1, 1), 1),
            ("Later", new DateOnly(2010, 5, 1), 2),
            ("Earlier tie, higher id", new DateOnly(2005, 1, 1), 4),
            ("Earlier tie, lower id", new DateOnly(2005, 1, 1), 3)
        };

        FranchiseNaming.Suggest(members, []).Should().Be("Earlier tie, lower id");
    }

    [Fact]
    public void LongNames_AreClamped()
    {
        var title = new string('あ', 250);
        FranchiseNaming.Suggest(Members((title, 2000)), []).Should().HaveLength(FranchiseNaming.MaxNameLength);
    }
}
