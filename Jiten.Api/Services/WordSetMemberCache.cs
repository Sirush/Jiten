using System.Collections.Concurrent;
using System.Collections.Frozen;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace Jiten.Api.Services;

public class WordSetMemberCache(
    IDbContextFactory<JitenDbContext> contextFactory,
    IWordFormSiblingCache wordFormCache,
    IDerivationLinkCache derivationCache,
    IMemoryCache memoryCache) : IWordSetMemberCache
{
    /// <summary>Bounds staleness after a membership write made outside this process, such as a CLI word replacement.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);

    /// <summary>Cancelling evicts the superseded generation at once, so a large set is never held twice.</summary>
    private sealed record Generation(int Number, CancellationTokenSource Eviction);

    private readonly ConcurrentDictionary<int, Generation> _generations = new();

    private Generation CurrentGeneration(int setId) => _generations.GetOrAdd(setId, _ => new Generation(0, new CancellationTokenSource()));

    private Task<FrozenSet<long>> GetMembersAsync(int setId)
        => GetOrLoad(setId, "members", () => LoadMembersAsync(setId));

    public Task<FrozenSet<long>> GetKanaRedundantAsync(int setId)
        => GetOutsideMembers(setId, "kana",
                             (members, redundant) => WordFormHelper.ExpandKanaRedundancyKeys(wordFormCache, members, redundant));

    public Task<FrozenSet<long>> GetDerivationRedundantAsync(int setId, IReadOnlySet<DerivationCategory> categories)
    {
        if (categories.Count == 0 || derivationCache.IsEmpty)
            return Task.FromResult(FrozenSet<long>.Empty);

        return GetOutsideMembers(setId, $"derivation:{DerivationSettingsHelper.ToCacheKey(categories)}",
                                 (members, redundant) =>
                                     WordFormHelper.ExpandDerivationRedundancyKeys(derivationCache, categories, members, redundant));
    }

    public async Task<UserWordSets> LoadForUserAsync(UserDbContext userContext, string userId, bool withKanaRedundancy = false,
                                                     IReadOnlySet<DerivationCategory>? derivationCategories = null)
    {
        var states = await userContext.UserWordSetStates
                                      .AsNoTracking()
                                      .Where(s => s.UserId == userId)
                                      .Select(s => new { s.SetId, s.State })
                                      .ToListAsync();
        if (states.Count == 0)
            return UserWordSets.Empty;

        var subscriptions = await Task.WhenAll(states.Select(async s =>
        {
            var generation = CurrentGeneration(s.SetId).Number;
            var members = await GetMembersAsync(s.SetId);
            var kana = withKanaRedundancy ? await GetKanaRedundantAsync(s.SetId) : null;
            var derivation = derivationCategories == null ? null : await GetDerivationRedundantAsync(s.SetId, derivationCategories);
            return new UserWordSets.Subscription(s.SetId, generation, s.State, members, kana, derivation);
        }));

        return new UserWordSets(subscriptions);
    }

    public void Invalidate(int setId)
    {
        while (true)
        {
            var current = CurrentGeneration(setId);
            if (!_generations.TryUpdate(setId, new Generation(current.Number + 1, new CancellationTokenSource()), current))
                continue;

            current.Eviction.Cancel();
            return;
        }
    }

    private Task<FrozenSet<long>> GetOutsideMembers(int setId, string kind,
                                                    Action<IEnumerable<(int WordId, byte ReadingIndex)>, HashSet<long>> expand)
        => GetOrLoad(setId, kind, async () =>
        {
            var members = await GetMembersAsync(setId);
            var redundant = new HashSet<long>();
            expand(members.Select(WordFormHelper.DecodeWordKey), redundant);
            redundant.ExceptWith(members);
            return redundant.ToFrozenSet();
        });

    private Task<FrozenSet<long>> GetOrLoad(int setId, string kind, Func<Task<FrozenSet<long>>> load)
    {
        var generation = CurrentGeneration(setId);
        var options = new MemoryCacheEntryOptions()
                      .SetAbsoluteExpiration(Lifetime)
                      .AddExpirationToken(new CancellationChangeToken(generation.Eviction.Token));
        return memoryCache.GetOrCreateOnceAsync($"wordset:{setId}:{generation.Number}:{kind}", options, load);
    }

    private async Task<FrozenSet<long>> LoadMembersAsync(int setId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var keys = new HashSet<long>();
        await foreach (var m in context.WordSetMembers
                                       .AsNoTracking()
                                       .Where(m => m.SetId == setId)
                                       .Select(m => new { m.WordId, m.ReadingIndex })
                                       .AsAsyncEnumerable())
        {
            if (m.ReadingIndex is < 0 or > byte.MaxValue) continue;
            keys.Add(WordFormHelper.EncodeWordKey(m.WordId, (byte)m.ReadingIndex));
        }

        return keys.ToFrozenSet();
    }
}
