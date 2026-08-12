using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Helpers;

/// <summary>Resolves a user's enabled derivation categories. <c>GetKnownWordsState</c> runs on every word list,
/// so the lookup is cached briefly instead of reading UserFsrsSettings per request; the write path invalidates it.</summary>
public static class DerivationSettingsHelper
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private static readonly IReadOnlySet<DerivationCategory> None = new HashSet<DerivationCategory>();

    public static async Task<IReadOnlySet<DerivationCategory>> GetEnabledCategories(
        IMemoryCache cache, UserDbContext userContext, string userId)
    {
        if (cache.TryGetValue(CacheKey(userId), out IReadOnlySet<DerivationCategory>? cached) && cached != null)
            return cached;

        var settingsJson = await userContext.UserFsrsSettings
                                            .AsNoTracking()
                                            .Where(s => s.UserId == userId)
                                            .Select(s => s.SettingsJson)
                                            .FirstOrDefaultAsync();

        var categories = string.IsNullOrEmpty(settingsJson) || settingsJson == "{}"
            ? None
            : Parse(FsrsSettingsHelper.GetStudySettings(new UserFsrsSettings
            {
                UserId = userId, SettingsJson = settingsJson
            }).DerivationalRedundancyCategories);

        cache.Set(CacheKey(userId), categories, CacheDuration);
        return categories;
    }

    public static void Invalidate(IMemoryCache cache, string userId) => cache.Remove(CacheKey(userId));

    /// <summary>Unknown or non-shipped keys are dropped, so a stale client can never enable a dormant category.</summary>
    public static HashSet<DerivationCategory> Parse(IEnumerable<string>? keys)
    {
        var result = new HashSet<DerivationCategory>();
        if (keys == null) return result;

        foreach (var key in keys)
            if (DerivationCategories.TryParseKey(key, out var category) &&
                DerivationCategories.ShippedCategories.Contains(category))
                result.Add(category);

        return result;
    }

    public static List<string> ToKeys(IEnumerable<DerivationCategory> categories)
        => categories.Select(DerivationCategories.GetKey).ToList();

    private static string CacheKey(string userId) => $"derivation-categories:{userId}";
}
