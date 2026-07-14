using Jiten.Core;
using Jiten.Core.Data.Billing;
using Jiten.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Services;

public class JitenPlusService(UserDbContext userContext, IMemoryCache cache) : IJitenPlusService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private static string CacheKey(string userId) => $"jitenplus:status:{userId}";

    public async Task<JitenPlusStatus> GetStatusAsync(string userId, CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey(userId), out JitenPlusStatus? cached) && cached is not null)
            return cached;

        var status = await BuildStatusAsync(userId, ct);
        cache.Set(CacheKey(userId), status, CacheDuration);
        return status;
    }

    public async Task<JitenPlusTier> GetTierAsync(string userId, CancellationToken ct = default) =>
        (await GetStatusAsync(userId, ct)).Tier;

    public void InvalidateTier(string userId) => cache.Remove(CacheKey(userId));

    private async Task<JitenPlusStatus> BuildStatusAsync(string userId, CancellationToken ct)
    {
        var user = await userContext.Users
                                    .AsNoTracking()
                                    .Where(u => u.Id == userId)
                                    .Select(u => new
                                    {
                                        u.StripeSubscriptionActive,
                                        u.SubscriptionPlan,
                                        u.SubscriptionPeriodEnd,
                                        u.IsLifetime,
                                        u.LifetimeSource,
                                        u.AdminPremiumOverride
                                    })
                                    .FirstOrDefaultAsync(ct);

        if (user is null)
        {
            return new JitenPlusStatus(JitenPlusTier.None, false, null, null, false, null, 0, [], false);
        }

        // GrantsFullTier is denormalised onto the credit (copied from the code at redemption, or set on an
        // admin grant), so no PromoCode join is needed and admin grants with a null PromoCodeId resolve too.
        var creditInfos = await userContext.UserPromoCredits
                                           .AsNoTracking()
                                           .Where(c => c.UserId == userId && c.RemainingDays > 0)
                                           .OrderBy(c => c.GrantedAt)
                                           .Select(c => new PromoCreditInfo(
                                               c.UserPromoCreditId,
                                               c.RemainingDays,
                                               c.GrantsFullTier,
                                               c.GrantedAt,
                                               c.ThankYouMessage))
                                           .ToListAsync(ct);

        var input = new JitenPlusTierResolver.Input(
            user.StripeSubscriptionActive,
            user.SubscriptionPeriodEnd,
            user.IsLifetime,
            user.AdminPremiumOverride,
            creditInfos.Select(c => new JitenPlusTierResolver.PromoCreditSnapshot(c.RemainingDays, c.GrantsFullTier)).ToList());

        var now = DateTime.UtcNow;
        var tier = JitenPlusTierResolver.Resolve(input, now);

        return new JitenPlusStatus(
            tier,
            JitenPlusTierResolver.IsSubscriptionActive(input, now),
            user.SubscriptionPlan,
            user.SubscriptionPeriodEnd,
            user.IsLifetime,
            user.LifetimeSource,
            creditInfos.Sum(c => c.RemainingDays),
            creditInfos,
            user.AdminPremiumOverride);
    }
}
