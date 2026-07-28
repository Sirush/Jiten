using Jiten.Core.Data.Billing;
using Microsoft.AspNetCore.Identity;

namespace Jiten.Core.Data.Authentication;

public class User : IdentityUser
{
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime TosAcceptedAt { get; set; }
    public bool ReceivesNewsletter { get; set; }
    public RateLimitTier RateLimitTier { get; set; } = RateLimitTier.Default;

    /// <summary>Last time a password reset email was sent, used to throttle reset requests.</summary>
    public DateTime? LastPasswordResetRequestedAt { get; set; }

    /// <summary>Last time an email change was requested, used to throttle change-email requests.</summary>
    public DateTime? LastEmailChangeRequestedAt { get; set; }

    // Jiten+ billing state
    public string? StripeCustomerId { get; set; }
    public bool StripeSubscriptionActive { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public DateTime? SubscriptionPeriodEnd { get; set; }
    public SubscriptionPlan? SubscriptionPlan { get; set; }
    public bool IsLifetime { get; set; }
    public LifetimeSource? LifetimeSource { get; set; }
    public bool AdminPremiumOverride { get; set; }
}