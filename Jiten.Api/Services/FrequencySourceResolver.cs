using Hangfire;
using Jiten.Api.Helpers;
using Jiten.Api.Jobs;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Services;

public interface IFrequencySourceResolver
{
    /// <summary>The current caller's default ranking; global for anonymous callers and for a stale stored source.</summary>
    Task<FrequencyScope> Resolve();

    Task<FrequencyScope> Resolve(string? userId);

    Task<ScopedFormFrequencies> LoadFrequencies(JitenDbContext context, List<int> wordIds);

    Task<ScopedFormFrequencies> LoadFrequencies(JitenDbContext context, List<int> wordIds, FrequencyScope scope);

    Task<IReadOnlyDictionary<long, int>> ListRanks(long listId);
}

/// <summary>
/// Reads the per-user default frequency source out of the study-settings blob. Rank display runs on nearly every
/// word list, so the lookup is cached briefly instead of hitting UserFsrsSettings per request; the settings PUT and
/// the custom-list write paths invalidate it.
/// </summary>
public class FrequencySourceResolver(
    UserDbContext userContext,
    ICurrentUserService currentUserService,
    IDeckWordResolver deckWordResolver,
    IMemoryCache memoryCache) : IFrequencySourceResolver
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    public Task<FrequencyScope> Resolve() => Resolve(currentUserService.UserId);

    public async Task<FrequencyScope> Resolve(string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return default;

        if (memoryCache.TryGetValue(CacheKey(userId), out FrequencyScope cached))
            return cached;

        var scope = await Load(userId);
        memoryCache.Set(CacheKey(userId), scope, CacheDuration);
        return scope;
    }

    public async Task<ScopedFormFrequencies> LoadFrequencies(JitenDbContext context, List<int> wordIds)
        => await LoadFrequencies(context, wordIds, await Resolve());

    public async Task<ScopedFormFrequencies> LoadFrequencies(JitenDbContext context, List<int> wordIds, FrequencyScope scope)
    {
        var global = await WordFormHelper.LoadWordFormFrequencies(context, wordIds);

        if (scope.MediaType.HasValue)
        {
            var byType = await WordFormHelper.LoadWordFormFrequencies(context, wordIds, scope.MediaType);
            return new ScopedFormFrequencies(scope, global, byType, null);
        }

        if (scope.FrequencyListId.HasValue)
            return new ScopedFormFrequencies(scope, global, null, await ListRanks(scope.FrequencyListId.Value));

        return new ScopedFormFrequencies(scope, global, null, null);
    }

    public Task<IReadOnlyDictionary<long, int>> ListRanks(long listId) => deckWordResolver.GetListRankMap(listId);

    public static void Invalidate(IMemoryCache cache, string userId) => cache.Remove(CacheKey(userId));

    /// <summary>The stored source is only a hint: a deleted, un-saved, or not-yet-packed list silently means global,
    /// which is also how a lapsed Jiten Plus subscription degrades.</summary>
    private async Task<FrequencyScope> Load(string userId)
    {
        var settingsJson = await userContext.UserFsrsSettings
                                            .AsNoTracking()
                                            .Where(s => s.UserId == userId)
                                            .Select(s => s.SettingsJson)
                                            .FirstOrDefaultAsync();

        if (string.IsNullOrEmpty(settingsJson) || settingsJson == "{}") return default;

        var settings = FsrsSettingsHelper.GetStudySettings(new UserFsrsSettings { UserId = userId, SettingsJson = settingsJson });

        if (settings.DefaultFrequencyListId is > 0)
        {
            var listId = settings.DefaultFrequencyListId.Value;
            var usable = await userContext.UserFrequencyLists
                                          .AsNoTracking()
                                          .AnyAsync(f => f.Id == listId && f.UserId == userId && f.IsSaved && f.RankedWordsBlob != null);
            return usable ? new FrequencyScope(null, listId) : default;
        }

        if (settings.DefaultFrequencyMediaType is > 0 &&
            Enum.IsDefined(typeof(MediaType), settings.DefaultFrequencyMediaType.Value))
            return new FrequencyScope((MediaType)settings.DefaultFrequencyMediaType.Value, null);

        return default;
    }

    private static string CacheKey(string userId) => $"frequency-source:{userId}";
}

/// <summary>The one gate on a frequency source arriving from a client, shared by study decks and the account default.</summary>
public static class FrequencySourceValidator
{
    public static async Task<IResult?> Validate(UserDbContext userContext, IBackgroundJobClient backgroundJobs,
                                                string userId, int? frequencyMediaType, long? frequencyListId)
    {
        if (frequencyMediaType.HasValue && frequencyListId.HasValue)
            return Results.BadRequest("Choose either a media type or a custom list, not both.");

        if (frequencyMediaType.HasValue && !Enum.IsDefined(typeof(MediaType), frequencyMediaType.Value))
            return Results.BadRequest("Unknown media type.");

        if (!frequencyListId.HasValue) return null;

        var list = await userContext.UserFrequencyLists.AsNoTracking()
                                    .Where(f => f.Id == frequencyListId.Value && f.UserId == userId)
                                    .Select(f => new { f.IsSaved, HasBlob = f.RankedWordsBlob != null })
                                    .FirstOrDefaultAsync();

        if (list is null || !list.IsSaved)
            return Results.BadRequest("Frequency list not found.");

        if (!list.HasBlob)
        {
            backgroundJobs.Enqueue<FrequencyListJob>(j => j.Generate(frequencyListId.Value));
            return Results.Json(new { error = "This list is still being prepared for study. Try again in a minute." },
                                statusCode: StatusCodes.Status409Conflict);
        }

        return null;
    }
}
