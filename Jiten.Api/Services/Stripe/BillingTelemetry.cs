using System.Diagnostics.Metrics;

namespace Jiten.Api.Services.Stripe;

/// <summary>
/// Counters for the billing paths whose failures are otherwise silent: a webhook that never applied leaves a
/// paying user without access and produces no user-visible error. Any non-zero rate on the failure counters
/// warrants an alert.
/// </summary>
public static class BillingTelemetry
{
    public const string MeterName = "Jiten.Api.Billing";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> WebhookHandled = Meter.CreateCounter<long>("jiten.billing.webhook.handled");

    /// <summary>A verified event whose handler threw. Stripe retries, so a sustained rate means stuck state.</summary>
    public static readonly Counter<long> WebhookFailed = Meter.CreateCounter<long>("jiten.billing.webhook.failed");

    /// <summary>Rejected before handling. A steady rate right after a deploy means a stale WebhookSecret.</summary>
    public static readonly Counter<long> WebhookSignatureRejected =
        Meter.CreateCounter<long>("jiten.billing.webhook.signature_rejected");

    /// <summary>Event verified but no local user matched it — money moved against an account we cannot credit.</summary>
    public static readonly Counter<long> WebhookUnresolvedUser = Meter.CreateCounter<long>("jiten.billing.webhook.unresolved_user");

    public static readonly Counter<long> EmailFailed = Meter.CreateCounter<long>("jiten.billing.email.failed");

    /// <summary>Drift the daily job had to fix. Steady-state is zero; anything else means webhooks are being missed.</summary>
    public static readonly Counter<long> ReconcileCorrected = Meter.CreateCounter<long>("jiten.billing.reconcile.corrected");

    public static readonly Counter<long> ReconcileFailed = Meter.CreateCounter<long>("jiten.billing.reconcile.failed");
}
