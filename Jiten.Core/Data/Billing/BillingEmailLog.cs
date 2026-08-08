namespace Jiten.Core.Data.Billing;

public enum BillingEmailKind
{
    /// <summary>L215-1 yearly renewal reminder (CGV art. 7.2): due 1-3 months before each yearly renewal.</summary>
    RenewalReminder = 0,

    /// <summary>CGV art. 12.2 written notice that new terms apply from the subscriber's next renewal.</summary>
    TermsChangeNotice = 1
}

/// <summary>Audit log of legally required billing emails; the send is a legal obligation, so "did we send it" must be answerable.</summary>
public class BillingEmailLog
{
    public long Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public BillingEmailKind Kind { get; set; }
    public string? SubscriptionId { get; set; }

    /// <summary>The renewal the email refers to; one send per (user, kind, renewal date).</summary>
    public DateTime? RenewalDate { get; set; }

    public DateTime SentAt { get; set; }
}
