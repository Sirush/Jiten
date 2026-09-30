using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Controllers;
using Jiten.Api.Helpers;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.Authentication;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class CommunityTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var seeded = new[] { TestUsers.UserA, TestUsers.UserB, TestUsers.Admin };
        userDb.Users.RemoveRange(await userDb.Users.Where(u => !seeded.Contains(u.Id)).ToListAsync());
        await userDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<User> CreateUserAsync(string? displayName = null, bool emailConfirmed = true, DateTime? changedAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var id = Guid.NewGuid().ToString();
        var user = new User
        {
            Id = id, UserName = $"u{id[..8]}", NormalizedUserName = $"U{id[..8]}".ToUpperInvariant(), Email = $"{id[..8]}@test.dev",
            EmailConfirmed = emailConfirmed, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, DisplayName = displayName,
            NormalizedDisplayName = displayName == null ? null : DisplayNameValidator.ToKey(displayName),
            DisplayNameChangedAt = displayName == null ? null : changedAt ?? DateTime.UtcNow
        };
        userDb.Users.Add(user);
        await userDb.SaveChangesAsync();
        return user;
    }

    private async Task<User> GetUserAsync(string id)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.Users.AsNoTracking().FirstAsync(u => u.Id == id);
    }

    private static object SignedPayload(string nonce = "nonce123",
                                        string returnUrl = JitenWebApplicationFactory.DiscourseUrl + "/session/sso_login")
    {
        var query = $"nonce={nonce}&return_sso_url={Uri.EscapeDataString(returnUrl)}";
        var sso = Convert.ToBase64String(Encoding.UTF8.GetBytes(query));
        return new { sso, sig = DiscourseConnect.Sign(sso, JitenWebApplicationFactory.DiscourseSsoSecret) };
    }

    private Task<HttpResponseMessage> PutDisplayNameAsync(string userId, string displayName, bool admin = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/account/display-name").WithUser(userId);
        if (admin) request.Headers.Add("X-Test-Role", "Administrator");
        return _client.SendAsync(request.WithJsonContent(new { displayName }));
    }

    private static Dictionary<string, string> ReadReturnedFields(string returnUrl)
    {
        var query = QueryHelpers.ParseQuery(new Uri(returnUrl).Query);
        var sso = query["sso"].ToString();
        query["sig"].ToString().Should().Be(DiscourseConnect.Sign(sso, JitenWebApplicationFactory.DiscourseSsoSecret));
        return QueryHelpers.ParseQuery(Encoding.UTF8.GetString(Convert.FromBase64String(sso)))
                           .ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
    }

    [Fact]
    public async Task SetDisplayName_FirstTime_SavesNormalisedName()
    {
        var user = await CreateUserAsync();

        var response = await PutDisplayNameAsync(user.Id, " Ｔａｎａｋａ ");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var saved = await GetUserAsync(user.Id);
        saved.DisplayName.Should().Be("Tanaka");
        saved.NormalizedDisplayName.Should().Be("TANAKA");
    }

    [Fact]
    public async Task SetDisplayName_ReservedWord_Rejected()
    {
        var user = await CreateUserAsync();

        var response = await PutDisplayNameAsync(user.Id, "jiten.moe");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetUserAsync(user.Id)).DisplayName.Should().BeNull();
    }

    [Fact]
    public async Task SetDisplayName_ReservedWord_AllowedForAdmin()
    {
        var response = await PutDisplayNameAsync(TestUsers.Admin, "Jiten", admin: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SetDisplayName_TakenIgnoringCase_Conflict()
    {
        await CreateUserAsync("Tanaka");
        var user = await CreateUserAsync();

        var response = await PutDisplayNameAsync(user.Id, "tanaka");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SetDisplayName_WithinCooldown_Rejected()
    {
        var user = await CreateUserAsync("Tanaka", changedAt: DateTime.UtcNow.AddDays(-5));

        var response = await PutDisplayNameAsync(user.Id, "Suzuki");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetUserAsync(user.Id)).DisplayName.Should().Be("Tanaka");
    }

    [Fact]
    public async Task SetDisplayName_AfterCooldown_Changes()
    {
        var user = await CreateUserAsync("Tanaka", changedAt: DateTime.UtcNow - AccountController.DisplayNameChangeCooldown - TimeSpan.FromMinutes(1));

        var response = await PutDisplayNameAsync(user.Id, "Suzuki");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await GetUserAsync(user.Id)).DisplayName.Should().Be("Suzuki");
    }

    [Fact]
    public async Task GetAccount_ReportsDisplayNameAndNextChange()
    {
        var changedAt = DateTime.UtcNow.AddDays(-5);
        var user = await CreateUserAsync("Tanaka", changedAt: changedAt);

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/account").WithUser(user.Id));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("displayName").GetString().Should().Be("Tanaka");
        body.GetProperty("displayNameChangeAvailableAt").GetDateTime()
            .Should().BeCloseTo(changedAt + AccountController.DisplayNameChangeCooldown, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Sso_ValidPayload_ReturnsSignedUserFields()
    {
        var user = await CreateUserAsync("たなか");

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/community/sso")
                                               .WithUser(user.Id).WithJsonContent(SignedPayload()));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var returnUrl = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("returnUrl").GetString()!;
        returnUrl.Should().StartWith(JitenWebApplicationFactory.DiscourseUrl + "/session/sso_login?");

        var fields = ReadReturnedFields(returnUrl);
        fields["nonce"].Should().Be("nonce123");
        fields["external_id"].Should().Be(user.Id);
        fields["email"].Should().Be(user.Email);
        fields["username"].Should().Be("たなか");
        fields["remove_groups"].Should().Be("jiten-plus");
        fields.Should().NotContainKey("admin");
        fields.Should().NotContainKey("require_activation");
    }

    [Fact]
    public async Task Sso_UnconfirmedEmail_RequiresActivation()
    {
        var user = await CreateUserAsync("Tanaka", emailConfirmed: false);

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/community/sso")
                                               .WithUser(user.Id).WithJsonContent(SignedPayload()));

        var returnUrl = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("returnUrl").GetString()!;
        ReadReturnedFields(returnUrl)["require_activation"].Should().Be("true");
    }

    [Fact]
    public async Task Sso_Admin_SendsAdminFlag()
    {
        await PutDisplayNameAsync(TestUsers.Admin, "Jiten", admin: true);

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/community/sso")
                                               .WithAdmin().WithJsonContent(SignedPayload()));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var returnUrl = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("returnUrl").GetString()!;
        ReadReturnedFields(returnUrl)["admin"].Should().Be("true");
    }

    [Fact]
    public async Task Sso_WithoutDisplayName_AsksForOne()
    {
        var user = await CreateUserAsync();

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/community/sso")
                                               .WithUser(user.Id).WithJsonContent(SignedPayload()));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()
            .Should().Be(CommunityController.DisplayNameRequiredCode);
    }

    [Fact]
    public async Task Sso_TamperedSignature_BadRequest()
    {
        var user = await CreateUserAsync("Tanaka");
        var sso = Convert.ToBase64String(Encoding.UTF8.GetBytes("nonce=n&return_sso_url=https%3A%2F%2Fcommunity.test%2Fsso"));

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/community/sso")
                                               .WithUser(user.Id).WithJsonContent(new { sso, sig = DiscourseConnect.Sign(sso, "wrong") }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sso_ForeignReturnUrl_BadRequest()
    {
        var user = await CreateUserAsync("Tanaka");

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/community/sso")
                                               .WithUser(user.Id).WithJsonContent(SignedPayload(returnUrl: "https://evil.test/sso")));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sso_Anonymous_Unauthorized()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/community/sso").WithJsonContent(SignedPayload()));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
