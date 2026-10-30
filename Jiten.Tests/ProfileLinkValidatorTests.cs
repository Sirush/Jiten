using FluentAssertions;
using Jiten.Api.Helpers;
using Xunit;

namespace Jiten.Parser.Tests;

public class ProfileLinkValidatorTests
{
    [Theory]
    [InlineData("https://x.com/someone")]
    [InlineData("https://www.youtube.com/@channel")]
    [InlineData("https://anilist.co/user/Someone")]
    [InlineData("https://my-blog.example.jp/posts/日本語")]
    public void AcceptsOrdinaryHttpsLinks(string link) =>
        ProfileLinkValidator.Validate(link).Should().BeNull();

    [Theory]
    [InlineData("http://example.com", "https://")]
    [InlineData("javascript:alert(1)", "full https")]
    [InlineData("example.com", "full https")]
    [InlineData("https://google.com@evil.example", "full https")]
    [InlineData("https://1.2.3.4/page", "IP address")]
    [InlineData("https://[::1]/page", "IP address")]
    [InlineData("https://localhost/page", "IP address")]
    [InlineData("https://bit.ly/abc", "shorteners")]
    [InlineData("https://www.tinyurl.com/abc", "shorteners")]
    [InlineData("https://example-porn.com", "isn't allowed")]
    [InlineData("https://example.com/Hentai/list", "isn't allowed")]
    [InlineData("https://example.com/%68entai", "isn't allowed")]
    [InlineData("https://example.com/fuck", "isn't allowed")]
    public void RejectsWithAReason(string link, string reason) =>
        ProfileLinkValidator.Validate(link).Should().Contain(reason);

    [Fact]
    public void RejectsOverlongLinks() =>
        ProfileLinkValidator.Validate("https://example.com/" + new string('a', ProfileLinkValidator.MaxLength)).Should().NotBeNull();

    [Fact]
    public void LegacyHttpLinksStillDisplayButBlockedOnesDoNot()
    {
        ProfileLinkValidator.IsDisplayable("http://example.com").Should().BeTrue();
        ProfileLinkValidator.IsDisplayable("http://example.com/hentai").Should().BeFalse();
        ProfileLinkValidator.IsDisplayable("https://bit.ly/abc").Should().BeFalse();
    }
}
