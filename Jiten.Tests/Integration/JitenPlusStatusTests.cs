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

public class JitenPlusStatusTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        userDb.UserPromoCredits.RemoveRange(userDb.UserPromoCredits);
        userDb.PromoCodes.RemoveRange(userDb.PromoCodes);

        // Clear billing state on the shared test users so tests don't bleed into each other.
        foreach (var user in await userDb.Users.ToListAsync())
        {
            user.StripeSubscriptionActive = false;
            user.SubscriptionPeriodEnd = null;
            user.SubscriptionPlan = null;
            user.IsLifetime = false;
            user.LifetimeSource = null;
            user.AdminPremiumOverride = false;
        }
        await userDb.SaveChangesAsync();

        // The tier service caches per user for 60s; drop it so each test sees fresh state.
        var jitenPlus = scope.ServiceProvider.GetRequiredService<IJitenPlusService>();
        foreach (var id in new[] { TestUsers.UserA, TestUsers.UserB, TestUsers.Admin })
            jitenPlus.InvalidateTier(id);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<JsonElement> GetStatus(string userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/jiten-plus/status").WithUser(userId);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task SetUser(string userId, Action<Jiten.Core.Data.Authentication.User> mutate)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var user = await userDb.Users.FirstAsync(u => u.Id == userId);
        mutate(user);
        await userDb.SaveChangesAsync();

        scope.ServiceProvider.GetRequiredService<IJitenPlusService>().InvalidateTier(userId);
    }

    private async Task AddCredit(string userId, int remainingDays, bool grantsFull, string? thankYou = null)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var code = new PromoCode { Code = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(), DurationDays = remainingDays, GrantsFullTier = grantsFull };
        userDb.PromoCodes.Add(code);
        await userDb.SaveChangesAsync();

        userDb.UserPromoCredits.Add(new UserPromoCredit
        {
            UserId = userId,
            PromoCodeId = code.CodeId,
            GrantsFullTier = grantsFull,
            RemainingDays = remainingDays,
            GrantedAt = DateTime.UtcNow,
            ThankYouMessage = thankYou
        });
        await userDb.SaveChangesAsync();

        scope.ServiceProvider.GetRequiredService<IJitenPlusService>().InvalidateTier(userId);
    }

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        var response = await _client.GetAsync("/api/jiten-plus/status");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Pricing_IsAnonymous_AndReportsLifetimeWindow()
    {
        // No auth headers: the endpoint is [AllowAnonymous] so the marketing page renders logged-out.
        var response = await _client.GetAsync("/api/jiten-plus/pricing");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The test factory sets Stripe__LifetimeWindowEnd=2999-01-01, so the window is open.
        body.GetProperty("lifetimeAvailable").GetBoolean().Should().BeTrue();
        body.GetProperty("lifetimeWindowEnd").GetDateTime().Year.Should().Be(2999);
    }

    [Fact]
    public async Task FreeUser_IsNone_WithQuotaShape()
    {
        var body = await GetStatus(TestUsers.UserB);

        body.GetProperty("tier").GetString().Should().Be("none");
        body.GetProperty("sources").GetProperty("subscriptionActive").GetBoolean().Should().BeFalse();
        body.GetProperty("sources").GetProperty("promoCreditDays").GetInt32().Should().Be(0);
        body.GetProperty("quota").GetProperty("usedBytes").GetInt64().Should().Be(0);
        body.GetProperty("quota").GetProperty("maxBytes").GetInt64().Should().Be(JitenPlusConstants.StorageQuotaBytes);
    }

    [Fact]
    public async Task AdminOverride_IsFull()
    {
        await SetUser(TestUsers.UserA, u => u.AdminPremiumOverride = true);

        var body = await GetStatus(TestUsers.UserA);

        body.GetProperty("tier").GetString().Should().Be("full");
        body.GetProperty("sources").GetProperty("adminOverride").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ActiveSubscription_IsFull_WithPlan()
    {
        await SetUser(TestUsers.UserA, u =>
        {
            u.StripeSubscriptionActive = true;
            u.SubscriptionPlan = SubscriptionPlan.Yearly;
            u.SubscriptionPeriodEnd = DateTime.UtcNow.AddDays(300);
        });

        var body = await GetStatus(TestUsers.UserA);

        body.GetProperty("tier").GetString().Should().Be("full");
        body.GetProperty("sources").GetProperty("subscriptionActive").GetBoolean().Should().BeTrue();
        body.GetProperty("sources").GetProperty("plan").GetString().Should().Be("Yearly");
    }

    [Fact]
    public async Task TrialCredit_IsTrial_WithCreditListed()
    {
        await AddCredit(TestUsers.UserA, remainingDays: 6, grantsFull: false, thankYou: "Thanks!");

        var body = await GetStatus(TestUsers.UserA);

        body.GetProperty("tier").GetString().Should().Be("trial");
        var sources = body.GetProperty("sources");
        sources.GetProperty("promoCreditDays").GetInt32().Should().Be(6);
        var credits = sources.GetProperty("credits");
        credits.GetArrayLength().Should().Be(1);
        credits[0].GetProperty("remainingDays").GetInt32().Should().Be(6);
        credits[0].GetProperty("grantsFullTier").GetBoolean().Should().BeFalse();
        credits[0].GetProperty("thankYouMessage").GetString().Should().Be("Thanks!");
    }

    [Fact]
    public async Task FullGrantingCredit_IsFull()
    {
        await AddCredit(TestUsers.UserA, remainingDays: 30, grantsFull: true);

        var body = await GetStatus(TestUsers.UserA);

        body.GetProperty("tier").GetString().Should().Be("full");
        body.GetProperty("sources").GetProperty("promoCreditDays").GetInt32().Should().Be(30);
    }
}
