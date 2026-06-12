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
}
