using Jiten.Core.Data;
using Jiten.Core.Data.Billing;

namespace Jiten.Api.Services;

public interface ISentenceUnlockOrder
{
    /// <summary>Null when the viewer lacks Jiten+ or the title has no sentence profiles; callers then keep deck-frequency order.</summary>
    Task<IReadOnlyDictionary<int, int>?> GetRanksAsync(int deckId);

    /// <summary>Ranked words first, unranked ones after in their incoming order; <paramref name="words"/> comes back untouched when there are no ranks.</summary>
    Task<List<T>> OrderAsync<T>(int deckId, List<T> words, Func<T, (int WordId, byte ReadingIndex)> form);
}

public class SentenceUnlockOrder(ICurrentUserService currentUser, IJitenPlusService jitenPlus, ISentenceStatsService sentenceStats)
    : ISentenceUnlockOrder
{
    public async Task<IReadOnlyDictionary<int, int>?> GetRanksAsync(int deckId)
    {
        if (currentUser.UserId is not { } userId || await jitenPlus.GetTierAsync(userId) < JitenPlusTier.Trial) return null;

        var ranks = await sentenceStats.GetUnlockRanksAsync(deckId);
        return ranks is { Count: > 0 } ? ranks : null;
    }

    public async Task<List<T>> OrderAsync<T>(int deckId, List<T> words, Func<T, (int WordId, byte ReadingIndex)> form)
    {
        if (words.Count == 0 || await GetRanksAsync(deckId) is not { } ranks) return words;

        return words.OrderBy(w =>
        {
            var (wordId, readingIndex) = form(w);
            return ranks.GetValueOrDefault(ExampleSentenceTokens.WordKey(wordId, readingIndex), int.MaxValue);
        }).ToList();
    }
}
