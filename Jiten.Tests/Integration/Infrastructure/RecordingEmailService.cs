using System.Collections.Concurrent;
using Jiten.Api.Services;
using Microsoft.AspNetCore.Identity.UI.Services;

namespace Jiten.Parser.Tests.Integration.Infrastructure;

/// <summary>
/// Test double for <see cref="IEmailService"/> (and <see cref="IEmailSender"/>) that records every
/// send instead of contacting SMTP. Registered as a singleton so tests can inspect what was sent.
/// Call <see cref="Clear"/> between tests for reliable assertions.
/// </summary>
public class RecordingEmailService : IEmailService, IEmailSender
{
    public record SentEmail(string Method, string Recipient, string? UserId, string? Code, string? OtherEmail, string? Subject);

    private readonly ConcurrentQueue<SentEmail> _sent = new();

    public IReadOnlyList<SentEmail> Sent => _sent.ToArray();

    public void Clear() => _sent.Clear();

    public IEnumerable<SentEmail> ForRecipient(string recipient) =>
        Sent.Where(e => string.Equals(e.Recipient, recipient, StringComparison.OrdinalIgnoreCase));

    // IEmailService

    public Task SendEmailConfirmationAsync(string email, string userId, string encodedCode)
    {
        _sent.Enqueue(new SentEmail(nameof(SendEmailConfirmationAsync), email, userId, encodedCode, null, null));
        return Task.CompletedTask;
    }

    public Task SendChangeEmailConfirmationAsync(string newEmail, string userId, string encodedCode)
    {
        _sent.Enqueue(new SentEmail(nameof(SendChangeEmailConfirmationAsync), newEmail, userId, encodedCode, null, null));
        return Task.CompletedTask;
    }

    public Task SendEmailChangeNoticeAsync(string oldEmail, string newEmail)
    {
        _sent.Enqueue(new SentEmail(nameof(SendEmailChangeNoticeAsync), oldEmail, null, null, newEmail, null));
        return Task.CompletedTask;
    }

    public Task SendEmailChangedAwayNoticeAsync(string oldEmail, string newEmail)
    {
        _sent.Enqueue(new SentEmail(nameof(SendEmailChangedAwayNoticeAsync), oldEmail, null, null, newEmail, null));
        return Task.CompletedTask;
    }

    public Task SendEmailChangedConfirmationAsync(string newEmail)
    {
        _sent.Enqueue(new SentEmail(nameof(SendEmailChangedConfirmationAsync), newEmail, null, null, null, null));
        return Task.CompletedTask;
    }

    public Task SendPasswordChangedNoticeAsync(string email)
    {
        _sent.Enqueue(new SentEmail(nameof(SendPasswordChangedNoticeAsync), email, null, null, null, null));
        return Task.CompletedTask;
    }

    public Task SendPasswordSetNoticeAsync(string email)
    {
        _sent.Enqueue(new SentEmail(nameof(SendPasswordSetNoticeAsync), email, null, null, null, null));
        return Task.CompletedTask;
    }

    // IEmailSender (used by AuthController register/forgot-password)

    public Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        _sent.Enqueue(new SentEmail(nameof(SendEmailAsync), email, null, ExtractCode(htmlMessage), null, subject));
        return Task.CompletedTask;
    }

    // Register/forgot-password embed the confirmation code in a callback URL inside the html body.
    // Pull it out so tests that drive the real register endpoint can confirm the email.
    private static string? ExtractCode(string htmlMessage)
    {
        var marker = "code=";
        var idx = htmlMessage.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        idx += marker.Length;
        var end = idx;
        while (end < htmlMessage.Length && htmlMessage[end] != '&' && htmlMessage[end] != '\'' && htmlMessage[end] != '"' &&
               htmlMessage[end] != '<' && htmlMessage[end] != ' ')
            end++;
        return htmlMessage.Substring(idx, end - idx);
    }
}
