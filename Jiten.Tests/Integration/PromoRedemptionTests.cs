using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.Billing;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class PromoRedemptionTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserPromoCredits.RemoveRange(userDb.UserPromoCredits);
        userDb.PromoCodes.RemoveRange(userDb.PromoCodes);
        foreach (var user in await userDb.Users.ToListAsync())
        {
            user.StripeSubscriptionActive = false;
            user.IsLifetime = false;
            user.LifetimeSource = null;
            user.AdminPremiumOverride = false;
        }
        await userDb.SaveChangesAsync();

        var jitenPlus = scope.ServiceProvider.GetRequiredService<IJitenPlusService>();
        foreach (var id in new[] { TestUsers.UserA, TestUsers.UserB, TestUsers.Admin })
            jitenPlus.InvalidateTier(id);

        factory.Emails.Clear();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<PromoCode> SeedCode(string code, int days = 7, bool grantsFull = false,
        bool isActive = true, int? maxUses = null, DateTime? expiresAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var promo = new PromoCode
        {
            Code = code, DurationDays = days, GrantsFullTier = grantsFull,
            IsActive = isActive, MaxUses = maxUses, ExpiresAt = expiresAt, CreatedAt = DateTime.UtcNow
        };
        userDb.PromoCodes.Add(promo);
        await userDb.SaveChangesAsync();
        return promo;
    }

    private Task<HttpResponseMessage> Redeem(string userId, string code) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/jiten-plus/redeem")
            .WithUser(userId).WithJsonContent(new { code }));

    private async Task<JsonElement> GetStatus(string userId)
    {
        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/jiten-plus/status").WithUser(userId));
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Redeem_TrialCode_FlipsTierToTrial()
    {
        await SeedCode("TRIALSEVEN", days: 7, grantsFull: false);

        var response = await Redeem(TestUsers.UserA, "TRIALSEVEN");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("tier").GetString().Should().Be("trial");
        body.GetProperty("days").GetInt32().Should().Be(7);

        var status = await GetStatus(TestUsers.UserA);
        status.GetProperty("tier").GetString().Should().Be("trial");
        status.GetProperty("sources").GetProperty("promoCreditDays").GetInt32().Should().Be(7);

        factory.Emails.Sent.Should().Contain(e => e.Method == "SendPromoRedeemedAsync");
    }

    [Fact]
    public async Task Redeem_FullCode_FlipsTierToFull()
    {
        await SeedCode("FULLTHIRTY", days: 30, grantsFull: true);

        var response = await Redeem(TestUsers.UserA, "FULLTHIRTY");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("tier").GetString().Should().Be("full");
        body.GetProperty("grantsFullTier").GetBoolean().Should().BeTrue();

        var status = await GetStatus(TestUsers.UserA);
        status.GetProperty("tier").GetString().Should().Be("full");
    }

    [Fact]
    public async Task Redeem_LowercaseInput_Matches()
    {
        await SeedCode("MIXEDCASE", days: 5);
        var response = await Redeem(TestUsers.UserA, "mixedcase");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Redeem_ExpiredCode_Fails()
    {
        await SeedCode("EXPIREDONE", expiresAt: DateTime.UtcNow.AddDays(-1));
        var response = await Redeem(TestUsers.UserA, "EXPIREDONE");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Redeem_InactiveCode_Fails()
    {
        await SeedCode("INACTIVEONE", isActive: false);
        var response = await Redeem(TestUsers.UserA, "INACTIVEONE");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Redeem_UnknownCode_Fails()
    {
        var response = await Redeem(TestUsers.UserA, "NOSUCHCODE");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Redeem_ExhaustedCode_Fails()
    {
        await SeedCode("ONEUSEONLY", maxUses: 1);

        var first = await Redeem(TestUsers.UserA, "ONEUSEONLY");
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await Redeem(TestUsers.UserB, "ONEUSEONLY");
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Redeem_Twice_SameUser_Fails()
    {
        await SeedCode("DUPECODEXX", days: 7);

        var first = await Redeem(TestUsers.UserA, "DUPECODEXX");
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await Redeem(TestUsers.UserA, "DUPECODEXX");
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Uses aren't leaked by the rejected second attempt.
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var promo = await userDb.PromoCodes.FirstAsync(p => p.Code == "DUPECODEXX");
        promo.CurrentUses.Should().Be(1);
    }
}
