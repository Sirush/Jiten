using Jiten.Core.Data.Billing;
using Jiten.Core.Services;
using Microsoft.Extensions.Options;

namespace Jiten.Api.Services;

/// <summary>A user's collection limits, resolved against their tier but keeping the Jiten+ values
/// available so a free user can be told what they would gain.</summary>
public sealed record UserLimits(JitenPlusTier Tier, JitenPlusLimitsOptions Allowances)
{
    public bool IsPlus => Tier != JitenPlusTier.None;

    public int StudyDecks => Allowances.StudyDecks.ForTier(Tier);
    public int StudyDeckWords => Allowances.StudyDeckWords.ForTier(Tier);
    public int ImportWords => Allowances.ImportWords.ForTier(Tier);
    public int ActiveMediaRequests => Allowances.ActiveMediaRequests.ForTier(Tier);
    public int CustomSentencesPerWord => Allowances.CustomSentencesPerWord.ForTier(Tier);
    public int Roadmaps => Allowances.Roadmaps.ForTier(Tier);
}

/// <summary>
/// Resolves a user's effective limits. Every limit check goes through here, so a future per-user
/// override (admin bump, reward) only has to be added in one place.
/// </summary>
public interface IUserLimitsService
{
    Task<UserLimits> GetLimitsAsync(string userId, CancellationToken ct = default);
}

public sealed class UserLimitsService(
    IJitenPlusService jitenPlusService,
    IOptionsMonitor<JitenPlusLimitsOptions> options) : IUserLimitsService
{
    public async Task<UserLimits> GetLimitsAsync(string userId, CancellationToken ct = default)
    {
        var tier = await jitenPlusService.GetTierAsync(userId, ct);
        return new UserLimits(tier, options.CurrentValue);
    }
}
