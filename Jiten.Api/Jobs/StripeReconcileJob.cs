using Hangfire;
using Jiten.Api.Services;
using Jiten.Api.Services.Stripe;
using Jiten.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Jiten.Api.Jobs;

/// <summary>
/// Daily safety net for missed or out-of-order webhooks: re-reads every customer's subscriptions from Stripe
/// and corrects any drift in the local subscription flags. Corrections are logged at Warning and the tier
/// cache is dropped so the fix is visible immediately.
/// </summary>
public class StripeReconcileJob(
    IDbContextFactory<UserDbContext> contextFactory,
    IStripeGateway gateway,
    IJitenPlusService jitenPlus,
    IBillingAlertService alerts,
    IOptions<StripeOptions> options,
    ILogger<StripeReconcileJob> logger)
{
    private readonly StripeOptions _options = options.Value;

    [Queue("default")]
    public async Task Reconcile()
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var userIds = await context.Users
                                   .Where(u => u.StripeCustomerId != null && u.StripeCustomerId != "")
                                   .Select(u => u.Id)
                                   .ToListAsync();

        var corrected = 0;
        var failed = 0;

        foreach (var userId in userIds)
        {
            try
            {
                if (await ReconcileUserAsync(context, userId))
                {
                    corrected++;
                    BillingTelemetry.ReconcileCorrected.Add(1);
                    jitenPlus.InvalidateTier(userId);
                }
            }
            catch (Exception ex)
            {
                failed++;
                BillingTelemetry.ReconcileFailed.Add(1);
                logger.LogError(ex, "StripeReconcile: failed for user {UserId}", userId);
            }

            // Small throttle
            await Task.Delay(250);
        }

        logger.LogInformation("StripeReconcile: checked {Count} customers, corrected {Corrected}, failed {Failed}",
                              userIds.Count, corrected, failed);

        // Steady state is zero of both: a correction means a webhook was missed, a failure means Stripe could
        // not be read, and either way the safety net is reporting that the primary path is not working.
        if (corrected > 0 || failed > 0)
            await alerts.RaiseAsync("reconcile-drift",
                                    "Stripe reconciliation found drift",
                                    $"Checked {userIds.Count} customers: corrected {corrected}, failed {failed}. " +
                                    "Corrections mean webhook events were missed — check the endpoint's delivery log in Stripe.");
    }

    private async Task<bool> ReconcileUserAsync(UserDbContext context, string userId)
    {
        var user = await context.Users.FirstAsync(u => u.Id == userId);

        var subs = await gateway.ListSubscriptionsAsync(user.StripeCustomerId!);

        // The canonical subscription: an active/trialing one if present, otherwise the one we already track.
        var canonical = subs.FirstOrDefault(s => StripeOptions.IsActiveStatus(s.Status))
                        ?? subs.FirstOrDefault(s => s.Id == user.StripeSubscriptionId);

        var changed = false;

        if (canonical is null)
        {
            // Stripe knows of no live subscription: if we still think one is active, that's drift.
            if (user.StripeSubscriptionActive)
            {
                logger.LogWarning("StripeReconcile: user {UserId} marked active but Stripe has no live subscription", userId);
                user.StripeSubscriptionActive = false;
                changed = true;
            }

            if (changed) await context.SaveChangesAsync();
            return changed;
        }

        var active = StripeOptions.IsActiveStatus(canonical.Status);
        if (user.StripeSubscriptionActive != active)
        {
            logger.LogWarning("StripeReconcile: user {UserId} active flag {Old} -> {New}", userId, user.StripeSubscriptionActive, active);
            user.StripeSubscriptionActive = active;
            changed = true;
        }

        if (user.StripeSubscriptionId != canonical.Id)
        {
            logger.LogWarning("StripeReconcile: user {UserId} subscription id {Old} -> {New}", userId, user.StripeSubscriptionId, canonical.Id);
            user.StripeSubscriptionId = canonical.Id;
            changed = true;
        }

        if (canonical.CurrentPeriodEnd.HasValue && user.SubscriptionPeriodEnd != canonical.CurrentPeriodEnd)
        {
            logger.LogWarning("StripeReconcile: user {UserId} period end {Old} -> {New}", userId, user.SubscriptionPeriodEnd, canonical.CurrentPeriodEnd);
            user.SubscriptionPeriodEnd = canonical.CurrentPeriodEnd;
            changed = true;
        }

        var plan = _options.PlanForPriceId(canonical.PriceId);
        if (plan.HasValue && user.SubscriptionPlan != plan)
        {
            logger.LogWarning("StripeReconcile: user {UserId} plan {Old} -> {New}", userId, user.SubscriptionPlan, plan);
            user.SubscriptionPlan = plan;
            changed = true;
        }

        if (changed) await context.SaveChangesAsync();
        return changed;
    }
}
