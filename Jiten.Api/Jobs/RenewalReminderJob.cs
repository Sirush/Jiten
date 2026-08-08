using Hangfire;
using Jiten.Api.Services;
using Jiten.Api.Services.Stripe;
using Jiten.Core;
using Jiten.Core.Data.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Jiten.Api.Jobs;

/// <summary>
/// L215-1 renewal reminder (CGV art. 7.2): yearly subscribers must be told, in writing, of the date by which
/// they may object to renewal, no earlier than 3 months and no later than 1 month before it. Missing the
/// window lets the subscriber cancel any time post-renewal with a refund, so each send is logged and the job
/// alerts if a renewal is about to enter the final month unnotified.
/// </summary>
public class RenewalReminderJob(
    IDbContextFactory<UserDbContext> contextFactory,
    IEmailService emails,
    IBillingAlertService alerts,
    IOptions<StripeOptions> options,
    ILogger<RenewalReminderJob> logger)
{
    private readonly StripeOptions _options = options.Value;

    [Queue("default")]
    public async Task Run()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;

        // Send from 2.5 months out: safely inside the legal window with 6 weeks of retry room before it closes.
        var windowStart = now + TimeSpan.FromDays(30);
        var sendFrom = now + TimeSpan.FromDays(75);

        var candidates = await context.Users
                                      .Where(u => u.StripeSubscriptionActive &&
                                                  !u.StripeCancelAtPeriodEnd &&
                                                  u.SubscriptionPlan == SubscriptionPlan.Yearly &&
                                                  u.SubscriptionPeriodEnd != null &&
                                                  u.SubscriptionPeriodEnd > windowStart &&
                                                  u.SubscriptionPeriodEnd <= sendFrom)
                                      .Select(u => new { u.Id, u.Email, u.StripeSubscriptionId, RenewalDate = u.SubscriptionPeriodEnd!.Value })
                                      .ToListAsync();

        var sent = 0;
        foreach (var user in candidates)
        {
            var alreadySent = await context.BillingEmailLogs
                                           .AnyAsync(l => l.UserId == user.Id &&
                                                          l.Kind == BillingEmailKind.RenewalReminder &&
                                                          l.RenewalDate == user.RenewalDate);
            if (alreadySent)
                continue;

            // Email first, log after: a failed send throws and is retried next run; a duplicate send is the
            // cheaper failure compared with a logged-but-never-sent legal notice.
            await emails.SendRenewalReminderAsync(user.Email, user.RenewalDate, _options.YearlyPriceCents);
            context.BillingEmailLogs.Add(new BillingEmailLog
            {
                UserId = user.Id,
                Kind = BillingEmailKind.RenewalReminder,
                SubscriptionId = user.StripeSubscriptionId,
                RenewalDate = user.RenewalDate,
                SentAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
            sent++;
            logger.LogInformation("RenewalReminder: sent to user {UserId} for renewal {RenewalDate:yyyy-MM-dd}", user.Id, user.RenewalDate);
        }

        // A renewal inside its final legal month with no logged send means the window is about to be missed.
        var lastCall = now + TimeSpan.FromDays(35);
        var missed = await context.Users
                                  .Where(u => u.StripeSubscriptionActive &&
                                              !u.StripeCancelAtPeriodEnd &&
                                              u.SubscriptionPlan == SubscriptionPlan.Yearly &&
                                              u.SubscriptionPeriodEnd != null &&
                                              u.SubscriptionPeriodEnd > now &&
                                              u.SubscriptionPeriodEnd <= lastCall)
                                  .Select(u => new { u.Id, RenewalDate = u.SubscriptionPeriodEnd!.Value })
                                  .ToListAsync();
        foreach (var user in missed)
        {
            var logged = await context.BillingEmailLogs
                                      .AnyAsync(l => l.UserId == user.Id &&
                                                     l.Kind == BillingEmailKind.RenewalReminder &&
                                                     l.RenewalDate == user.RenewalDate);
            if (!logged)
                await alerts.RaiseAsync($"renewal-reminder-window:{user.Id}",
                                        "L215-1 renewal reminder window closing",
                                        $"User {user.Id} renews {user.RenewalDate:yyyy-MM-dd} and has no logged reminder. " +
                                        "The legal send window (renewal minus 3 months to minus 1 month) is about to close.");
        }

        if (sent > 0)
            logger.LogInformation("RenewalReminder: sent {Count} reminder(s)", sent);
    }
}
