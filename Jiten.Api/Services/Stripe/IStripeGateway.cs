namespace Jiten.Api.Services.Stripe;

public enum StripeCheckoutMode
{
    Subscription,
    Payment
}

public enum StripeWebhookKind
{
    Unknown,
    CheckoutCompleted,
    SubscriptionUpdated,
    SubscriptionDeleted,
    PaymentFailed
}

/// <summary>A checkout session request, shaped by <see cref="StripeService"/> and executed by the gateway.</summary>
public record StripeCheckoutRequest(
    string CustomerId,
    StripeCheckoutMode Mode,
    string PriceId,
    string UserId,
    string SuccessUrl,
    string CancelUrl,
    string? CouponId);

public record StripeCheckoutResult(string SessionId, string Url);

/// <summary>A billing-provider-agnostic view of one subscription, so business logic never touches Stripe SDK types.</summary>
public record StripeSubscriptionSnapshot(
    string Id,
    string CustomerId,
    string Status,
    string? PriceId,
    DateTime? CurrentPeriodEnd,
    bool CancelAtPeriodEnd,
    DateTime? EndedAt = null);

/// <summary>A normalised, verified webhook event. The only Stripe SDK types are behind the gateway.</summary>
public record StripeWebhookEvent(
    StripeWebhookKind Kind,
    string RawType,
    string EventId,
    string? CustomerId,
    string? SubscriptionId,
    StripeCheckoutMode? CheckoutMode,
    string? MetadataUserId,
    StripeSubscriptionSnapshot? Subscription);

/// <summary>
/// Thin seam over every Stripe SDK call the API makes. The real
/// implementation talks to Stripe.net; tests register a stub so <see cref="StripeService"/> is exercised
/// without the network. <see cref="ConstructEvent"/> is the one method a stub still runs for real, so signed
/// test payloads verify genuinely.
/// </summary>
public interface IStripeGateway
{
    Task<string> CreateCustomerAsync(string email, string userId, CancellationToken ct = default);

    /// <summary>The customer's <c>metadata.userId</c> — the webhook's source of truth for identity.</summary>
    Task<string?> GetCustomerUserIdAsync(string customerId, CancellationToken ct = default);

    Task<StripeCheckoutResult> CreateCheckoutSessionAsync(StripeCheckoutRequest request, CancellationToken ct = default);

    Task<string> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken ct = default);

    /// <summary>Creates a one-off <c>amount_off</c> coupon (duration: once) and returns its id.</summary>
    Task<string> CreateAmountOffCouponAsync(long amountOffCents, string currency, string name, CancellationToken ct = default);

    Task CancelSubscriptionAtPeriodEndAsync(string subscriptionId, CancellationToken ct = default);

    Task<StripeSubscriptionSnapshot?> GetSubscriptionAsync(string subscriptionId, CancellationToken ct = default);

    Task<IReadOnlyList<StripeSubscriptionSnapshot>> ListSubscriptionsAsync(string customerId, CancellationToken ct = default);

    /// <summary>Verifies the signature and returns a normalised event. Throws <see cref="global::Stripe.StripeException"/> on a bad signature.</summary>
    StripeWebhookEvent ConstructEvent(string payload, string signatureHeader);
}
