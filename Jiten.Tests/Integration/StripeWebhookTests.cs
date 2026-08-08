using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Api.Services.Stripe;
using Jiten.Core;
using Jiten.Core.Data.Billing;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class StripeWebhookTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private const string CustomerId = "cus_1";
    private const string SubscriptionId = "sub_1";

    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        factory.Stripe.Reset();
        factory.Emails.Clear();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        foreach (var user in await userDb.Users.ToListAsync())
        {
            user.StripeCustomerId = user.Id == TestUsers.UserA ? CustomerId : null;
            user.Email = $"{user.Id}@example.com";
            user.StripeSubscriptionActive = false;
            user.StripeSubscriptionId = null;
            user.StripeCancelAtPeriodEnd = false;
            user.SubscriptionPeriodEnd = null;
            user.SubscriptionPlan = null;
            user.IsLifetime = false;
            user.LifetimeSource = null;
        }
        await userDb.SaveChangesAsync();

        var jitenPlus = scope.ServiceProvider.GetRequiredService<IJitenPlusService>();
        foreach (var id in new[] { TestUsers.UserA, TestUsers.UserB, TestUsers.Admin })
            jitenPlus.InvalidateTier(id);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Sign(string payload)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(StubStripeGateway.WebhookSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ts}.{payload}"));
        var signature = Convert.ToHexString(hash).ToLowerInvariant();
        return $"t={ts},v1={signature}";
    }

    private async Task<HttpResponseMessage> PostEvent(string payload, string? signature = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/stripe/webhook")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Stripe-Signature", signature ?? Sign(payload));
        return await _client.SendAsync(request);
    }

    private async Task<Jiten.Core.Data.Authentication.User> GetUserA()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.Users.AsNoTracking().FirstAsync(u => u.Id == TestUsers.UserA);
    }

    // Placeholders (CUS/SUB/UID) are substituted rather than string-interpolated so JSON's braces stay literal.
    private static string Fill(string template) =>
        template.Replace("CUS", CustomerId).Replace("SUB", SubscriptionId).Replace("UID", TestUsers.UserA);

    private static string CheckoutSubscriptionPayload() => Fill("""
        {"id":"evt_cs_sub","object":"event","type":"checkout.session.completed","data":{"object":{"id":"cs_1","object":"checkout.session","mode":"subscription","customer":"CUS","subscription":"SUB","metadata":{"userId":"UID"}}}}
        """);

    private static string CheckoutLifetimePayload() => Fill("""
        {"id":"evt_cs_life","object":"event","type":"checkout.session.completed","data":{"object":{"id":"cs_2","object":"checkout.session","mode":"payment","customer":"CUS","metadata":{"userId":"UID"}}}}
        """);

    private static string SubscriptionUpdatedPayload() => Fill("""
        {"id":"evt_sub_upd","object":"event","type":"customer.subscription.updated","data":{"object":{"id":"SUB","object":"subscription","status":"active","customer":"CUS","cancel_at_period_end":false,"metadata":{"userId":"UID"},"items":{"object":"list","data":[{"id":"si_1","object":"subscription_item","current_period_end":1924992000,"price":{"id":"price_yearly","object":"price"}}]}}}}
        """);

    private static string SubscriptionCancelledAtPeriodEndPayload() => Fill("""
        {"id":"evt_sub_cancel","object":"event","type":"customer.subscription.updated","data":{"object":{"id":"SUB","object":"subscription","status":"active","customer":"CUS","cancel_at_period_end":true,"metadata":{"userId":"UID"},"items":{"object":"list","data":[{"id":"si_1","object":"subscription_item","current_period_end":1924992000,"price":{"id":"price_yearly","object":"price"}}]}}}}
        """);

    // Distinct event id from SubscriptionUpdatedPayload: the dedupe cache is shared across tests in the class.
    private static string SubscriptionResumedPayload() => Fill("""
        {"id":"evt_sub_resume","object":"event","type":"customer.subscription.updated","data":{"object":{"id":"SUB","object":"subscription","status":"active","customer":"CUS","cancel_at_period_end":false,"metadata":{"userId":"UID"},"items":{"object":"list","data":[{"id":"si_1","object":"subscription_item","current_period_end":1924992000,"price":{"id":"price_yearly","object":"price"}}]}}}}
        """);

    private static string SubscriptionDeletedPayload() => Fill("""
        {"id":"evt_sub_del","object":"event","type":"customer.subscription.deleted","data":{"object":{"id":"SUB","object":"subscription","status":"canceled","customer":"CUS","metadata":{"userId":"UID"},"items":{"object":"list","data":[{"id":"si_1","object":"subscription_item","current_period_end":1924992000,"price":{"id":"price_yearly","object":"price"}}]}}}}
        """);

    // Immediate cancellation: subscription-level ended_at set to 2026-01-01 (1767225600).
    private static string SubscriptionDeletedImmediatePayload() => Fill("""
        {"id":"evt_sub_del_now","object":"event","type":"customer.subscription.deleted","data":{"object":{"id":"SUB","object":"subscription","status":"canceled","customer":"CUS","ended_at":1767225600,"metadata":{"userId":"UID"},"items":{"object":"list","data":[{"id":"si_1","object":"subscription_item","current_period_end":1924992000,"price":{"id":"price_yearly","object":"price"}}]}}}}
        """);

    private static string PaymentFailedPayload() => Fill("""
        {"id":"evt_inv_fail","object":"event","type":"invoice.payment_failed","data":{"object":{"id":"in_1","object":"invoice","customer":"CUS","metadata":{"userId":"UID"}}}}
        """);

    [Fact]
    public async Task BadSignature_Returns400()
    {
        var response = await PostEvent(PaymentFailedPayload(), signature: "t=123,v1=deadbeef");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CheckoutSubscription_ActivatesUser()
    {
        factory.Stripe.Subscriptions[SubscriptionId] =
            new StripeSubscriptionSnapshot(SubscriptionId, CustomerId, "active", "price_yearly",
                                           DateTime.UtcNow.AddDays(365), false);

        var response = await PostEvent(CheckoutSubscriptionPayload());
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var user = await GetUserA();
        user.StripeSubscriptionActive.Should().BeTrue();
        user.StripeSubscriptionId.Should().Be(SubscriptionId);
        user.SubscriptionPlan.Should().Be(SubscriptionPlan.Yearly);
        factory.Emails.Sent.Should().Contain(e => e.Method == nameof(IEmailService.SendSubscriptionConfirmedAsync));
    }

    [Fact]
    public async Task CheckoutLifetime_SetsLifetime()
    {
        var response = await PostEvent(CheckoutLifetimePayload());
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var user = await GetUserA();
        user.IsLifetime.Should().BeTrue();
        user.LifetimeSource.Should().Be(LifetimeSource.WindowPurchase);
        factory.Emails.Sent.Should().Contain(e => e.Method == nameof(IEmailService.SendLifetimeConfirmedAsync));
    }

    [Fact]
    public async Task SubscriptionCancelledAtPeriodEnd_PersistsFlag_AndUncancelClearsIt()
    {
        var response = await PostEvent(SubscriptionCancelledAtPeriodEndPayload());
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var user = await GetUserA();
        user.StripeSubscriptionActive.Should().BeTrue();
        user.StripeCancelAtPeriodEnd.Should().BeTrue();

        // Resuming via the Stripe portal sends another update with cancel_at_period_end back to false.
        await PostEvent(SubscriptionResumedPayload());
        (await GetUserA()).StripeCancelAtPeriodEnd.Should().BeFalse();
    }

    [Fact]
    public async Task SubscriptionUpdated_SyncsPeriodEndAndPlan()
    {
        var response = await PostEvent(SubscriptionUpdatedPayload());
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var user = await GetUserA();
        user.StripeSubscriptionActive.Should().BeTrue();
        user.SubscriptionPlan.Should().Be(SubscriptionPlan.Yearly);
        user.SubscriptionPeriodEnd.Should().BeCloseTo(
            DateTimeOffset.FromUnixTimeSeconds(1924992000).UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SubscriptionDeleted_DeactivatesAndEmails()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var u = await userDb.Users.FirstAsync(x => x.Id == TestUsers.UserA);
            u.StripeSubscriptionActive = true;
            u.StripeSubscriptionId = SubscriptionId;
            await userDb.SaveChangesAsync();
            scope.ServiceProvider.GetRequiredService<IJitenPlusService>().InvalidateTier(TestUsers.UserA);
        }

        var response = await PostEvent(SubscriptionDeletedPayload());
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetUserA()).StripeSubscriptionActive.Should().BeFalse();
        factory.Emails.Sent.Should().Contain(e => e.Method == nameof(IEmailService.SendSubscriptionEndedAsync));
    }

    [Fact]
    public async Task SubscriptionDeleted_ImmediateCancel_ClampsPeriodEnd()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var u = await userDb.Users.FirstAsync(x => x.Id == TestUsers.UserA);
            u.StripeSubscriptionActive = true;
            u.StripeSubscriptionId = SubscriptionId;
            u.SubscriptionPeriodEnd = DateTime.UtcNow.AddDays(300); // original end ~10 months out
            await userDb.SaveChangesAsync();
            scope.ServiceProvider.GetRequiredService<IJitenPlusService>().InvalidateTier(TestUsers.UserA);
        }

        var response = await PostEvent(SubscriptionDeletedImmediatePayload());
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var user = await GetUserA();
        user.StripeSubscriptionActive.Should().BeFalse();
        user.SubscriptionPeriodEnd.Should().BeCloseTo(
            DateTimeOffset.FromUnixTimeSeconds(1767225600).UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task PaymentFailed_EmailsOnly()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var u = await userDb.Users.FirstAsync(x => x.Id == TestUsers.UserA);
            u.StripeSubscriptionActive = true;
            await userDb.SaveChangesAsync();
        }

        var response = await PostEvent(PaymentFailedPayload());
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetUserA()).StripeSubscriptionActive.Should().BeTrue();
        factory.Emails.Sent.Should().Contain(e => e.Method == nameof(IEmailService.SendSubscriptionPaymentFailedAsync));
    }

    [Fact]
    public async Task UnknownEventType_Returns200()
    {
        var payload = """
        {"id":"evt_unknown","object":"event","type":"customer.created",
         "data":{"object":{"id":"cus_1","object":"customer"}}}
        """;
        var response = await PostEvent(payload);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
