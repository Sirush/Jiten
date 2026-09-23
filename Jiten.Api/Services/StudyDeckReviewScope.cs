using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Services;

/// <summary>Which word keys a user's active study decks cover, for "review from study decks only".</summary>
public interface IStudyDeckReviewScope
{
    Task<List<UserStudyDeck>> LoadActiveStudyDecks(string userId);

    /// <summary>Word keys of the active media and static decks; global-dynamic membership is matched per caller.</summary>
    Task<HashSet<long>> GetStudyDeckBaseKeys(List<UserStudyDeck> activeStudyDecks);

    /// <summary>Base keys plus the global-dynamic matches among <paramref name="cardKeys"/>.</summary>
    Task<HashSet<long>> BuildDeckReviewFilter(string userId, List<(int WordId, byte ReadingIndex)>? cardKeys = null,
                                              List<UserStudyDeck>? activeStudyDecks = null, HashSet<long>? baseKeys = null);

    Task<HashSet<long>> GetFilteredMediaWordKeys(List<UserStudyDeck> mediaStudyDecks);

    /// <summary>Resolves each distinct frequency range once; a range wider than the hoist cap is left out, so those decks keep the in-query filter.</summary>
    Task<Dictionary<(int Min, int Max), HashSet<long>>> LoadGlobalFrequencyRanges(List<UserStudyDeck> studyDecks);
}

