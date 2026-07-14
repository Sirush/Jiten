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
    public async Task Checkout_LifetimeWithActiveSubscription_AttachesUpgradeCreditFromActualPayments()
    {
        await SetUser(TestUsers.UserA, u =>
        {
            u.StripeCustomerId = "cus_existing";
            u.StripeSubscriptionActive = true;
            u.StripeSubscriptionId = "sub_x";
            u.SubscriptionPlan = SubscriptionPlan.Yearly;
            u.SubscriptionPeriodEnd = DateTime.UtcNow.AddDays(182.5);
        });

        // A genuinely-paid yearly, half elapsed → €25 credit (from the actual €50 invoice, not the plan table).
        var periodStart = DateTime.UtcNow.AddDays(-182.5);
        factory.Stripe.Subscriptions["sub_x"] = new StripeSubscriptionSnapshot(
            "sub_x", "cus_existing", "active", "price_yearly", DateTime.UtcNow.AddDays(182.5), false,
            CurrentPeriodStart: periodStart);
        factory.Stripe.Invoices["sub_x"] = new[] { new StripeInvoiceRecord("in_1", 5000, 0, periodStart) };

        var response = await Checkout(TestUsers.UserA, "lifetime");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        factory.Stripe.Coupons.Should().ContainSingle()
               .Which.AmountCents.Should().Be(2500);
        factory.Stripe.CheckoutRequests.Should().ContainSingle()
               .Which.CouponId.Should().Be(factory.Stripe.NextCouponId);
    }

    [Fact]
    public async Task Checkout_LifetimeAfterPlanSwitch_CreditsOnlyActualPaid_Not50()
    {
        // Exact live repro: monthly (€5) → portal switch to yearly (€4.25 proration). Subscription now reads
        // yearly with a year-out period, but only €9.25 was collected — the credit must reflect that, not €50.
        await SetUser(TestUsers.UserA, u =>
        {
            u.StripeCustomerId = "cus_switch";
            u.StripeSubscriptionActive = true;
            u.StripeSubscriptionId = "sub_switch";
            u.SubscriptionPlan = SubscriptionPlan.Yearly;
            u.SubscriptionPeriodEnd = DateTime.UtcNow.AddDays(365);
        });

        var periodStart = DateTime.UtcNow;
        factory.Stripe.Subscriptions["sub_switch"] = new StripeSubscriptionSnapshot(
            "sub_switch", "cus_switch", "active", "price_yearly", periodStart.AddDays(31), false,
            CurrentPeriodStart: periodStart);
        factory.Stripe.Invoices["sub_switch"] = new[]
        {
            new StripeInvoiceRecord("in_monthly", 500, 0, periodStart),
            new StripeInvoiceRecord("in_proration", 425, 0, periodStart)
        };

        var response = await Checkout(TestUsers.UserA, "lifetime");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var coupon = factory.Stripe.Coupons.Should().ContainSingle().Subject;
        coupon.AmountCents.Should().Be(925);
        coupon.AmountCents.Should().NotBe(5000);
    }

    [Fact]
    public async Task Checkout_UnknownPlan_Returns400()
    {
        var response = await Checkout(TestUsers.UserA, "quarterly");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("monthly")]
    [InlineData("yearly")]
    [InlineData("lifetime")]
    public async Task Checkout_WhenAlreadyLifetime_IsBlockedForEveryPlan(string plan)
    {
        await SetUser(TestUsers.UserA, u =>
        {
            u.IsLifetime = true;
            u.LifetimeSource = LifetimeSource.WindowPurchase;
        });

        var response = await Checkout(TestUsers.UserA, plan);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("lifetime access");

        // No Stripe interaction at all — the guard short-circuits before any gateway call.
        factory.Stripe.CheckoutRequests.Should().BeEmpty();
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
