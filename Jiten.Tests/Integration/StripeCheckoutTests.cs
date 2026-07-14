using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Api.Services.Stripe;
using Jiten.Core;
using Jiten.Core.Data.Billing;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class StripeCheckoutTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        factory.Stripe.Reset();
        factory.Emails.Clear();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        foreach (var user in await userDb.Users.ToListAsync())
        {
            user.StripeCustomerId = null;
            user.StripeSubscriptionActive = false;
            user.StripeSubscriptionId = null;
            user.SubscriptionPeriodEnd = null;
            user.SubscriptionPlan = null;
            user.IsLifetime = false;
            user.LifetimeSource = null;
            user.AdminPremiumOverride = false;
        }
        await userDb.SaveChangesAsync();

        var jitenPlus = scope.ServiceProvider.GetRequiredService<IJitenPlusService>();
        foreach (var id in new[] { TestUsers.UserA, TestUsers.UserB, TestUsers.Admin })
            jitenPlus.InvalidateTier(id);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpResponseMessage> Checkout(string userId, string plan)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/stripe/checkout")
            .WithUser(userId)
            .WithJsonContent(new { plan });
        return await _client.SendAsync(request);
    }

    private async Task SetUser(string userId, Action<Jiten.Core.Data.Authentication.User> mutate)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var user = await userDb.Users.FirstAsync(u => u.Id == userId);
        mutate(user);
        await userDb.SaveChangesAsync();
    }

    private async Task<Jiten.Core.Data.Authentication.User> GetUser(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
    }

    [Fact]
    public async Task Checkout_Unauthenticated_Returns401()
    {
        var response = await _client.PostAsync("/api/stripe/checkout",
            JsonContent.Create(new { plan = "yearly" }));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Checkout_Yearly_ReturnsUrlAndPersistsCustomer()
    {
        var response = await Checkout(TestUsers.UserA, "yearly");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("url").GetString().Should().Be(factory.Stripe.NextCheckoutUrl);

        (await GetUser(TestUsers.UserA)).StripeCustomerId.Should().Be(factory.Stripe.NextCustomerId);

        var recorded = factory.Stripe.CheckoutRequests.Should().ContainSingle().Subject;
        recorded.Mode.Should().Be(StripeCheckoutMode.Subscription);
        recorded.PriceId.Should().Be("price_yearly");
        recorded.CouponId.Should().BeNull();
    }

    [Fact]
    public async Task Checkout_Lifetime_UsesPaymentMode()
    {
        var response = await Checkout(TestUsers.UserA, "lifetime");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var recorded = factory.Stripe.CheckoutRequests.Should().ContainSingle().Subject;
        recorded.Mode.Should().Be(StripeCheckoutMode.Payment);
        recorded.PriceId.Should().Be("price_lifetime");
    }

    [Fact]
    public async Task Checkout_LifetimeWithActiveSubscription_AttachesUpgradeCredit()
    {
        await SetUser(TestUsers.UserA, u =>
        {
            u.StripeCustomerId = "cus_existing";
            u.StripeSubscriptionActive = true;
            u.StripeSubscriptionId = "sub_x";
            u.SubscriptionPlan = SubscriptionPlan.Yearly;
            u.SubscriptionPeriodEnd = DateTime.UtcNow.AddDays(182.5);
        });

        var response = await Checkout(TestUsers.UserA, "lifetime");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        factory.Stripe.Coupons.Should().ContainSingle()
               .Which.AmountCents.Should().Be(2500);
        factory.Stripe.CheckoutRequests.Should().ContainSingle()
               .Which.CouponId.Should().Be(factory.Stripe.NextCouponId);
    }

    [Fact]
    public async Task Checkout_UnknownPlan_Returns400()
    {
        var response = await Checkout(TestUsers.UserA, "quarterly");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Portal_WithoutCustomer_Returns400()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/stripe/portal").WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Portal_WithCustomer_ReturnsUrl()
    {
        await SetUser(TestUsers.UserA, u => u.StripeCustomerId = "cus_existing");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/stripe/portal").WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("url").GetString().Should().Be(factory.Stripe.NextPortalUrl);
    }
}
