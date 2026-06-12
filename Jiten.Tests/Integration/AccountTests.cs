using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data.Authentication;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class AccountTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();
    private const string DefaultPassword = "Password123!";

    public async Task InitializeAsync()
    {
        factory.Emails.Clear();
        await CleanupUsersAndTokensAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // Remove every user except the three pre-seeded fixtures, plus all refresh tokens, so each test starts clean.
    private async Task CleanupUsersAndTokensAsync()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.RefreshTokens.RemoveRange(userDb.RefreshTokens);
        var seeded = new[] { TestUsers.UserA, TestUsers.UserB, TestUsers.Admin };
        var extras = await userDb.Users.Where(u => !seeded.Contains(u.Id)).ToListAsync();
        userDb.Users.RemoveRange(extras);
        await userDb.SaveChangesAsync();
    }

    // Creates a confirmed user. password == null produces a Google-style password-less account.
    private async Task<User> CreateUserAsync(string userName, string email, string? password)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        var user = new User
        {
            UserName = userName,
            Email = email,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            TosAcceptedAt = DateTime.UtcNow
        };

        var result = password == null
            ? await userManager.CreateAsync(user)
            : await userManager.CreateAsync(user, password);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Description)));
        return user;
    }

    private async Task<User?> GetUserAsync(string id)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
    }

    private record TokenPair(string AccessToken, string RefreshToken);

    private async Task<TokenPair> LoginAsync(string usernameOrEmail, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            .WithJsonContent(new { usernameOrEmail, password });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new TokenPair(body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!);
    }

    private async Task<HttpResponseMessage> RefreshAsync(string accessToken, string refreshToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
            .WithJsonContent(new { accessToken, refreshToken });
        return await _client.SendAsync(request);
    }

    private static HttpRequestMessage WithBearer(HttpRequestMessage request, string accessToken)
    {
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    // ---- GET /api/account ----

    [Fact]
    public async Task GetAccount_PasswordUser_ReturnsHasPasswordTrue()
    {
        var user = await CreateUserAsync("acc_pw", "acc_pw@test.dev", DefaultPassword);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/account").WithUser(user.Id);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("hasPassword").GetBoolean().Should().BeTrue();
        body.GetProperty("email").GetString().Should().Be("acc_pw@test.dev");
        body.GetProperty("userName").GetString().Should().Be("acc_pw");
    }

    [Fact]
    public async Task GetAccount_PasswordlessUser_ReturnsHasPasswordFalse()
    {
        var user = await CreateUserAsync("acc_nopw", "acc_nopw@test.dev", null);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/account").WithUser(user.Id);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("hasPassword").GetBoolean().Should().BeFalse();
        body.GetProperty("email").GetString().Should().Be("acc_nopw@test.dev");
        body.GetProperty("userName").GetString().Should().Be("acc_nopw");
    }

    // ---- change-password ----

    [Fact]
    public async Task ChangePassword_WrongCurrent_Returns400_AndDoesNotIncrementLockout()
    {
        var user = await CreateUserAsync("cp_wrong", "cp_wrong@test.dev", DefaultPassword);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/change-password")
            .WithUser(user.Id)
            .WithJsonContent(new { currentPassword = "WrongPassword123!", newPassword = "BrandNewPass123!" });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var refreshed = await GetUserAsync(user.Id);
        refreshed!.AccessFailedCount.Should().Be(0, "change-password is not a login and must not bump lockout");
    }

    [Fact]
    public async Task ChangePassword_Success_RotatesTokens_OldRefreshRejected_NewValid()
    {
        var user = await CreateUserAsync("cp_ok", "cp_ok@test.dev", DefaultPassword);
        var session = await LoginAsync("cp_ok", DefaultPassword);

        var request = WithBearer(new HttpRequestMessage(HttpMethod.Post, "/api/account/change-password"), session.AccessToken)
            .WithJsonContent(new { currentPassword = DefaultPassword, newPassword = "BrandNewPass123!" });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var newAccess = body.GetProperty("accessToken").GetString()!;
        var newRefresh = body.GetProperty("refreshToken").GetString()!;
        newAccess.Should().NotBeNullOrEmpty();
        newRefresh.Should().NotBeNullOrEmpty();

        // Old refresh token (issued at login) was revoked.
        var oldRefreshResult = await RefreshAsync(session.AccessToken, session.RefreshToken);
        oldRefreshResult.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // New refresh token from the change-password response still works.
        var newRefreshResult = await RefreshAsync(newAccess, newRefresh);
        newRefreshResult.StatusCode.Should().Be(HttpStatusCode.OK, await newRefreshResult.Content.ReadAsStringAsync());

        // A password-changed notice was emailed.
        factory.Emails.Sent.Should().Contain(e =>
            e.Method == nameof(RecordingEmailService.SendPasswordChangedNoticeAsync) && e.Recipient == "cp_ok@test.dev");
    }

    // ---- set-password ----

    [Fact]
    public async Task SetPassword_UserWithPassword_Returns400()
    {
        var user = await CreateUserAsync("sp_has", "sp_has@test.dev", DefaultPassword);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/set-password")
            .WithUser(user.Id)
            .WithJsonContent(new { newPassword = "BrandNewPass123!" });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetPassword_PasswordlessUser_Succeeds_ThenLoginWorks()
    {
        var user = await CreateUserAsync("sp_none", "sp_none@test.dev", null);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/set-password")
            .WithUser(user.Id)
            .WithJsonContent(new { newPassword = "FreshPass123!" });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        factory.Emails.Sent.Should().Contain(e =>
            e.Method == nameof(RecordingEmailService.SendPasswordSetNoticeAsync) && e.Recipient == "sp_none@test.dev");

        var login = await LoginAsync("sp_none@test.dev", "FreshPass123!");
        login.AccessToken.Should().NotBeNullOrEmpty();
    }

    // ---- change-email ----

    [Fact]
    public async Task ChangeEmail_TakenEmail_Returns400()
    {
        await CreateUserAsync("ce_other", "ce_taken@test.dev", DefaultPassword);
        var user = await CreateUserAsync("ce_taker", "ce_taker@test.dev", DefaultPassword);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/change-email")
            .WithUser(user.Id)
            .WithJsonContent(new { newEmail = "ce_taken@test.dev", currentPassword = DefaultPassword });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ChangeEmail_PasswordlessUser_Returns400()
    {
        var user = await CreateUserAsync("ce_nopw", "ce_nopw@test.dev", null);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/change-email")
            .WithUser(user.Id)
            .WithJsonContent(new { newEmail = "ce_nopw_new@test.dev" });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ChangeEmail_WrongCurrentPassword_Returns400()
    {
        var user = await CreateUserAsync("ce_wrongpw", "ce_wrongpw@test.dev", DefaultPassword);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/change-email")
            .WithUser(user.Id)
            .WithJsonContent(new { newEmail = "ce_wrongpw_new@test.dev", currentPassword = "Nope123456!" });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ChangeEmail_HappyPath_EmailUnchangedUntilConfirmed_EmailsCaptured_ConfirmChangesEmail()
    {
        var user = await CreateUserAsync("ce_ok", "ce_ok_old@test.dev", DefaultPassword);
        const string newEmail = "ce_ok_new@test.dev";

        // Request the change.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/change-email")
            .WithUser(user.Id)
            .WithJsonContent(new { newEmail, currentPassword = DefaultPassword });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        // Email is unchanged until confirmed.
        (await GetUserAsync(user.Id))!.Email.Should().Be("ce_ok_old@test.dev");

        // Confirmation to NEW address (with token) and notice to OLD address captured.
        var confirmation = factory.Emails.Sent.SingleOrDefault(e =>
            e.Method == nameof(RecordingEmailService.SendChangeEmailConfirmationAsync) && e.Recipient == newEmail);
        confirmation.Should().NotBeNull("a confirmation must be sent to the new address");
        confirmation!.Code.Should().NotBeNullOrEmpty();
        factory.Emails.Sent.Should().Contain(e =>
            e.Method == nameof(RecordingEmailService.SendEmailChangeNoticeAsync) && e.Recipient == "ce_ok_old@test.dev");

        // Issue a session so we can verify confirm revokes refresh tokens.
        var session = await LoginAsync("ce_ok", DefaultPassword);

        // Confirm with the captured real token.
        var confirm = new HttpRequestMessage(HttpMethod.Post, "/api/account/confirm-email-change")
            .WithJsonContent(new { userId = user.Id, newEmail, code = confirmation.Code });
        var confirmResponse = await _client.SendAsync(confirm);
        confirmResponse.StatusCode.Should().Be(HttpStatusCode.OK, await confirmResponse.Content.ReadAsStringAsync());

        (await GetUserAsync(user.Id))!.Email.Should().Be(newEmail);

        factory.Emails.ForRecipient("ce_ok_old@test.dev")
               .Count(e => e.Method == nameof(RecordingEmailService.SendEmailChangeNoticeAsync))
               .Should().Be(1, "the old address must not receive the request notice twice");
        factory.Emails.Sent.Should().Contain(e =>
            e.Method == nameof(RecordingEmailService.SendEmailChangedAwayNoticeAsync) && e.Recipient == "ce_ok_old@test.dev");

        // Refresh tokens were revoked on confirm.
        var refreshResult = await RefreshAsync(session.AccessToken, session.RefreshToken);
        refreshResult.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ChangeEmail_SecondRequestWithinCooldown_Returns429_NoSecondEmail()
    {
        var user = await CreateUserAsync("ce_cd", "ce_cd_old@test.dev", DefaultPassword);

        var first = new HttpRequestMessage(HttpMethod.Post, "/api/account/change-email")
            .WithUser(user.Id)
            .WithJsonContent(new { newEmail = "ce_cd_new@test.dev", currentPassword = DefaultPassword });
        (await _client.SendAsync(first)).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = new HttpRequestMessage(HttpMethod.Post, "/api/account/change-email")
            .WithUser(user.Id)
            .WithJsonContent(new { newEmail = "ce_cd_new2@test.dev", currentPassword = DefaultPassword });
        var secondResponse = await _client.SendAsync(second);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        factory.Emails.Sent
               .Count(e => e.Method == nameof(RecordingEmailService.SendChangeEmailConfirmationAsync))
               .Should().Be(1, "the throttled second request must not send another confirmation email");
    }

    [Fact]
    public async Task ConfirmEmailChange_GarbageCode_Returns400_EmailUnchanged()
    {
        var user = await CreateUserAsync("ce_bad", "ce_bad_old@test.dev", DefaultPassword);

        var confirm = new HttpRequestMessage(HttpMethod.Post, "/api/account/confirm-email-change")
            .WithJsonContent(new { userId = user.Id, newEmail = "ce_bad_new@test.dev", code = "bm90LWEtcmVhbC10b2tlbg" });
        var response = await _client.SendAsync(confirm);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await GetUserAsync(user.Id))!.Email.Should().Be("ce_bad_old@test.dev");
    }

    // ---- resend-confirmation ----

    [Fact]
    public async Task ResendConfirmation_UnknownEmail_Returns200_NoEmailSent()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/resend-confirmation")
            .WithJsonContent(new { email = "nobody@test.dev", recaptchaResponse = "test" });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        factory.Emails.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task ResendConfirmation_AlreadyConfirmed_Returns200_NoEmailSent()
    {
        await CreateUserAsync("rc_confirmed", "rc_confirmed@test.dev", DefaultPassword); // EmailConfirmed = true

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/resend-confirmation")
            .WithJsonContent(new { email = "rc_confirmed@test.dev", recaptchaResponse = "test" });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        factory.Emails.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task ResendConfirmation_UnconfirmedUser_Returns200_ConfirmationSent()
    {
        // Build an explicitly unconfirmed user.
        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = new User
            {
                UserName = "rc_unconfirmed",
                Email = "rc_unconfirmed@test.dev",
                EmailConfirmed = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                TosAcceptedAt = DateTime.UtcNow
            };
            var result = await userManager.CreateAsync(user, DefaultPassword);
            result.Succeeded.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/resend-confirmation")
            .WithJsonContent(new { email = "rc_unconfirmed@test.dev", recaptchaResponse = "test" });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        factory.Emails.Sent.Should().Contain(e =>
            e.Method == nameof(RecordingEmailService.SendEmailConfirmationAsync) && e.Recipient == "rc_unconfirmed@test.dev");
    }

    // ---- preferences ----

    [Fact]
    public async Task UpdatePreferences_RoundTrip()
    {
        var user = await CreateUserAsync("pref_user", "pref_user@test.dev", DefaultPassword);

        async Task<bool> GetNewsletter()
        {
            var get = new HttpRequestMessage(HttpMethod.Get, "/api/account").WithUser(user.Id);
            var resp = await _client.SendAsync(get);
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            return body.GetProperty("receivesNewsletter").GetBoolean();
        }

        async Task SetNewsletter(bool value)
        {
            var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/account/preferences")
                .WithUser(user.Id)
                .WithJsonContent(new { receivesNewsletter = value });
            var resp = await _client.SendAsync(patch);
            resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        }

        await SetNewsletter(true);
        (await GetNewsletter()).Should().BeTrue();

        await SetNewsletter(false);
        (await GetNewsletter()).Should().BeFalse();
    }

    // ---- revoke-token keepCurrent ----

    [Fact]
    public async Task RevokeToken_KeepCurrent_SparesCurrentSession_RevokesOthers()
    {
        var user = await CreateUserAsync("rt_keep", "rt_keep@test.dev", DefaultPassword);
        var sessionA = await LoginAsync("rt_keep", DefaultPassword);
        var sessionB = await LoginAsync("rt_keep", DefaultPassword);

        // Revoke with keepCurrent using session A's JWT (its jti is spared).
        var revoke = WithBearer(new HttpRequestMessage(HttpMethod.Post, "/api/auth/revoke-token"), sessionA.AccessToken)
            .WithJsonContent(new { keepCurrent = true });
        var revokeResponse = await _client.SendAsync(revoke);
        revokeResponse.StatusCode.Should().Be(HttpStatusCode.OK, await revokeResponse.Content.ReadAsStringAsync());

        // Session A's refresh token still redeemable.
        var aRefresh = await RefreshAsync(sessionA.AccessToken, sessionA.RefreshToken);
        aRefresh.StatusCode.Should().Be(HttpStatusCode.OK, await aRefresh.Content.ReadAsStringAsync());

        // Session B's refresh token revoked.
        var bRefresh = await RefreshAsync(sessionB.AccessToken, sessionB.RefreshToken);
        bRefresh.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RevokeToken_WithoutKeepCurrent_RevokesAll()
    {
        var user = await CreateUserAsync("rt_all", "rt_all@test.dev", DefaultPassword);
        var sessionA = await LoginAsync("rt_all", DefaultPassword);
        var sessionB = await LoginAsync("rt_all", DefaultPassword);

        var revoke = WithBearer(new HttpRequestMessage(HttpMethod.Post, "/api/auth/revoke-token"), sessionA.AccessToken);
        var revokeResponse = await _client.SendAsync(revoke);
        revokeResponse.StatusCode.Should().Be(HttpStatusCode.OK, await revokeResponse.Content.ReadAsStringAsync());

        (await RefreshAsync(sessionA.AccessToken, sessionA.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RefreshAsync(sessionB.AccessToken, sessionB.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
