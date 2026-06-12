using System.Text;
using System.Text.Encodings.Web;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Identity.UI.Services;
using MimeKit;

namespace Jiten.Api.Services;

public class EmailService : IEmailSender, IEmailService
{
    private const string SiteUrl = "https://jiten.moe";

    public async Task SendEmailConfirmationAsync(string email, string userId, string encodedCode)
    {
        var callbackUrl = $"{SiteUrl}/confirm-email?userId={userId}&code={encodedCode}";
        await SendEmailAsync(email, "Jiten - Confirm your email",
                             $"Please confirm your account by <a href='{HtmlEncoder.Default.Encode(callbackUrl)}'>clicking here</a>." +
                             $"<br/>Do not share this link with anyone.<br/>If you did not request an account creation, please ignore this email.");
    }

    public async Task SendChangeEmailConfirmationAsync(string newEmail, string userId, string encodedCode)
    {
        var callbackUrl = $"{SiteUrl}/confirm-email-change?userId={userId}&email={UrlEncoder.Default.Encode(newEmail)}&code={encodedCode}";
        await SendEmailAsync(newEmail, "Jiten - Confirm your new email",
                             $"Please confirm your new email address on Jiten.moe by <a href='{HtmlEncoder.Default.Encode(callbackUrl)}'>clicking here</a>." +
                             $"<br/>Do not share this link with anyone.<br/>If you did not request an email change, please ignore this email.");
    }

    public async Task SendEmailChangeNoticeAsync(string oldEmail, string newEmail)
    {
        await SendEmailAsync(oldEmail, "Jiten - Email change requested",
                             $"A change of your account email to {HtmlEncoder.Default.Encode(newEmail)} was requested." +
                             $"<br/>If this wasn't you, please reset your password immediately.");
    }

    public async Task SendEmailChangedAwayNoticeAsync(string oldEmail, string newEmail)
    {
        await SendEmailAsync(oldEmail, "Jiten - Your account email was changed",
                             $"Your Jiten.moe account email was changed to {HtmlEncoder.Default.Encode(newEmail)}." +
                             $"<br/>This address is no longer associated with the account." +
                             $"<br/>If you did not request this change, please contact support immediately.");
    }

    public async Task SendEmailChangedConfirmationAsync(string newEmail)
    {
        await SendEmailAsync(newEmail, "Jiten - Your email was changed",
                             $"This address is now the email for your Jiten.moe account." +
                             $"<br/>If you did not request this change, please contact support immediately.");
    }

    public async Task SendPasswordChangedNoticeAsync(string email)
    {
        await SendEmailAsync(email, "Jiten - Your password was changed",
                             "Your Jiten.moe account password was just changed." +
                             "<br/>If this wasn't you, please reset your password immediately.");
    }

    public async Task SendPasswordSetNoticeAsync(string email)
    {
        await SendEmailAsync(email, "Jiten - A password was added to your account",
                             "A password was just added to your Jiten.moe account. You can now sign in with your email and password in addition to Google." +
                             "<br/>If this wasn't you, please reset your password immediately.");
    }

    private readonly IConfiguration _configuration;

    public EmailService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        var message = new MimeMessage();
        var fromName = "Jiten";
        var fromEmail = _configuration["Email:From"] ?? "noreply@example.com";
        message.From.Add(new MailboxAddress(fromName, fromEmail));
        message.To.Add(MailboxAddress.Parse(email));
        message.Subject = subject;

        var builder = new BodyBuilder { HtmlBody = htmlMessage, TextBody = StripHtml(htmlMessage) };
        message.Body = builder.ToMessageBody();


        await SendViaSmtp(message,
                          host: _configuration["Email:SmtpHost"] ?? "smtp.eu.mailgun.org",
                          port: int.TryParse(_configuration["Email:SmtpPort"], out var sp) ? sp : 587,
                          username: _configuration["Email:Username"],
                          password: _configuration["Email:Password"],
                          useStartTls: true);
    }

    private static async Task SendViaSmtp(MimeMessage message, string host, int port, string? username, string? password, bool useStartTls)
    {
        using var client = new SmtpClient();
        var secure = useStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
        await client.ConnectAsync(host, port, secure);

        if (!string.IsNullOrWhiteSpace(username))
        {
            await client.AuthenticateAsync(username, password);
        }

        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var sb = new StringBuilder(html.Length);
        bool inside = false;
        foreach (var ch in html)
        {
            if (ch == '<') inside = true;
            else if (ch == '>') inside = false;
            else if (!inside) sb.Append(ch);
        }

        return sb.ToString();
    }
}