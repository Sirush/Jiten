using Jiten.Api.Services.Stripe;
using Stripe;

namespace Jiten.Parser.Tests.Integration.Infrastructure;

/// <summary>
/// Test double for <see cref="IStripeGateway"/>. Network calls are canned and recorded; only
/// <see cref="ConstructEvent"/> runs for real (genuine HMAC signature verification) so signed test payloads
/// are validated exactly as in production. Registered as a singleton, mirroring <see cref="StubCdnService"/>.
/// </summary>
public class StubStripeGateway : IStripeGateway
{
    /// <summary>Signing secret used by <see cref="ConstructEvent"/>; test payloads must be signed with this.</summary>
    public const string WebhookSecret = "whsec_test_secret";

    public string NextCustomerId { get; set; } = "cus_stub";
    public string NextCheckoutUrl { get; set; } = "https://checkout.test/session";
    public string NextPortalUrl { get; set; } = "https://portal.test/session";
    public string NextCouponId { get; set; } = "coupon_stub";
    public string? CustomerUserId { get; set; }

    public Dictionary<string, StripeSubscriptionSnapshot> Subscriptions { get; } = new();
    public Func<string, IReadOnlyList<StripeSubscriptionSnapshot>>? ListSubscriptionsFor { get; set; }

    public List<StripeCheckoutRequest> CheckoutRequests { get; } = new();
    public List<string> CanceledSubscriptions { get; } = new();
    public List<(long AmountCents, string Currency, string Name)> Coupons { get; } = new();
    public List<(string Email, string UserId)> CreatedCustomers { get; } = new();

    public void Reset()
    {
        Subscriptions.Clear();
        ListSubscriptionsFor = null;
        CheckoutRequests.Clear();
        CanceledSubscriptions.Clear();
        Coupons.Clear();
        CreatedCustomers.Clear();
        CustomerUserId = null;
    }

    public Task<string> CreateCustomerAsync(string email, string userId, CancellationToken ct = default)
    {
        CreatedCustomers.Add((email, userId));
        return Task.FromResult(NextCustomerId);
    }

    public Task<string?> GetCustomerUserIdAsync(string customerId, CancellationToken ct = default) =>
        Task.FromResult(CustomerUserId);

    public Task<StripeCheckoutResult> CreateCheckoutSessionAsync(StripeCheckoutRequest request, CancellationToken ct = default)
    {
        CheckoutRequests.Add(request);
        return Task.FromResult(new StripeCheckoutResult("cs_stub", NextCheckoutUrl));
    }

    public Task<string> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken ct = default) =>
        Task.FromResult(NextPortalUrl);

    public Task<string> CreateAmountOffCouponAsync(long amountOffCents, string currency, string name, CancellationToken ct = default)
    {
        Coupons.Add((amountOffCents, currency, name));
        return Task.FromResult(NextCouponId);
    }

    public Task CancelSubscriptionAtPeriodEndAsync(string subscriptionId, CancellationToken ct = default)
    {
        CanceledSubscriptions.Add(subscriptionId);
        return Task.CompletedTask;
    }

    public Task<StripeSubscriptionSnapshot?> GetSubscriptionAsync(string subscriptionId, CancellationToken ct = default) =>
        Task.FromResult(Subscriptions.GetValueOrDefault(subscriptionId));

    public Task<IReadOnlyList<StripeSubscriptionSnapshot>> ListSubscriptionsAsync(string customerId, CancellationToken ct = default) =>
        Task.FromResult(ListSubscriptionsFor?.Invoke(customerId) ?? []);

    public StripeWebhookEvent ConstructEvent(string payload, string signatureHeader)
    {
        var e = EventUtility.ConstructEvent(payload, signatureHeader, WebhookSecret, tolerance: 300, throwOnApiVersionMismatch: false);
        return StripeEventNormalizer.Normalize(e);
    }
}
