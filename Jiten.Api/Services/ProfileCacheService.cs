using System.Text.Json;
using Jiten.Core.Data;
using StackExchange.Redis;

namespace Jiten.Api.Services;

/// <summary>With <c>PerViewer</c>, each viewer kind gets its own entry, since what each may see differs.</summary>
public sealed record ProfileCacheKind(string Name, TimeSpan Ttl, bool PerViewer)
{
    public static readonly ProfileCacheKind Stats = new("profile-stats", TimeSpan.FromMinutes(10), PerViewer: true);
    public static readonly ProfileCacheKind Genres = new("profile-genres", TimeSpan.FromMinutes(10), PerViewer: true);

    /// <summary>Counting loads every FSRS card for the user, and the profile endpoints are anonymous and crawlable.</summary>
    public static readonly ProfileCacheKind Vocabulary = new("profile-vocab", TimeSpan.FromMinutes(5), PerViewer: false);

    public static readonly ProfileCacheKind KanjiCount = new("profile-kanji-count", TimeSpan.FromMinutes(10), PerViewer: false);
    public static readonly ProfileCacheKind DifficultyVotes = new("profile-votes", TimeSpan.FromMinutes(30), PerViewer: false);

    /// <summary>Versioned by the coverage stamp rather than the generation, since coverage recomputes never save profile rows.</summary>
    public static readonly ProfileCacheKind Readable = new("profile-readable", TimeSpan.FromMinutes(10), PerViewer: false);
}

public interface IProfileCacheService
{
    /// <summary>Reads <paramref name="kinds"/> for one profile in two round trips: the owner's generation, then every entry at once.</summary>
    Task<ProfileCacheBatch> ReadAsync(ProfileContext access, params ProfileCacheKind[] kinds);

    /// <summary>For values that change with <paramref name="version"/> instead of the generation.</summary>
    Task<T> GetOrSetAsync<T>(ProfileCacheKind kind, string userId, string version, Func<Task<T>> compute);

    /// <summary>Every key embeds the generation, so bumping it strands all of the user's cached entries at once.</summary>
    Task BumpGenerationAsync(IReadOnlyCollection<string> userIds);
}

/// <summary>Redis outages are tolerated: reads miss, writes are dropped, and values are computed as if uncached.</summary>
public class ProfileCacheService(IConnectionMultiplexer redis, ILogger<ProfileCacheService> logger) : IProfileCacheService
{
    /// <summary>Outlives every entry's TTL, so a generation that expired and restarted at zero never meets an old zero-generation entry.</summary>
    private static readonly TimeSpan GenerationTtl = TimeSpan.FromDays(7);

    private static string GenerationKey(string userId) => $"jiten:profile-gen:{userId}";

    private static string Key(ProfileCacheKind kind, string userId, string version, ProfileViewer? viewer = null) =>
        viewer is { } v ? $"jiten:{kind.Name}:{userId}:{version}:{ProfilePrivacy.Name(v)}" : $"jiten:{kind.Name}:{userId}:{version}";

    public async Task<ProfileCacheBatch> ReadAsync(ProfileContext access, params ProfileCacheKind[] kinds)
    {
        if (kinds.Length == 0)
            return new ProfileCacheBatch(this, []);

        try
        {
            var db = redis.GetDatabase();
            var generation = ((long?)await db.StringGetAsync(GenerationKey(access.UserId)) ?? 0).ToString();
            var keys = kinds.Select(kind => Key(kind, access.UserId, generation, kind.PerViewer ? access.Viewer : null)).ToArray();
            var values = await db.StringGetAsync(keys.Select(key => (RedisKey)key).ToArray());
            return new ProfileCacheBatch(this, kinds.Select((kind, i) => (kind, keys[i], values[i]))
                                                    .ToDictionary(e => e.kind, e => (e.Item2, e.Item3)));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed reading the profile cache of {UserId}", access.UserId);
            return new ProfileCacheBatch(this, null);
        }
    }

    public async Task<T> GetOrSetAsync<T>(ProfileCacheKind kind, string userId, string version, Func<Task<T>> compute)
    {
        var key = Key(kind, userId, version);
        try
        {
            if (TryDeserialize<T>(await redis.GetDatabase().StringGetAsync(key), out var hit))
                return hit;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed reading profile cache {CacheKey}", key);
        }

        var value = await compute();
        await WriteAsync(key, value, kind.Ttl);
        return value;
    }

    public async Task BumpGenerationAsync(IReadOnlyCollection<string> userIds)
    {
        if (userIds.Count == 0)
            return;

        try
        {
            var db = redis.GetDatabase();
            await Task.WhenAll(userIds.Select(async userId =>
            {
                await db.StringIncrementAsync(GenerationKey(userId));
                await db.KeyExpireAsync(GenerationKey(userId), GenerationTtl, CommandFlags.FireAndForget);
            }));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed bumping the profile cache generation of {UserIds}", string.Join(",", userIds));
        }
    }

    internal async Task WriteAsync<T>(string key, T value, TimeSpan ttl)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(key, JsonSerializer.SerializeToUtf8Bytes(value), ttl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed writing profile cache {CacheKey}", key);
        }
    }

    /// <summary>An entry written by an older shape of <typeparamref name="T"/> reads as a miss.</summary>
    internal static bool TryDeserialize<T>(RedisValue cached, out T value)
    {
        value = default!;
        if (cached.IsNullOrEmpty)
            return false;

        try
        {
            if (JsonSerializer.Deserialize<T>((byte[])cached!) is not { } hit)
                return false;
            value = hit;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed class ProfileCacheBatch
{
    private readonly ProfileCacheService _cache;
    /// <summary>Null when Redis could not be read.</summary>
    private readonly Dictionary<ProfileCacheKind, (string Key, RedisValue Value)>? _entries;

    internal ProfileCacheBatch(ProfileCacheService cache, Dictionary<ProfileCacheKind, (string Key, RedisValue Value)>? entries)
    {
        _cache = cache;
        _entries = entries;
    }

    /// <summary>Throws for a kind the batch was not read with.</summary>
    public async Task<T> GetOrSetAsync<T>(ProfileCacheKind kind, Func<Task<T>> compute)
    {
        if (_entries == null)
            return await compute();
        if (!_entries.TryGetValue(kind, out var entry))
            throw new ArgumentException($"The batch was not read with {kind.Name}.", nameof(kind));
        if (ProfileCacheService.TryDeserialize<T>(entry.Value, out var hit))
            return hit;

        var value = await compute();
        await _cache.WriteAsync(entry.Key, value, kind.Ttl);
        return value;
    }
}
