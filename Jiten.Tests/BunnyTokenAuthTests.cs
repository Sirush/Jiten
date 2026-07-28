using FluentAssertions;
using Jiten.Core;
using Xunit;

namespace Jiten.Parser.Tests;

public class BunnyTokenAuthTests
{
    [Fact]
    public void Sign_MatchesKnownVector()
    {
        // Vector computed independently (Python hashlib): SHA256("testkey123" + path + "2000000000"),
        // Base64 with +/ -> -_ and '=' stripped.
        var token = BunnyTokenAuth.Sign("testkey123", "/card-media/u/100_0_image.png", 2000000000);
        token.Should().Be("9PJ3w6M_4XH6Uh4YmFRlJWLcAVqv6CT7NlpaWSO2e7w");
    }

    [Fact]
    public void Sign_IsBase64Url_NoPaddingOrUnsafeChars()
    {
        var token = BunnyTokenAuth.Sign("secret", "/x/y.mp3", 1700000000);
        token.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
    }

    [Fact]
    public void BuildSignedUrl_HasTokenAndExpiresParams()
    {
        var url = BunnyTokenAuth.BuildSignedUrl("https://secure.example.b-cdn.net/", "testkey123",
                                                "/card-media/u/100_0_image.png", 2000000000);
        url.Should().Be("https://secure.example.b-cdn.net/card-media/u/100_0_image.png" +
                        "?token=9PJ3w6M_4XH6Uh4YmFRlJWLcAVqv6CT7NlpaWSO2e7w&expires=2000000000");
    }
}
