using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace Jiten.Api.Services.Stripe;

/// <summary>The real gateway. Every Stripe.net call the platform makes lives here and nowhere else.</summary>
public class StripeGateway : IStripeGateway
{
    private readonly StripeOptions _options;
    private readonly StripeClient _client;

    public StripeGateway(IOptions<StripeOptions> options)
    {
        _options = options.Value;
        _client = new StripeClient(_options.SecretKey);
    }

    public async Task<string> CreateCustomerAsync(string email, string userId, CancellationToken ct = default)
    {
        var service = new CustomerService(_client);
        var customer = await service.CreateAsync(new CustomerCreateOptions
        {
            Email = email,
            // The webhook's source of truth for identity.
            Metadata = new Dictionary<string, string> { ["userId"] = userId }
        }, cancellationToken: ct);
        return customer.Id;
    }

    public async Task<string?> GetCustomerUserIdAsync(string customerId, CancellationToken ct = default)
    {
        var service = new CustomerService(_client);
        var customer = await service.GetAsync(customerId, cancellationToken: ct);
        return customer.Metadata is not null && customer.Metadata.TryGetValue("userId", out var id) ? id : null;
    }

    public async Task<StripeCheckoutResult> CreateCheckoutSessionAsync(StripeCheckoutRequest request, CancellationToken ct = default)
    {
        var options = new SessionCreateOptions
        {
            Customer = request.CustomerId,
            Mode = request.Mode == StripeCheckoutMode.Payment ? "payment" : "subscription",
            LineItems = [new SessionLineItemOptions { Price = request.PriceId, Quantity = 1 }],
            SuccessUrl = request.SuccessUrl,
            CancelUrl = request.CancelUrl,
            Metadata = new Dictionary<string, string> { ["userId"] = request.UserId },
            // EU 14-day withdrawal right: express consent that immediate access waives it
            ConsentCollection = new SessionConsentCollectionOptions { TermsOfService = "required" },
            CustomText = new SessionCustomTextOptions
            {
                TermsOfServiceAcceptance = new SessionCustomTextTermsOfServiceAcceptanceOptions
                {
                    Message = "I request immediate access to Jiten+ and acknowledge that I lose my 14-day right of " +
                              "withdrawal once access begins."
                }
            }
        };

        if (request.Mode == StripeCheckoutMode.Subscription)
            options.SubscriptionData = new SessionSubscriptionDataOptions
            {
                Metadata = new Dictionary<string, string> { ["userId"] = request.UserId }
            };

        if (!string.IsNullOrEmpty(request.CouponId))
            options.Discounts = [new SessionDiscountOptions { Coupon = request.CouponId }];

        var service = new SessionService(_client);
        var session = await service.CreateAsync(options, cancellationToken: ct);
        return new StripeCheckoutResult(session.Id, session.Url);
    }

    public async Task<string> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken ct = default)
    {
        var service = new global::Stripe.BillingPortal.SessionService(_client);
        var session = await service.CreateAsync(new global::Stripe.BillingPortal.SessionCreateOptions
        {
            Customer = customerId,
            ReturnUrl = returnUrl
        }, cancellationToken: ct);
        return session.Url;
    }

    public async Task<string> CreateAmountOffCouponAsync(long amountOffCents, string currency, string name, CancellationToken ct = default)
    {
        var service = new CouponService(_client);
        var coupon = await service.CreateAsync(new CouponCreateOptions
        {
            AmountOff = amountOffCents,
            Currency = currency,
            Duration = "once",
            Name = name,
            MaxRedemptions = 1
        }, cancellationToken: ct);
        return coupon.Id;
    }

    public async Task CancelSubscriptionAtPeriodEndAsync(string subscriptionId, CancellationToken ct = default)
    {
        var service = new SubscriptionService(_client);
        await service.UpdateAsync(subscriptionId, new SubscriptionUpdateOptions { CancelAtPeriodEnd = true }, cancellationToken: ct);
    }

    public async Task<StripeSubscriptionSnapshot?> GetSubscriptionAsync(string subscriptionId, CancellationToken ct = default)
    {
        var service = new SubscriptionService(_client);
        var sub = await service.GetAsync(subscriptionId, cancellationToken: ct);
        return StripeEventNormalizer.SnapshotFrom(sub);
    }

    public async Task<IReadOnlyList<StripeSubscriptionSnapshot>> ListSubscriptionsAsync(string customerId, CancellationToken ct = default)
    {
        var service = new SubscriptionService(_client);
        var list = await service.ListAsync(new SubscriptionListOptions { Customer = customerId, Status = "all", Limit = 100 }, cancellationToken: ct);
        return list.Data.Select(s => StripeEventNormalizer.SnapshotFrom(s)!).ToList();
    }

    public StripeWebhookEvent ConstructEvent(string payload, string signatureHeader)
    {
        // 300s tolerance (Stripe default); tolerate api-version drift so a dashboard version bump never drops events.
        var e = EventUtility.ConstructEvent(payload, signatureHeader, _options.WebhookSecret,
                                            tolerance: 300, throwOnApiVersionMismatch: false);
        return StripeEventNormalizer.Normalize(e);
    }
}
