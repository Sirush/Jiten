using Jiten.Api.Services;
using Jiten.Api.Services.Legal;
using Jiten.Core.Data.Billing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Jiten.Api.Controllers;

public partial class AdminController
{
    /// <summary>
    /// One-shot CGV art. 12.2 written notice to recurring paid subscribers that updated terms apply from
    /// their next renewal. Idempotent per (user, renewal date); re-run safely after a new subscriber appears
    /// or after a terms bump. Subscribers who already cancelled are excluded — they rejected renewal.
    /// Grant recipients can never be selected: StripeSubscriptionActive is only ever set from a real Stripe
    /// subscription (webhook/reconcile), while grants live in promo credits or IsLifetime.
    /// </summary>
    [HttpPost("legal/terms-change-notices")]
    public async Task<IActionResult> SendTermsChangeNotices(
        [FromQuery] bool dryRun,
        [FromServices] IEmailService emailService,
        [FromServices] IOptions<LegalDocumentsOptions> legalOptions)
    {
        var subscribers = await userContext.Users
                                           .Where(u => u.StripeSubscriptionActive &&
                                                       !u.StripeCancelAtPeriodEnd &&
                                                       !u.IsLifetime &&
                                                       u.SubscriptionPeriodEnd != null &&
                                                       u.SubscriptionPeriodEnd > DateTime.UtcNow)
                                           .Select(u => new
                                           {
                                               u.Id, u.Email, u.UserName, u.SubscriptionPlan, u.StripeSubscriptionId,
                                               RenewalDate = u.SubscriptionPeriodEnd!.Value
                                           })
                                           .ToListAsync();

        var version = legalOptions.Value.CgvVersion;
        var results = new List<object>();
        var sent = 0;
        var skipped = 0;

        foreach (var sub in subscribers)
        {
            var (subject, html) = EmailService.BuildTermsChangeNotice(sub.RenewalDate, version);

            var existingLog = await userContext.BillingEmailLogs
                                               .AsNoTracking()
                                               .FirstOrDefaultAsync(l => l.UserId == sub.Id &&
                                                                         l.Kind == BillingEmailKind.TermsChangeNotice &&
                                                                         l.RenewalDate == sub.RenewalDate);
            if (existingLog is not null)
            {
                skipped++;
                results.Add(new
                {
                    sub.UserName, sub.Email, sub.RenewalDate, plan = sub.SubscriptionPlan?.ToString(),
                    status = "already-sent", sentAt = (DateTime?)existingLog.SentAt, emailSubject = subject, emailHtml = html
                });
                continue;
            }

            if (!dryRun)
            {
                await emailService.SendTermsChangeNoticeAsync(sub.Email, sub.RenewalDate, version);
                userContext.BillingEmailLogs.Add(new BillingEmailLog
                {
                    UserId = sub.Id,
                    Kind = BillingEmailKind.TermsChangeNotice,
                    SubscriptionId = sub.StripeSubscriptionId,
                    RenewalDate = sub.RenewalDate,
                    SentAt = DateTime.UtcNow
                });
                await userContext.SaveChangesAsync();
                sent++;
            }

            results.Add(new
            {
                sub.UserName, sub.Email, sub.RenewalDate, plan = sub.SubscriptionPlan?.ToString(),
                status = dryRun ? "would-send" : "sent", sentAt = (DateTime?)(dryRun ? null : DateTime.UtcNow),
                emailSubject = subject, emailHtml = html
            });
            logger.LogInformation("TermsChangeNotice: {Status} for user {UserId}, renewal {RenewalDate:yyyy-MM-dd}",
                                  dryRun ? "dry-run" : "sent", sub.Id, sub.RenewalDate);
        }

        return Ok(new { dryRun, version, sent, skipped, subscribers = results });
    }
}
