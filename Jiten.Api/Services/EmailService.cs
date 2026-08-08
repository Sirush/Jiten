using System.Linq;
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

    // The billing confirmations restate the sale on a durable medium as CGV art. 5.4 requires: plan, amount,
    // frequency, renewal or access date, the art. 9.2 declaration given at checkout, and the CGV version accepted.
    public async Task SendSubscriptionConfirmedAsync(string? email, Jiten.Core.Data.Billing.SubscriptionPlan? plan, DateTime? renewsAt,
                                                     long amountCents, string cgvVersion)
    {
        if (string.IsNullOrEmpty(email)) return;
        var yearly = plan == Jiten.Core.Data.Billing.SubscriptionPlan.Yearly;
        var planName = yearly ? "yearly" : "monthly";
        var renewalLine = renewsAt.HasValue
            ? $"<br/>Renews automatically on {renewsAt.Value:d MMMM yyyy}, then {(yearly ? "every 12 months" : "every month")}, until cancelled."
            : $"<br/>Renews automatically {(yearly ? "every 12 months" : "every month")} until cancelled.";
        await SendEmailAsync(email, "Jiten+ - Subscription confirmed",
                             $"Your Jiten+ {planName} subscription is active. Thank you for supporting Jiten." +
                             $"<br/><br/>Plan: Jiten+ {planName} — {FormatEur(amountCents)} {(yearly ? "per year" : "per month")}." +
                             renewalLine +
                             "<br/><br/>At checkout you gave the express declaration that you request immediate access to Jiten+ " +
                             "and acknowledge losing your 14-day right of withdrawal once access begins (Terms of Sale, article 9)." +
                             $"<br/>The Terms of Sale you accepted (version {cgvVersion}): {SiteUrl}/cgv" +
                             $"<br/><br/>You can manage or cancel your subscription any time at {SiteUrl}/settings/subscription. " +
                             "Cancellation takes effect at the end of the paid period, and Jiten+ stays fully available until then.");
    }

    public async Task SendSubscriptionPaymentFailedAsync(string? email)
    {
        if (string.IsNullOrEmpty(email)) return;
        await SendEmailAsync(email, "Jiten+ - Payment failed",
                             "We couldn't process your latest Jiten+ payment. No action is needed right now — Stripe will " +
                             "retry automatically over the next few days, and your access continues in the meantime." +
                             "<br/>If you'd like to update your payment method, you can do so from your account settings.");
    }

    public async Task SendSubscriptionEndedAsync(string? email)
    {
        if (string.IsNullOrEmpty(email)) return;
        await SendEmailAsync(email, "Jiten+ - Subscription ended",
                             "Your Jiten+ subscription has ended and your access to Jiten+ features has stopped." +
                             "<br/>Anything you stored while subscribed is kept safe, and you can resubscribe any time from your account settings.");
    }

    public async Task SendLifetimeConfirmedAsync(string? email, long amountCents, string cgvVersion)
    {
        if (string.IsNullOrEmpty(email)) return;
        await SendEmailAsync(email, "Jiten+ - Lifetime access confirmed",
                             "Your Jiten+ lifetime access is now active. Thank you for supporting Jiten." +
                             $"<br/><br/>Purchase: Jiten+ lifetime — {FormatEur(amountCents)}, one-time payment, no renewal. " +
                             "If an upgrade credit from a running subscription applied, your Stripe receipt shows the exact amount charged." +
                             "<br/>Lifetime access is tied to this account and never expires." +
                             "<br/><br/>At checkout you gave the express declaration that you request immediate access to Jiten+ " +
                             "and acknowledge losing your 14-day right of withdrawal once access begins (Terms of Sale, article 9)." +
                             $"<br/>The Terms of Sale you accepted (version {cgvVersion}): {SiteUrl}/cgv");
    }

    public async Task SendRenewalReminderAsync(string? email, DateTime renewalDate, long amountCents)
    {
        if (string.IsNullOrEmpty(email)) return;
        await SendEmailAsync(email, $"Jiten+ - Your yearly plan renews on {renewalDate:d MMMM yyyy}",
                             $"Your Jiten+ yearly subscription renews automatically on {renewalDate:d MMMM yyyy} for {FormatEur(amountCents)}." +
                             $"<br/><br/>If you do not wish to renew, cancel any time before that date at {SiteUrl}/settings/subscription — " +
                             "online, in a minute, no contact with support needed. Cancellation takes effect at the end of the paid " +
                             "period, and Jiten+ stays fully available until then." +
                             "<br/>If you are happy to continue, there is nothing to do." +
                             "<br/><br/>This reminder is sent in accordance with article L215-1 of the French Consumer Code " +
                             "(Terms of Sale, article 7.2).");
    }

    /// <summary>Exposed separately so the admin dashboard can preview the exact email before sending.</summary>
    public static (string Subject, string Html) BuildTermsChangeNotice(DateTime renewalDate, string cgvVersion) =>
        ("Jiten+ - Updated Terms of Sale",
         $"We have published updated Terms of Sale for Jiten+ (version {cgvVersion}): {SiteUrl}/cgv" +
         $"<br/><br/>They apply to your subscription from your next renewal, on {renewalDate:d MMMM yyyy}. " +
         "Until then, nothing changes." +
         "<br/>They cover renewal, cancellation, refunds, your right of withdrawal, and a free consumer " +
         "mediator you can turn to if we cannot resolve a complaint. Nothing changes about what you pay or what you get." +
         $"<br/><br/>If you do not accept them, you can cancel free of charge before the renewal at " +
         $"{SiteUrl}/settings/subscription. Your access continues to the end of the paid period, and nothing " +
         "you uploaded is ever deleted." +
         "<br/><br/>This notice is sent in accordance with article 12.4 of the Terms of Sale.");

    public async Task SendTermsChangeNoticeAsync(string? email, DateTime renewalDate, string cgvVersion)
    {
        if (string.IsNullOrEmpty(email)) return;
        var (subject, html) = BuildTermsChangeNotice(renewalDate, cgvVersion);
        await SendEmailAsync(email, subject, html);
    }

    private static string FormatEur(long cents) =>
        cents % 100 == 0 ? $"{cents / 100} EUR" : $"{cents / 100m:0.00} EUR";

    public async Task SendPromoRedeemedAsync(string? email, int days, bool grantsFullTier)
    {
        if (string.IsNullOrEmpty(email)) return;
        var tierLine = grantsFullTier
            ? "This unlocks Jiten+ in full."
            : "This unlocks the Jiten+ trial: every Jiten+ feature, with a smaller storage allowance for card images and audio.";
        await SendEmailAsync(email, "Jiten+ - Code redeemed",
                             $"Your code is active: you now have {days} day{(days == 1 ? "" : "s")} of Jiten+." +
                             $"<br/>{tierLine}" +
                             "<br/>Your Jiten+ days only count down while you don't have an active paid subscription.");
    }

    public async Task SendPromoAccessEndsTomorrowAsync(string? email)
    {
        if (string.IsNullOrEmpty(email)) return;
        await SendEmailAsync(email, "Jiten+ - Your access ends tomorrow",
                             "Your Jiten+ access from promo credit ends tomorrow." +
                             "<br/>Subscribe any time from your settings to keep your Jiten+ features. All your stored data stays safe either way.");
    }

    public async Task SendJitenPlusGrantAsync(string? email, bool isLifetime, int? days, string? thankYouMessage)
    {
        if (string.IsNullOrEmpty(email)) return;
        var grantLine = isLifetime
            ? "You've been given Jiten+ lifetime access as a thank-you."
            : $"You've been given {days} day{(days == 1 ? "" : "s")} of Jiten+ as a thank-you.";
        var body = grantLine;
        if (!string.IsNullOrWhiteSpace(thankYouMessage))
            body += $"<br/><br/>{FormatMessageHtml(thankYouMessage)}";
        await SendEmailAsync(email, "Jiten+ - A gift for you", body);
    }

    /// <summary>
    /// Renders the limited formatting subset used for admin-authored thank-you messages:
    /// **bold**, *italic*, "- " bullet lists, and preserved line breaks. Mirrors the frontend's
    /// parseCustomMeaningHtml so the email and the in-app notification look the same. Text is
    /// HTML-encoded FIRST (defense in depth — messages are admin-authored but not markup), then only
    /// the app-controlled marker transforms below emit tags.
    /// </summary>
    internal static string FormatMessageHtml(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        static string Inline(string s) =>
            System.Text.RegularExpressions.Regex.Replace(
                System.Text.RegularExpressions.Regex.Replace(s, @"\*\*([^*\n]+)\*\*", "<strong>$1</strong>"),
                @"\*([^*\n]+)\*", "<em>$1</em>");

        var blocks = new List<string>();
        var textLines = new List<string>();
        var listItems = new List<string>();

        void FlushText()
        {
            if (textLines.Count > 0)
            {
                blocks.Add(string.Join("<br/>", textLines));
                textLines.Clear();
            }
        }

        void FlushList()
        {
            if (listItems.Count > 0)
            {
                blocks.Add("<ul>" + string.Concat(listItems.Select(i => $"<li>{i}</li>")) + "</ul>");
                listItems.Clear();
            }
        }

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var escaped = HtmlEncoder.Default.Encode(rawLine);
            var listMatch = System.Text.RegularExpressions.Regex.Match(escaped, @"^\s*-\s+(.*)$");
            if (listMatch.Success)
            {
                FlushText();
                listItems.Add(Inline(listMatch.Groups[1].Value));
            }
            else
            {
                FlushList();
                textLines.Add(Inline(escaped));
            }
        }

        FlushText();
        FlushList();

        return string.Concat(blocks);
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