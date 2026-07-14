using Jiten.Core.Data.Billing;

namespace Jiten.Api.Services;

public interface IEmailService
{
    Task SendEmailConfirmationAsync(string email, string userId, string encodedCode);
    Task SendChangeEmailConfirmationAsync(string newEmail, string userId, string encodedCode);
    Task SendEmailChangeNoticeAsync(string oldEmail, string newEmail);
    Task SendEmailChangedAwayNoticeAsync(string oldEmail, string newEmail);
    Task SendEmailChangedConfirmationAsync(string newEmail);
    Task SendPasswordChangedNoticeAsync(string email);
    Task SendPasswordSetNoticeAsync(string email);

    // Jiten+ billing
    Task SendSubscriptionConfirmedAsync(string? email, SubscriptionPlan? plan);
    Task SendSubscriptionPaymentFailedAsync(string? email);
    Task SendSubscriptionEndedAsync(string? email);
    Task SendLifetimeConfirmedAsync(string? email);
}
