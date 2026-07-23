using Jiten.Core.Data.Billing;

namespace Jiten.Api.Services;

public record PromoCreditInfo(
    long UserPromoCreditId,
    int RemainingDays,
    bool GrantsFullTier,
    DateTime GrantedAt,
    string? ThankYouMessage);

/// <summary>Resolved tier plus the source breakdown that produced it.</summary>
public record JitenPlusStatus(
    JitenPlusTier Tier,
    bool SubscriptionActive,
    SubscriptionPlan? Plan,
    DateTime? PeriodEnd,
    bool IsLifetime,
    LifetimeSource? LifetimeSource,
    int PromoCreditDays,
    IReadOnlyList<PromoCreditInfo> Credits,
    bool AdminOverride);

public interface IJitenPlusService
{
    Task<JitenPlusStatus> GetStatusAsync(string userId, CancellationToken ct = default);

    Task<JitenPlusTier> GetTierAsync(string userId, CancellationToken ct = default);

    /// <summary>Drop the cached tier for a user.</summary>
    void InvalidateTier(string userId);
}