public class StudyDeckReviewScope(
    JitenDbContext context,
    UserDbContext userContext,
    IDbContextFactory<JitenDbContext> contextFactory,
    IDbContextFactory<UserDbContext> userContextFactory,
    IHttpContextAccessor httpContextAccessor,
    IDeckWordResolver deckWordResolver,
    IWordFormSiblingCache wordFormCache,
    IDerivationLinkCache derivationCache,
    IMemoryCache memoryCache) : IStudyDeckReviewScope
{
    private const int MaxConcurrentDeckQueries = 6;
    private const int MaxHoistedFrequencyKeys = 400_000;

    public Task<List<UserStudyDeck>> LoadActiveStudyDecks(string userId)
        => userContext.UserStudyDecks
                      .AsNoTracking()
                      .Where(sd => sd.UserId == userId && sd.IsActive)
                      .ToListAsync();

    public async Task<HashSet<long>> GetStudyDeckBaseKeys(List<UserStudyDeck> activeStudyDecks)
    {
        var mediaDecks = activeStudyDecks
            .Where(sd => sd.DeckType == StudyDeckType.MediaDeck && sd.DeckId.HasValue)
            .ToList();
        var wordKeys = await GetFilteredMediaWordKeys(mediaDecks);

        var staticDeckIds = activeStudyDecks
            .Where(sd => sd.DeckType.HasMaterialisedWords())
            .Select(sd => sd.UserStudyDeckId).ToList();
        if (staticDeckIds.Count > 0)
            wordKeys.UnionWith(await deckWordResolver.GetStaticDeckWordKeys(staticDeckIds));

        return wordKeys;
    }

    public async Task<HashSet<long>> BuildDeckReviewFilter(
        string userId,
        List<(int WordId, byte ReadingIndex)>? cardKeys = null,
        List<UserStudyDeck>? activeStudyDecks = null,
        HashSet<long>? baseKeys = null)
    {
        var studyDecks = activeStudyDecks ?? await LoadActiveStudyDecks(userId);
        var wordKeys = baseKeys != null ? new HashSet<long>(baseKeys) : await GetStudyDeckBaseKeys(studyDecks);

        if (cardKeys != null)
        {
            var globalDynamicDecks = studyDecks.Where(sd => sd.DeckType == StudyDeckType.GlobalDynamic).ToList();
            if (globalDynamicDecks.Count > 0)
            {
                var unmatchedWordIds = cardKeys
                    .Where(k => !wordKeys.Contains(WordFormHelper.EncodeWordKey(k.WordId, k.ReadingIndex)))
                    .Select(k => k.WordId)
                    .Distinct()
                    .ToList();

                if (unmatchedWordIds.Count > 0)
                {
                    foreach (var gd in globalDynamicDecks)
                    {
                        wordKeys.UnionWith(await deckWordResolver.GetGlobalDynamicWordKeysForWordIds(
                            gd.MinGlobalFrequency, gd.MaxGlobalFrequency, gd.PosFilter, unmatchedWordIds, gd.ExcludeKana,
                                FrequencyScope.From(gd)));
                    }
                }
            }
        }

        return wordKeys;
    }

    public async Task<HashSet<long>> GetFilteredMediaWordKeys(List<UserStudyDeck> mediaStudyDecks)
    {
        var wordKeys = new HashSet<long>();
        if (mediaStudyDecks.Count == 0) return wordKeys;

        var deckIds = mediaStudyDecks.Select(sd => sd.DeckId!.Value).Distinct().ToList();
        var wordCounts = await context.Decks.AsNoTracking()
            .Where(d => deckIds.Contains(d.DeckId))
            .Select(d => new { d.DeckId, d.WordCount })
            .ToDictionaryAsync(d => d.DeckId, d => d.WordCount);

        var globalFrequencyKeysByRange = await LoadGlobalFrequencyRanges(mediaStudyDecks);
        var deckQueryGate = new SemaphoreSlim(MaxConcurrentDeckQueries);
        var countTasks = new List<Task<(int Count, HashSet<long> WordKeys)>>();

        foreach (var sd in mediaStudyDecks)
        {
            if (!wordCounts.TryGetValue(sd.DeckId!.Value, out var wordCount)) continue;
            var deck = new Deck { DeckId = sd.DeckId.Value, WordCount = wordCount };

            if ((DeckDownloadType)sd.DownloadType == DeckDownloadType.TargetCoverage && sd.TargetPercentage.HasValue)
            {
                countTasks.Add(CountWithFactoryContext((ctx, uCtx, us) => new DeckWordResolver(ctx, uCtx, us, wordFormCache, memoryCache)
                    .CountTargetCoverageWords(sd.DeckId.Value, deck, sd.TargetPercentage.Value, sd.ExcludeKana, sd.PosFilter, sd.StartFromKnown),
                    deckQueryGate));
            }
            else
            {
                var request = new DeckWordResolveRequest(
                    sd.DeckId.Value, deck,
                    (DeckDownloadType)sd.DownloadType, (DeckOrder)sd.Order,
                    sd.MinFrequency, sd.MaxFrequency,
                    false, false,
                    sd.TargetPercentage,
                    sd.MinOccurrences, sd.MaxOccurrences,
                    sd.PosFilter, sd.StartFromKnown);
                globalFrequencyKeysByRange.TryGetValue((sd.MinFrequency, sd.MaxFrequency), out var frequencyKeys);
                countTasks.Add(CountWithFactoryContext((ctx, uCtx, us) => new DeckWordResolver(ctx, uCtx, us, wordFormCache, memoryCache)
                    .CountDeckWords(request, sd.ExcludeKana, frequencyKeys),
                    deckQueryGate));
            }
        }

        foreach (var (_, keys) in await Task.WhenAll(countTasks))
            wordKeys.UnionWith(keys);

        return wordKeys;
    }

    public async Task<Dictionary<(int Min, int Max), HashSet<long>>> LoadGlobalFrequencyRanges(List<UserStudyDeck> studyDecks)
    {
        var ranges = studyDecks
            .Where(sd => sd.DeckType == StudyDeckType.MediaDeck
                         && (DeckDownloadType)sd.DownloadType == DeckDownloadType.TopGlobalFrequency)
            .Select(sd => (Min: sd.MinFrequency, Max: sd.MaxFrequency))
            .Distinct()
            .ToList();

        var result = new Dictionary<(int Min, int Max), HashSet<long>>();
        if (ranges.Count == 0) return result;

        foreach (var range in ranges)
        {
            var rows = await context.WordFormFrequencies
                .AsNoTracking()
                .Where(wff => wff.FrequencyRank >= range.Min && wff.FrequencyRank <= range.Max)
                .Select(wff => new { wff.WordId, wff.ReadingIndex })
                .Take(MaxHoistedFrequencyKeys + 1)
                .ToListAsync();

            if (rows.Count > MaxHoistedFrequencyKeys) continue;

            result[range] = rows.Select(r => WordFormHelper.EncodeWordKey(r.WordId, r.ReadingIndex)).ToHashSet();
        }

        return result;
    }

    private async Task<T> CountWithFactoryContext<T>(Func<JitenDbContext, UserDbContext, ICurrentUserService, Task<T>> query,
                                                     SemaphoreSlim gate)
    {
        await gate.WaitAsync();
        try
        {
            await using var ctx = await contextFactory.CreateDbContextAsync();
            await using var uCtx = await userContextFactory.CreateDbContextAsync();
            var userService = new CurrentUserService(httpContextAccessor, ctx, uCtx, wordFormCache, derivationCache,
                                                     memoryCache);
            return await query(ctx, uCtx, userService);
        }
        finally
        {
            gate.Release();
        }
    }
}
