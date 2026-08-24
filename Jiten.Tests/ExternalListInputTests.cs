using FluentAssertions;
using Jiten.Api.Services.ExternalMediaList;

namespace Jiten.Tests;

public class ExternalListInputTests
{
    [Theory]
    [InlineData("TestUser", "TestUser")]
    [InlineData("  TestUser  ", "TestUser")]
    [InlineData("https://anilist.co/user/TestUser/", "TestUser")]
    [InlineData("https://anilist.co/user/TestUser", "TestUser")]
    [InlineData("https://anilist.co/user/TestUser/animelist", "TestUser")]
    [InlineData("anilist.co/user/TestUser?tab=overview", "TestUser")]
    [InlineData("https://ANILIST.co/USER/TestUser/", "TestUser")]
    [InlineData("https://anilist.co/user/Some%20User/", "Some User")]
    public void Normalize_Anilist(string input, string expected)
    {
        ExternalListInput.Normalize(ExternalListProvider.Anilist, input).Should().Be(expected);
    }

    [Theory]
    [InlineData("testuser", "testuser")]
    [InlineData("u1234", "u1234")]
    [InlineData("https://vndb.org/u1234", "u1234")]
    [InlineData("https://vndb.org/u1234/", "u1234")]
    [InlineData("https://vndb.org/u1234/ulist?vnlist=1", "u1234")]
    [InlineData("vndb.org/u1234", "u1234")]
    [InlineData("https://VNDB.org/U1234", "u1234")]
    [InlineData("U1234", "u1234")]
    public void Normalize_Vndb(string input, string expected)
    {
        ExternalListInput.Normalize(ExternalListProvider.Vndb, input).Should().Be(expected);
    }

    [Fact]
    public void Normalize_UnrecognisedUrlIsLeftAlone()
    {
        ExternalListInput.Normalize(ExternalListProvider.Anilist, "https://myanimelist.net/profile/TestUser")
                         .Should().Be("https://myanimelist.net/profile/TestUser");
    }

    [Fact]
    public void Normalize_EmptyStaysEmpty()
    {
        ExternalListInput.Normalize(ExternalListProvider.Vndb, "   ").Should().BeEmpty();
    }
}
