using FluentAssertions;
using Jiten.Core.Services;
using Xunit;

namespace Jiten.Tests;

public class DescriptionKeywordsTests
{
    [Fact]
    public void LatinKeywordsMatchWholeWordsOnly()
    {
        var keywords = DescriptionKeywords.Extract("time loop");
        keywords.Should().Contain("lop");

        DescriptionKeywords.Hits(keywords, DescriptionKeywords.Fold("he must develop a plan sometimes")).Should().BeEmpty();
        DescriptionKeywords.Hits(keywords, DescriptionKeywords.Fold("stuck in a time loop.")).Should().BeEquivalentTo(["time", "lop"]);
        DescriptionKeywords.Hits(keywords, DescriptionKeywords.Fold("The loop won't end")).Should().BeEquivalentTo(["lop"]);
    }

    [Fact]
    public void JapaneseKeywordsMatchAsSubstrings()
    {
        var keywords = DescriptionKeywords.Extract("陰陽師の話");
        DescriptionKeywords.Hits(keywords, DescriptionKeywords.Fold("平安の陰陽師が活躍する")).Should().Contain("陰陽師");
    }

    [Fact]
    public void FunctionWordsAreNotKeywords()
    {
        DescriptionKeywords.Extract("the protagonist keeps dying every day").Should().BeEquivalentTo(["protagonist", "dying", "day"]);
    }
}
