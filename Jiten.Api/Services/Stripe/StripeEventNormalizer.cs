using Stripe;
using Stripe.Checkout;

namespace Jiten.Api.Services.Stripe;

/// <summary>
/// Maps a verified Stripe <see cref="Event"/> to the SDK-free <see cref="StripeWebhookEvent"/>. Shared by the
/// real gateway and the test stub so both interpret payloads identically.
/// </summary>
public static class StripeEventNormalizer
{
    public static StripeWebhookEvent Normalize(Event e)
    {
        switch (e.Type)
        {
            case EventTypes.CheckoutSessionCompleted:
            {
                var session = e.Data.Object as Session;
                var mode = string.Equals(session?.Mode, "payment", StringComparison.OrdinalIgnoreCase)
                    ? StripeCheckoutMode.Payment
                    : StripeCheckoutMode.Subscription;
                return new StripeWebhookEvent(
                    StripeWebhookKind.CheckoutCompleted, e.Type, e.Id,
                    session?.CustomerId, session?.SubscriptionId, mode,
                    MetadataUserId(session?.Metadata), null);
            }

            case EventTypes.CustomerSubscriptionUpdated:
            {
                var sub = e.Data.Object as Subscription;
                return new StripeWebhookEvent(
                    StripeWebhookKind.SubscriptionUpdated, e.Type, e.Id,
                    sub?.CustomerId, sub?.Id, null,
                    MetadataUserId(sub?.Metadata), SnapshotFrom(sub));
            }

            case EventTypes.CustomerSubscriptionDeleted:
            {
                var sub = e.Data.Object as Subscription;
                return new StripeWebhookEvent(
                    StripeWebhookKind.SubscriptionDeleted, e.Type, e.Id,
                    sub?.CustomerId, sub?.Id, null,
                    MetadataUserId(sub?.Metadata), SnapshotFrom(sub));
            }

            case EventTypes.InvoicePaymentFailed:
            {
                var invoice = e.Data.Object as Invoice;
                return new StripeWebhookEvent(
                    StripeWebhookKind.PaymentFailed, e.Type, e.Id,
                    invoice?.CustomerId, null, null,
                    MetadataUserId(invoice?.Metadata), null);
            }

            default:
                return new StripeWebhookEvent(StripeWebhookKind.Unknown, e.Type, e.Id, null, null, null, null, null);
        }
    }

    public static StripeSubscriptionSnapshot? SnapshotFrom(Subscription? sub)
    {
        if (sub is null) return null;

        var item = sub.Items?.Data?.FirstOrDefault();
        return new StripeSubscriptionSnapshot(
            sub.Id,
            sub.CustomerId,
            sub.Status,
            item?.Price?.Id,
            NormalizePeriodEnd(item?.CurrentPeriodEnd),
            sub.CancelAtPeriodEnd,
            NormalizePeriodEnd(sub.EndedAt));
    }

    private static string? MetadataUserId(IDictionary<string, string>? metadata) =>
        metadata is not null && metadata.TryGetValue("userId", out var id) && !string.IsNullOrEmpty(id) ? id : null;

    private static DateTime? NormalizePeriodEnd(DateTime? value) =>
        value is null || value.Value <= new DateTime(1971, 1, 1, 0, 0, 0, DateTimeKind.Utc) ? null : value;
}
