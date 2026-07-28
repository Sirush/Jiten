using Jiten.Core.Data.Billing;
using Jiten.Core.Services;
using Microsoft.Extensions.Options;

namespace Jiten.Api.Services;

/// <summary>A tier and its effective allowance; a zero <c>MaxBytes</c> closes uploads while leaving existing media readable.</summary>
public readonly record struct CardMediaQuota(JitenPlusTier Tier, long MaxBytes)
{
    public bool CanUpload => MaxBytes > 0;
}

/// <summary>
/// Resolves a user's effective card-media allowance. Every quota read goes through here, so a future
/// per-user allowance (storage pack, one-off admin bump) only has to be added in one place.
/// </summary>
public interface ICardMediaQuotaService
{
    Task<CardMediaQuota> GetQuotaAsync(string userId, CancellationToken ct = default);
}

public sealed class CardMediaQuotaService(
    IJitenPlusService jitenPlusService,
    IOptionsMonitor<CardMediaStorageOptions> options) : ICardMediaQuotaService
{
    public async Task<CardMediaQuota> GetQuotaAsync(string userId, CancellationToken ct = default)
    {
        var tier = await jitenPlusService.GetTierAsync(userId, ct);
        return new CardMediaQuota(tier, options.CurrentValue.ForTier(tier));
    }
}
