using System.Text;
using FluentAssertions;
using Jiten.Api.Helpers;
using Jiten.Api.Services;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace Jiten.Tests;

public class DisplayNameValidatorTests
{
    private static string? Validate(string raw, bool allowReserved = false) =>
        DisplayNameValidator.Validate(DisplayNameValidator.Normalize(raw), allowReserved);

    [Theory]
    [InlineData("Tanaka")]
    [InlineData("taro_99")]
    [InlineData("_under")]
    [InlineData("a.b-c")]
    [InlineData("たなか")]
    [InlineData("タナカー")]
    [InlineData("山田々")]
    [InlineData("山田")]
    [InlineData("ab")]
    [InlineData("Modern")]
    [InlineData("Yamashita")]
    [InlineData("kiken")]
    [InlineData("torpedo")]
    [InlineData("Jitensha")]
    [InlineData("じてんしゃ好き")]
    [InlineData("badminton")]
    [InlineData("Cummings")]
    [InlineData("12345")]
    public void Accepts_ordinary_names(string name) => Validate(name).Should().BeNull();

    [Theory]
    [InlineData("a")]
    [InlineData("abcdefghijklmnopqrstu")]
    [InlineData("has space")]
    [InlineData("tony@aol.com")]
    [InlineData("plus+sign")]
    [InlineData(".dot")]
    [InlineData("-dash")]
    [InlineData("trailing_")]
    [InlineData("double__under")]
    [InlineData("a.-b")]
    [InlineData("name.json")]
    [InlineData("Ꭺdmin")]
    [InlineData("аdmin")]
    [InlineData("名前・名前")]
    [InlineData("😀smile")]
    public void Rejects_bad_format(string name) => Validate(name).Should().NotBeNull();

    [Fact]
    public void Folds_full_width_and_half_width_forms()
    {
        DisplayNameValidator.Normalize("  Ｔａｎａｋａ ").Should().Be("Tanaka");
        DisplayNameValidator.Normalize("ﾀﾅｶ").Should().Be("タナカ");
    }

    [Fact]
    public void Key_is_case_insensitive() =>
        DisplayNameValidator.ToKey("Tanaka").Should().Be(DisplayNameValidator.ToKey("tAnAkA"));

    [Theory]
    [InlineData("jiten")]
    [InlineData("Jiten")]
    [InlineData("jiten.moe")]
    [InlineData("jitenmoe")]
    [InlineData("JitenMoe")]
    [InlineData("jiten_moe")]
    [InlineData("j.i.t.e.n")]
    [InlineData("J1ten")]
    [InlineData("jiiiten")]
    [InlineData("TheJitenFan")]
    [InlineData("jitensha_jiten")]
    [InlineData("ジテン")]
    [InlineData("じてん")]
    [InlineData("辞典")]
    [InlineData("admin")]
    [InlineData("Administrator")]
    [InlineData("4dm1n")]
    [InlineData("SiteAdmin")]
    [InlineData("system")]
    [InlineData("5ystem")]
    [InlineData("moderator")]
    [InlineData("Mod_Tanaka")]
    [InlineData("ModTanaka")]
    [InlineData("m0d")]
    [InlineData("staff")]
    [InlineData("support")]
    [InlineData("TanakaBot")]
    [InlineData("official_account")]
    [InlineData("Sirus")]
    [InlineData("discourse")]
    [InlineData("管理人")]
    [InlineData("運営チーム")]
    [InlineData("everyone")]
    [InlineData("trust_level_0")]
    [InlineData("anonymous")]
    public void Refuses_reserved_names(string name) =>
        Validate(name).Should().Be(DisplayNameValidator.UnavailableMessage);

    [Theory]
    [InlineData("jiten")]
    [InlineData("admin")]
    [InlineData("Sirus")]
    public void Staff_may_use_reserved_names(string name) => Validate(name, allowReserved: true).Should().BeNull();

