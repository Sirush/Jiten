using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Core.Data.Providers;

namespace Jiten.Tests;

public class ExternalUrlParserTests
{
    [Theory]
    [InlineData("https://vndb.org/v1234", "v1234")]
    [InlineData("https://vndb.org/v1234/", "v1234")]
    [InlineData("https://vndb.org/v1234/chars", "v1234")]
    [InlineData("https://www.vndb.org/V1234?q=1", "v1234")]
    public void Vndb_ReturnsPrefixedId(string url, string expected)
    {
        ExternalUrlParser.TryParse(url, out var result).Should().BeTrue();
        result.LinkType.Should().Be(LinkType.Vndb);
        result.Id.Should().Be(expected);
        result.Kind.Should().Be(ExternalUrlKind.Unknown);
    }

    [Theory]
    [InlineData("https://vndb.org/r1234")]
    [InlineData("https://vndb.org/c1234")]
    [InlineData("https://vndb.org/p123")]
    [InlineData("https://vndb.org/s123")]
    [InlineData("https://vndb.org/v")]
    [InlineData("https://vndb.org")]
    public void Vndb_NonVisualNovelPaths_ReturnFalse(string url)
    {
        ExternalUrlParser.TryParse(url, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://anilist.co/anime/123", "123", ExternalUrlKind.Anime)]
    [InlineData("https://anilist.co/anime/123/Some-Slug/", "123", ExternalUrlKind.Anime)]
    [InlineData("https://anilist.co/manga/456/Title/characters", "456", ExternalUrlKind.Manga)]
    public void Anilist_ReturnsIdAndKind(string url, string expectedId, ExternalUrlKind expectedKind)
    {
        ExternalUrlParser.TryParse(url, out var result).Should().BeTrue();
        result.LinkType.Should().Be(LinkType.Anilist);
        result.Id.Should().Be(expectedId);
        result.Kind.Should().Be(expectedKind);
    }

    [Theory]
    [InlineData("https://anilist.co/user/someone")]
    [InlineData("https://anilist.co/anime/notanumber")]
    [InlineData("https://anilist.co/anime")]
    public void Anilist_NonWorkPaths_ReturnFalse(string url)
    {
        ExternalUrlParser.TryParse(url, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://myanimelist.net/anime/1/Cowboy_Bebop", "1", ExternalUrlKind.Anime)]
    [InlineData("https://myanimelist.net/manga/2/Berserk", "2", ExternalUrlKind.Manga)]
    [InlineData("https://myanimelist.net/anime/1/Cowboy_Bebop/episode/3", "1", ExternalUrlKind.Anime)]
    public void Mal_ReturnsIdAndKind(string url, string expectedId, ExternalUrlKind expectedKind)
    {
        ExternalUrlParser.TryParse(url, out var result).Should().BeTrue();
        result.LinkType.Should().Be(LinkType.Mal);
        result.Id.Should().Be(expectedId);
        result.Kind.Should().Be(expectedKind);
    }

    [Theory]
    [InlineData("https://www.themoviedb.org/movie/550-fight-club", "550", ExternalUrlKind.Movie)]
    [InlineData("https://www.themoviedb.org/movie/550", "550", ExternalUrlKind.Movie)]
    [InlineData("https://www.themoviedb.org/tv/1396-breaking-bad/season/1", "1396", ExternalUrlKind.Tv)]
    [InlineData("https://themoviedb.org/tv/1396?language=ja", "1396", ExternalUrlKind.Tv)]
    public void Tmdb_StripsSlugAndKeepsKind(string url, string expectedId, ExternalUrlKind expectedKind)
    {
        ExternalUrlParser.TryParse(url, out var result).Should().BeTrue();
        result.LinkType.Should().Be(LinkType.Tmdb);
        result.Id.Should().Be(expectedId);
        result.Kind.Should().Be(expectedKind);
    }

    [Theory]
    [InlineData("https://www.themoviedb.org/person/123-someone")]
    [InlineData("https://www.themoviedb.org/movie/fight-club")]
    public void Tmdb_NonWorkPaths_ReturnFalse(string url)
    {
        ExternalUrlParser.TryParse(url, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://books.google.com/books?id=abcDEF123", "abcDEF123")]
    [InlineData("https://books.google.co.jp/books/about/Title.html?id=abcDEF123&hl=ja", "abcDEF123")]
    [InlineData("https://www.google.com/books/edition/Some_Title/abcDEF123", "abcDEF123")]
    [InlineData("https://www.google.com/books/edition/_/abcDEF123?hl=en", "abcDEF123")]
    public void GoogleBooks_ReturnsVolumeId(string url, string expected)
    {
        ExternalUrlParser.TryParse(url, out var result).Should().BeTrue();
        result.LinkType.Should().Be(LinkType.GoogleBooks);
        result.Id.Should().Be(expected);
    }

    [Fact]
    public void GoogleBooks_WithoutVolumeId_ReturnsFalse()
    {
        ExternalUrlParser.TryParse("https://books.google.com/books", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://www.igdb.com/games/some-game", "https://www.igdb.com/games/some-game")]
    [InlineData("https://www.igdb.com/games/some-game/", "https://www.igdb.com/games/some-game")]
    public void Igdb_KeepsFullUrlAsId(string url, string expected)
    {
        ExternalUrlParser.TryParse(url, out var result).Should().BeTrue();
        result.LinkType.Should().Be(LinkType.Igdb);
        result.Id.Should().Be(expected);
    }

    [Fact]
    public void Igdb_NonGamePath_ReturnsFalse()
    {
        ExternalUrlParser.TryParse("https://www.igdb.com/companies/nintendo", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://bookmeter.com/books/642866", "642866")]
    [InlineData("https://bookmeter.com/books/642866/", "642866")]
    [InlineData("https://www.bookmeter.com/books/642866?review=1", "642866")]
    public void Bookmeter_ReturnsBookId(string url, string expected)
    {
        ExternalUrlParser.TryParse(url, out var result).Should().BeTrue();
        result.LinkType.Should().Be(LinkType.Bookmeter);
        result.Id.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://bookmeter.com/users/12345")]
    [InlineData("https://bookmeter.com/books/not-a-number")]
    [InlineData("https://bookmeter.com/books")]
    public void Bookmeter_NonBookPaths_ReturnFalse(string url)
    {
        ExternalUrlParser.TryParse(url, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://www.imdb.com/title/tt0111161/", "tt0111161")]
    [InlineData("https://imdb.com/title/tt0111161", "tt0111161")]
    [InlineData("https://www.imdb.com/title/TT0111161/fullcredits", "tt0111161")]
    public void Imdb_ReturnsTitleId(string url, string expected)
    {
        ExternalUrlParser.TryParse(url, out var result).Should().BeTrue();
        result.LinkType.Should().Be(LinkType.Imdb);
        result.Id.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://www.imdb.com/name/nm0000151/")]
    [InlineData("https://www.imdb.com/title/notatitle/")]
    public void Imdb_NonTitlePaths_ReturnFalse(string url)
    {
        ExternalUrlParser.TryParse(url, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("vndb.org/v1234")]
    [InlineData("ftp://vndb.org/v1234")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://example.com/anime/123")]
    [InlineData("https://ncode.syosetu.com/n9669bk/")]
    [InlineData("https://www.amazon.co.jp/dp/B0000")]
    public void UnsupportedInput_ReturnsFalse(string? url)
    {
        ExternalUrlParser.TryParse(url, out var result).Should().BeFalse();
        result.Should().Be(default(ExternalUrlRef));
    }

    [Fact]
    public void LookalikeHost_IsNotMatched()
    {
        ExternalUrlParser.TryParse("https://vndb.org.evil.com/v1234", out _).Should().BeFalse();
    }
}
