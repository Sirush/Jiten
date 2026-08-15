using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.Authentication;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class RefreshTokenTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.RefreshTokens.RemoveRange(userDb.RefreshTokens);

        // The shared fixture seeds users without an email; the access token claims one.
        foreach (var id in new[] { TestUsers.UserA, TestUsers.UserB })
        {
            var user = await userDb.Users.SingleAsync(u => u.Id == id);
            user.Email ??= $"{id}@example.test";
        }
        await userDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<TokenResponse> IssueAsync(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var user = await userManager.FindByIdAsync(userId);
        var tokens = await tokenService.GenerateTokens(user!);
        await userDb.SaveChangesAsync();
        return tokens;
    }

    private Task<HttpResponseMessage> RefreshAsync(string? accessToken, string refreshToken) =>
        _client.PostAsJsonAsync("/api/auth/refresh", new { accessToken, refreshToken });

    [Fact]
    public async Task Refresh_RotatesThePair()
    {
        var issued = await IssueAsync(TestUsers.UserA);

        var response = await RefreshAsync(issued.AccessToken, issued.RefreshToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var refreshed = await response.Content.ReadFromJsonAsync<TokenResponse>();
        refreshed!.RefreshToken.Should().NotBe(issued.RefreshToken);
    }

    /// A browser writes the two auth cookies as separate operations, so tabs racing each other leave a
    /// pair from two different rotations. Both halves are the user's own and neither is spent.
    [Fact]
    public async Task Refresh_AcceptsAPairFromTwoDifferentRotations()
    {
        var first = await IssueAsync(TestUsers.UserA);
        var second = await IssueAsync(TestUsers.UserA);

        var response = await RefreshAsync(first.AccessToken, second.RefreshToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_RejectsAnotherUsersAccessToken()
    {
        var victim = await IssueAsync(TestUsers.UserA);
        var attacker = await IssueAsync(TestUsers.UserB);

        var response = await RefreshAsync(attacker.AccessToken, victim.RefreshToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Refresh_ToleratesReuseWithinTheGraceWindow()
    {
        var issued = await IssueAsync(TestUsers.UserA);

        (await RefreshAsync(issued.AccessToken, issued.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        var retry = await RefreshAsync(issued.AccessToken, issued.RefreshToken);

        retry.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_RejectsReuseOutsideTheGraceWindow()
    {
        var issued = await IssueAsync(TestUsers.UserA);
        (await RefreshAsync(issued.AccessToken, issued.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var spent = await userDb.RefreshTokens.SingleAsync(rt => rt.Token == issued.RefreshToken);
            spent.UsedAt = DateTime.UtcNow.AddMinutes(-5);
            await userDb.SaveChangesAsync();
        }

        var replay = await RefreshAsync(issued.AccessToken, issued.RefreshToken);

        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Refresh_RejectsARevokedToken()
    {
        var issued = await IssueAsync(TestUsers.UserA);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var stored = await userDb.RefreshTokens.SingleAsync(rt => rt.Token == issued.RefreshToken);
            stored.IsRevoked = true;
            await userDb.SaveChangesAsync();
        }

        var response = await RefreshAsync(issued.AccessToken, issued.RefreshToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Refresh_RejectsAnUnknownToken()
    {
        var issued = await IssueAsync(TestUsers.UserA);

        var response = await RefreshAsync(issued.AccessToken, "not-a-refresh-token");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
