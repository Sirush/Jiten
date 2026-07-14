using Jiten.Core;
using Jiten.Core.Data.Authentication;
using Jiten.Core.Data.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Jiten.Api.Services.Stripe;

public enum CheckoutPlan
{
    Monthly,
    Yearly,
    Lifetime
}

public record CheckoutOutcome(bool Success, string? Url, string? Error);

/// <summary>
/// Jiten+ billing logic on top of <see cref="IStripeGateway"/>. Owns checkout/portal creation, the
/// upgrade-credit computation, and idempotent webhook handling. Kept free of Stripe SDK types so it is
/// unit-testable against a stub gateway.
/// </summary>
public class StripeService(
    IStripeGateway gateway,
    UserDbContext userContext,
    IJitenPlusService jitenPlus,
    IEmailService emails,
    IMemoryCache cache,
    IOptions<StripeOptions> options,
    ILogger<StripeService> logger)
{
    private const string SiteUrl = "https://jiten.moe";
    private const string Currency = "eur";

    // Sticker prices in whole cents (tax-inclusive), used only for the upgrade-credit proration.
    private const long MonthlyPriceCents = 500;
    private const long YearlyPriceCents = 5000;
    private const int MonthlyLengthDays = 30;
    private const int YearlyLengthDays = 365;

    private readonly StripeOptions _options = options.Value;

    public async Task<CheckoutOutcome> CreateCheckoutAsync(string userId, CheckoutPlan plan, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (plan == CheckoutPlan.Lifetime && now > _options.LifetimeWindowEnd)
            return new CheckoutOutcome(false, null, "The lifetime window has closed.");

        var user = await userContext.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return new CheckoutOutcome(false, null, "User not found.");

        if (string.IsNullOrEmpty(user.StripeCustomerId))
        {
            user.StripeCustomerId = await gateway.CreateCustomerAsync(user.Email ?? "", userId, ct);
            await userContext.SaveChangesAsync(ct);
        }

        var mode = plan == CheckoutPlan.Lifetime ? StripeCheckoutMode.Payment : StripeCheckoutMode.Subscription;
        var priceId = plan switch
        {
            CheckoutPlan.Monthly => _options.MonthlyPriceId,
            CheckoutPlan.Yearly => _options.YearlyPriceId,
            _ => _options.LifetimePriceId
        };

        string? couponId = null;
        if (plan == CheckoutPlan.Lifetime && user.StripeSubscriptionActive && user.SubscriptionPlan.HasValue)
        {
            var creditCents = ComputeUpgradeCreditCents(user.SubscriptionPlan.Value, user.SubscriptionPeriodEnd, now);
            if (creditCents > 0)
                couponId = await gateway.CreateAmountOffCouponAsync(
                    creditCents, Currency, $"Jiten+ upgrade credit ({creditCents / 100.0:0.00} EUR)", ct);
        }

        var request = new StripeCheckoutRequest(
            user.StripeCustomerId!, mode, priceId, userId,
            SuccessUrl: $"{SiteUrl}/settings/subscription?checkout=success",
            CancelUrl: $"{SiteUrl}/jiten-plus",
            CouponId: couponId);

        var result = await gateway.CreateCheckoutSessionAsync(request, ct);
        return new CheckoutOutcome(true, result.Url, null);
    }

    public async Task<CheckoutOutcome> CreatePortalAsync(string userId, CancellationToken ct = default)
    {
        var customerId = await userContext.Users.Where(u => u.Id == userId)
                                          .Select(u => u.StripeCustomerId).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(customerId))
            return new CheckoutOutcome(false, null, "No billing account. Subscribe first.");

        var url = await gateway.CreatePortalSessionAsync(customerId, $"{SiteUrl}/settings/subscription", ct);
        return new CheckoutOutcome(true, url, null);
    }

    /// <summary>
    /// Unused subscription value as whole cents: plan price pro-rated by remaining days over the plan length.
    /// A period end in the past yields 0. Clamped to the plan price.
    /// </summary>
    public static long ComputeUpgradeCreditCents(SubscriptionPlan plan, DateTime? periodEnd, DateTime now)
    {
        if (periodEnd is null || periodEnd.Value <= now)
            return 0;

        var (priceCents, lengthDays) = plan == SubscriptionPlan.Monthly
            ? (MonthlyPriceCents, MonthlyLengthDays)
            : (YearlyPriceCents, YearlyLengthDays);

        var remainingDays = (periodEnd.Value - now).TotalDays;
        var raw = priceCents * (remainingDays / lengthDays);
        var rounded = (long)Math.Round(raw, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, 0, priceCents);
    }

    // ---- Webhook handling ----------------------------------------------------------------------

    /// <summary>
    /// Applies a verified webhook event. Idempotent: replays re-sync state without double-sending emails
    /// (via current-state checks) and a short-lived event-id cache blunts rapid redelivery. Throws on failure
    /// so the controller can return 500 and Stripe retries.
    /// </summary>
    public async Task HandleWebhookAsync(StripeWebhookEvent evt, CancellationToken ct = default)
    {
        if (evt.Kind == StripeWebhookKind.Unknown)
            return;

        var dedupeKey = $"stripe:evt:{evt.EventId}";
        if (cache.TryGetValue(dedupeKey, out _))
        {
            logger.LogInformation("Stripe webhook {EventId} ({Type}) already processed, skipping", evt.EventId, evt.RawType);
            return;
        }

        var userId = await ResolveUserIdAsync(evt, ct);
        if (userId is null)
        {
            logger.LogWarning("Stripe webhook {EventId} ({Type}): could not resolve a user for customer {Customer}",
                              evt.EventId, evt.RawType, evt.CustomerId);
            cache.Set(dedupeKey, true, TimeSpan.FromHours(6));
            return;
        }

        var user = await userContext.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            logger.LogWarning("Stripe webhook {EventId} ({Type}): user {UserId} not found", evt.EventId, evt.RawType, userId);
            cache.Set(dedupeKey, true, TimeSpan.FromHours(6));
            return;
        }

        // Backfill the customer link if we resolved via metadata but never stored it.
        if (string.IsNullOrEmpty(user.StripeCustomerId) && !string.IsNullOrEmpty(evt.CustomerId))
            user.StripeCustomerId = evt.CustomerId;

        switch (evt.Kind)
        {
            case StripeWebhookKind.CheckoutCompleted when evt.CheckoutMode == StripeCheckoutMode.Payment:
                await HandleLifetimeAsync(user, ct);
                break;
            case StripeWebhookKind.CheckoutCompleted:
                await HandleSubscriptionCheckoutAsync(user, evt, ct);
                break;
            case StripeWebhookKind.SubscriptionUpdated:
                await HandleSubscriptionUpdatedAsync(user, evt, ct);
                break;
            case StripeWebhookKind.SubscriptionDeleted:
                await HandleSubscriptionDeletedAsync(user, evt, ct);
                break;
            case StripeWebhookKind.PaymentFailed:
                await SafeSend(() => emails.SendSubscriptionPaymentFailedAsync(user.Email));
                break;
        }

        jitenPlus.InvalidateTier(user.Id);
        cache.Set(dedupeKey, true, TimeSpan.FromHours(6));
    }

    private async Task HandleLifetimeAsync(User user, CancellationToken ct)
    {
        if (user.IsLifetime)
            return; // already applied — idempotent

        user.IsLifetime = true;
        user.LifetimeSource = LifetimeSource.WindowPurchase;

        var subToCancel = user.StripeSubscriptionActive ? user.StripeSubscriptionId : null;
        await userContext.SaveChangesAsync(ct);

        if (!string.IsNullOrEmpty(subToCancel))
            await gateway.CancelSubscriptionAtPeriodEndAsync(subToCancel, ct);

        await SafeSend(() => emails.SendLifetimeConfirmedAsync(user.Email));
    }

    private async Task HandleSubscriptionCheckoutAsync(User user, StripeWebhookEvent evt, CancellationToken ct)
    {
        var wasActive = user.StripeSubscriptionActive && user.StripeSubscriptionId == evt.SubscriptionId;

        var snapshot = evt.SubscriptionId is not null ? await gateway.GetSubscriptionAsync(evt.SubscriptionId, ct) : null;
        ApplySubscriptionState(user, evt.SubscriptionId, snapshot, activeFallback: true);
        await userContext.SaveChangesAsync(ct);

        if (!wasActive)
            await SafeSend(() => emails.SendSubscriptionConfirmedAsync(user.Email, user.SubscriptionPlan));
    }

    private async Task HandleSubscriptionUpdatedAsync(User user, StripeWebhookEvent evt, CancellationToken ct)
    {
        ApplySubscriptionState(user, evt.SubscriptionId, evt.Subscription, activeFallback: false);
        await userContext.SaveChangesAsync(ct);
    }

    private async Task HandleSubscriptionDeletedAsync(User user, StripeWebhookEvent evt, CancellationToken ct)
    {
        // Ignore a delete for a subscription that isn't the one we track (e.g. a superseded one).
        if (!string.IsNullOrEmpty(user.StripeSubscriptionId) && user.StripeSubscriptionId != evt.SubscriptionId)
            return;

        var wasActive = user.StripeSubscriptionActive;
        user.StripeSubscriptionActive = false;

        // Clamp the stored period end to when the subscription actually ended. A normal end-of-period
        // cancellation ends at the period end (unchanged → the 3-day grace still applies); an immediate
        // cancellation (admin/fraud/refund) ends now, so the grace runs from the real end instead of
        // leaving the user Full until an original period end that may be a year away.
        var endedAt = evt.Subscription?.EndedAt ?? DateTime.UtcNow;
        if (!user.SubscriptionPeriodEnd.HasValue || endedAt < user.SubscriptionPeriodEnd.Value)
            user.SubscriptionPeriodEnd = endedAt;

        await userContext.SaveChangesAsync(ct);

        if (wasActive)
            await SafeSend(() => emails.SendSubscriptionEndedAsync(user.Email));
    }

    /// <summary>Writes subscription fields from a snapshot. When no snapshot is available, only the id and an
    /// optional active fallback (checkout completed implies active) are applied.</summary>
    private void ApplySubscriptionState(User user, string? subscriptionId, StripeSubscriptionSnapshot? snapshot, bool activeFallback)
    {
        if (!string.IsNullOrEmpty(subscriptionId))
            user.StripeSubscriptionId = subscriptionId;

        if (snapshot is null)
        {
            if (activeFallback)
                user.StripeSubscriptionActive = true;
            return;
        }

        user.StripeSubscriptionActive = IsActiveStatus(snapshot.Status);
        if (snapshot.CurrentPeriodEnd.HasValue)
            user.SubscriptionPeriodEnd = snapshot.CurrentPeriodEnd;

        var plan = PlanFromPriceId(snapshot.PriceId);
        if (plan.HasValue)
            user.SubscriptionPlan = plan;
    }

    private async Task<string?> ResolveUserIdAsync(StripeWebhookEvent evt, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(evt.MetadataUserId))
            return evt.MetadataUserId;

        if (string.IsNullOrEmpty(evt.CustomerId))
            return null;

        var fromCustomer = await gateway.GetCustomerUserIdAsync(evt.CustomerId, ct);
        if (!string.IsNullOrEmpty(fromCustomer))
            return fromCustomer;

        return await userContext.Users.Where(u => u.StripeCustomerId == evt.CustomerId)
                                .Select(u => u.Id).FirstOrDefaultAsync(ct);
    }

    private SubscriptionPlan? PlanFromPriceId(string? priceId)
    {
        if (string.IsNullOrEmpty(priceId)) return null;
        if (priceId == _options.MonthlyPriceId) return SubscriptionPlan.Monthly;
        if (priceId == _options.YearlyPriceId) return SubscriptionPlan.Yearly;
        return null;
    }

    private static bool IsActiveStatus(string? status) =>
        status is "active" or "trialing";

    private async Task SafeSend(Func<Task> send)
    {
        try
        {
            await send();
        }
        catch (Exception ex)
        {
            // A billing email failure must not fail the webhook (which would trigger a Stripe retry and re-run
            // the state change). Log and move on.
            logger.LogError(ex, "Failed to send a Jiten+ billing email");
        }
    }
}