    [Theory]
    [InlineData("fuckface")]
    [InlineData("FUCK")]
    [InlineData("fuuuck")]
    [InlineData("f.u.c.k")]
    [InlineData("hitler88")]
    [InlineData("n1gger")]
    [InlineData("shit_poster")]
    [InlineData("ShitPoster")]
    [InlineData("sex")]
    [InlineData("ちんこ")]
    [InlineData("チンコ")]
    [InlineData("レイプ魔")]
    public void Refuses_offensive_names(string name) =>
        Validate(name, allowReserved: true).Should().Be(DisplayNameValidator.UnavailableMessage);
}

public class DiscourseConnectTests
{
    private static readonly DiscourseOptions Options = new() { Url = "https://community.test", SsoSecret = "secret" };

    private static (string Sso, string Sig) Payload(string query, string secret = "secret")
    {
        var sso = Convert.ToBase64String(Encoding.UTF8.GetBytes(query));
        return (sso, DiscourseConnect.Sign(sso, secret));
    }

    [Fact]
    public void Reads_a_correctly_signed_request()
    {
        var (sso, sig) = Payload("nonce=abc&return_sso_url=https%3A%2F%2Fcommunity.test%2Fsession%2Fsso_login");

        var request = DiscourseConnect.ReadRequest(sso, sig, Options);

        request.Should().NotBeNull();
        request!.Value.Nonce.Should().Be("abc");
        request.Value.ReturnUrl.Should().Be("https://community.test/session/sso_login");
    }

    [Fact]
    public void Rejects_a_request_signed_with_another_secret()
    {
        var (sso, sig) = Payload("nonce=abc&return_sso_url=https%3A%2F%2Fcommunity.test%2Fsession%2Fsso_login", "other");
        DiscourseConnect.ReadRequest(sso, sig, Options).Should().BeNull();
    }

    [Fact]
    public void Rejects_a_tampered_payload()
    {
        var (_, sig) = Payload("nonce=abc&return_sso_url=https%3A%2F%2Fcommunity.test%2Fsession%2Fsso_login");
        var (tampered, _) = Payload("nonce=xyz&return_sso_url=https%3A%2F%2Fcommunity.test%2Fsession%2Fsso_login");
        DiscourseConnect.ReadRequest(tampered, sig, Options).Should().BeNull();
    }

    [Theory]
    [InlineData("https%3A%2F%2Fevil.test%2Fsession%2Fsso_login")]
    [InlineData("https%3A%2F%2Fcommunity.test.evil.test%2Fsso")]
    public void Rejects_a_return_url_off_the_forum(string returnUrl)
    {
        var (sso, sig) = Payload($"nonce=abc&return_sso_url={returnUrl}");
        DiscourseConnect.ReadRequest(sso, sig, Options).Should().BeNull();
    }

    [Theory]
    [InlineData("not-hex")]
    [InlineData("")]
    public void Rejects_a_malformed_signature(string sig)
    {
        var (sso, _) = Payload("nonce=abc&return_sso_url=https%3A%2F%2Fcommunity.test%2Fsso");
        DiscourseConnect.ReadRequest(sso, sig, Options).Should().BeNull();
    }

    [Fact]
    public void Signs_the_response_with_the_nonce_and_fields()
    {
        var request = new DiscourseConnect.Request("abc", "https://community.test/session/sso_login");
        var url = DiscourseConnect.BuildReturnUrl(request,
            [new("external_id", "u1"), new("username", "たなか"), new("email", "a+b@test.dev")], Options);

        var query = QueryHelpers.ParseQuery(new Uri(url).Query);
        var sso = query["sso"].ToString();
        query["sig"].ToString().Should().Be(DiscourseConnect.Sign(sso, "secret"));

        var fields = QueryHelpers.ParseQuery(Encoding.UTF8.GetString(Convert.FromBase64String(sso)));
        fields["nonce"].ToString().Should().Be("abc");
        fields["external_id"].ToString().Should().Be("u1");
        fields["username"].ToString().Should().Be("たなか");
        fields["email"].ToString().Should().Be("a+b@test.dev");
    }
}
