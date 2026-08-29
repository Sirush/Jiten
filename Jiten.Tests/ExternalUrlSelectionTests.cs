using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Core.Data.Providers;

namespace Jiten.Tests;

public class ExternalUrlSelectionTests
{
    private static List<Link> Links(LinkType linkType, params string[] urls) =>
        urls.Select(u => new Link { LinkType = linkType, Url = u }).ToList();

    [Theory]
    [InlineData("https://vndb.org/r5678", "https://vndb.org/v1234")]
    [InlineData("https://vndb.org/v1234", "https://vndb.org/r5678")]
    public void Vndb_PicksVisualNovelLinkWhateverTheOrder(string first, string second)
    {
        var links = Links(LinkType.Vndb, first, second);

        ExternalUrlParser.TryParseFirst(links, LinkType.Vndb, out var result).Should().BeTrue();
        result.LinkType.Should().Be(LinkType.Vndb);
        result.Id.Should().Be("v1234");
    }

    [Fact]
    public void Vndb_ReleaseLinkAlone_ReturnsFalse()
    {
        ExternalUrlParser.TryParseFirst(Links(LinkType.Vndb, "https://vndb.org/r5678"), LinkType.Vndb, out var result)
                         .Should().BeFalse();
        result.Should().Be(default(ExternalUrlRef));
    }

    [Fact]
    public void NoLinks_ReturnsFalse()
    {
        ExternalUrlParser.TryParseFirst([], LinkType.Vndb, out _).Should().BeFalse();
    }

    [Fact]
    public void OtherLinkTypesAreIgnored()
    {
        var links = Links(LinkType.Anilist, "https://anilist.co/anime/123");
        links.AddRange(Links(LinkType.Vndb, "https://vndb.org/v1234"));

        ExternalUrlParser.TryParseFirst(links, LinkType.Vndb, out var result).Should().BeTrue();
        result.Id.Should().Be("v1234");
    }

    [Theory]
    [InlineData("https://anilist.co/user/someone", "https://anilist.co/manga/456")]
    [InlineData("https://anilist.co/manga/456", "https://anilist.co/user/someone")]
    public void Anilist_SkipsNonWorkLinkWhateverTheOrder(string first, string second)
    {
        var links = Links(LinkType.Anilist, first, second);

        ExternalUrlParser.TryParseFirst(links, LinkType.Anilist, out var result).Should().BeTrue();
        result.Id.Should().Be("456");
        result.Kind.Should().Be(ExternalUrlKind.Manga);
    }

    [Fact]
    public void ReturnsFirstParseableWhenSeveralAreUsable()
    {
        var links = Links(LinkType.Vndb, "https://vndb.org/v18368", "https://vndb.org/v20105");

        ExternalUrlParser.TryParseFirst(links, LinkType.Vndb, out var result).Should().BeTrue();
        result.Id.Should().Be("v18368");
    }
}
