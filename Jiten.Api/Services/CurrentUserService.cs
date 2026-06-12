using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public class CurrentUserService(
    IHttpContextAccessor httpContextAccessor,
    JitenDbContext jitenDbContext,
    UserDbContext userContext,
    IWordFormSiblingCache wordFormCache)
    : ICurrentUserService
{
    public ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public string? UserId
    {
        get
        {
            var user = Principal;
            if (user?.Identity?.IsAuthenticated != true)
                return null;

            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? user.FindFirstValue(JwtRegisteredClaimNames.Sub);

            if (string.IsNullOrEmpty(userId) || !Guid.TryParse(userId, out _))
                return null;

            return userId;
        }
    }

    public async Task<Dictionary<(int WordId, byte ReadingIndex), List<KnownState>>> GetKnownWordsState(
        IEnumerable<(int WordId, byte ReadingIndex)> keys)
    {
        if (!IsAuthenticated)
            return new();

        var keysSet = keys.ToHashSet();
        if (keysSet.Count == 0)
            return new();

        var wordIds = keysSet.Select(k => k.WordId).Distinct().ToList();

        var candidates = await userContext.FsrsCards
                                          .Where(u => u.UserId == UserId && wordIds.Contains(u.WordId))
                                          .ToListAsync();

        var fsrsCardDict = candidates
                            .Where(w => keysSet.Contains((w.WordId, w.ReadingIndex)))
                            .DistinctBy(w => (w.WordId, w.ReadingIndex))
                            .ToDictionary(w => (w.WordId, w.ReadingIndex));

        var setDerivedStates = await GetWordSetDerivedStates(wordIds);

        var candidatesByWordId = candidates.GroupBy(c => c.WordId)
                                           .ToDictionary(g => g.Key, g => g.ToList());

        return keysSet.ToDictionary(k => k, k =>
        {
            var hasWordSetState = setDerivedStates.TryGetValue((k.WordId, k.ReadingIndex), out var setState);

            if (fsrsCardDict.TryGetValue(k, out var card))
                return GetKnownStatesFromCard(card);

            if (hasWordSetState)
            {
                return setState switch
                {
                    WordSetStateType.Blacklisted => [KnownState.Blacklisted],
                    WordSetStateType.Mastered => [KnownState.Mastered],
                    _ => [KnownState.New]
                };
            }

            var kanjiIndexes = wordFormCache.GetKanjiIndexesForKana(k.WordId, k.ReadingIndex);
            if (kanjiIndexes != null && candidatesByWordId.TryGetValue(k.WordId, out var wordCandidates))
            {
                var bestKanjiCard = wordCandidates
                    .Where(c => kanjiIndexes.Contains(c.ReadingIndex))
                    .OrderByDescending(c => GetKnownStateRank(c))
                    .FirstOrDefault();

                if (bestKanjiCard != null)
                {
                    // Due belongs to the covering sibling's card; the redundant form itself has nothing to review.
                    // Redundant is always paired with a tier state (New if the sibling was never reviewed).
                    var states = GetKnownStatesFromCard(bestKanjiCard);
                    states.Remove(KnownState.Due);
                    if (states.Count == 0)
                        states.Add(KnownState.New);
                    states.Add(KnownState.Redundant);
                    return states;
                }
            }

            return [KnownState.New];
        });
    }

    private static int GetKnownStateRank(FsrsCard card) => card.State switch
    {
        FsrsState.Mastered => 4,
        FsrsState.Blacklisted => 3,
        FsrsState.Review or FsrsState.Relearning or FsrsState.Learning or FsrsState.Suspended when
            card.LastReview != null && (card.Due - card.LastReview.Value).TotalDays >= 21 => 2,
        FsrsState.Review or FsrsState.Relearning or FsrsState.Learning or FsrsState.Suspended when card.LastReview != null => 1,
        _ => 0
    };

    private static List<KnownState> GetKnownStatesFromCard(FsrsCard card)
    {
        List<KnownState> knownState = new();
        switch (card.State)
        {
            case FsrsState.Mastered:
                knownState.Add(KnownState.Mastered);
                break;
            case FsrsState.Blacklisted:
                knownState.Add(KnownState.Blacklisted);
                break;
        }

        if (knownState.Count > 0)
            return knownState;

        if (card.LastReview == null)
        {
            knownState.Add(KnownState.Due);
            return knownState;
        }

        if (card.Due <= DateTime.UtcNow)
            knownState.Add(KnownState.Due);

        var interval = (card.Due - card.LastReview.Value).TotalDays;
        knownState.Add(interval < 21 ? KnownState.Young : KnownState.Mature);

        return knownState;
    }

    public Task<Dictionary<(int, byte), WordSetStateType>> GetWordSetDerivedStates() =>
        GetWordSetDerivedStates(null);

    private async Task<Dictionary<(int, byte), WordSetStateType>> GetWordSetDerivedStates(List<int>? wordIds)
    {
        if (!IsAuthenticated)
            return new();

        var userSetStates = await userContext.UserWordSetStates
            .AsNoTracking()
            .Where(uwss => uwss.UserId == UserId)
            .ToListAsync();

        if (userSetStates.Count == 0)
            return new();

        var subscribedSetIds = userSetStates.Select(s => s.SetId).ToList();

        IQueryable<WordSetMember> query = jitenDbContext.WordSetMembers
            .Where(wsm => subscribedSetIds.Contains(wsm.SetId));
        if (wordIds != null)
            query = query.Where(wsm => wordIds.Contains(wsm.WordId));

        var memberships = await query
            .Select(wsm => new { wsm.WordId, wsm.ReadingIndex, wsm.SetId })
            .ToListAsync();

        var setStateDict = userSetStates.ToDictionary(s => s.SetId, s => s.State);
        var result = new Dictionary<(int, byte), WordSetStateType>();

        foreach (var m in memberships)
        {
            if (m.ReadingIndex < 0 || m.ReadingIndex > byte.MaxValue) continue;
            var key = (m.WordId, (byte)m.ReadingIndex);

            var newState = setStateDict[m.SetId];

            if (!result.TryGetValue(key, out var existingState))
                result[key] = newState;
            else if (newState == WordSetStateType.Mastered && existingState == WordSetStateType.Blacklisted)
                result[key] = WordSetStateType.Mastered;
        }

        return result;
    }

    public async Task<List<KnownState>> GetKnownWordState(int wordId, byte readingIndex)
    {
        var key = (wordId, readingIndex);
        var result = await GetKnownWordsState([key]);
        return result.TryGetValue(key, out var states) ? states : [KnownState.New];
    }

    public Task<int> AddKnownWords(IEnumerable<DeckWord> deckWords) =>
        UpsertCardsWithState(deckWords, FsrsState.Mastered);

    public Task<int> BlacklistWords(IEnumerable<DeckWord> deckWords) =>
        UpsertCardsWithState(deckWords, FsrsState.Blacklisted);

    private async Task<int> UpsertCardsWithState(IEnumerable<DeckWord> deckWords, FsrsState targetState)
    {
        if (!IsAuthenticated) return 0;
        var words = deckWords?.ToList() ?? [];
        if (words.Count == 0) return 0;

        var wordIds = words.Select(w => w.WordId).Distinct().ToList();

        var validForms = await jitenDbContext.WordForms
                                             .AsNoTracking()
                                             .Where(wf => wordIds.Contains(wf.WordId))
                                             .Select(wf => new { wf.WordId, wf.ReadingIndex })
                                             .ToListAsync();
        var validFormSet = validForms.Select(f => (f.WordId, (byte)f.ReadingIndex)).ToHashSet();

        var pairs = new List<(int WordId, byte ReadingIndex)>();
        var seen = new HashSet<(int, byte)>();
        foreach (var word in words)
        {
            if (!validFormSet.Contains((word.WordId, word.ReadingIndex))) continue;

            var key = (word.WordId, word.ReadingIndex);
            if (seen.Add(key))
                pairs.Add(key);
        }

        if (pairs.Count == 0) return 0;

        DateTime now = DateTime.UtcNow;
        List<int> pairWordIds = pairs.Select(p => p.WordId).Distinct().ToList();
        List<FsrsCard> existing = await userContext.FsrsCards
                                                   .Where(uk => uk.UserId == UserId && pairWordIds.Contains(uk.WordId))
                                                   .ToListAsync();
        var existingSet = existing.DistinctBy(e => (e.WordId, e.ReadingIndex))
                                  .ToDictionary(e => (e.WordId, e.ReadingIndex));

        List<FsrsCard> toInsert = new();

        foreach (var p in pairs)
        {
            if (!existingSet.TryGetValue(p, out var existingUk))
            {
                toInsert.Add(new FsrsCard(UserId!, p.WordId, p.ReadingIndex, due: now, lastReview: now,
                                           state: targetState));
            }
            else
            {
                existingUk.State = targetState;
            }
        }

        if (toInsert.Count > 0)
            await userContext.FsrsCards.AddRangeAsync(toInsert);

        try
        {
            await userContext.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            foreach (var entry in userContext.ChangeTracker.Entries().Where(e => e.State == EntityState.Added))
                entry.State = EntityState.Detached;

            var retryExisting = await userContext.FsrsCards
                .Where(uk => uk.UserId == UserId && pairWordIds.Contains(uk.WordId))
                .ToListAsync();
            var retrySet = retryExisting.DistinctBy(e => (e.WordId, e.ReadingIndex))
                                        .ToDictionary(e => (e.WordId, e.ReadingIndex));

            foreach (var p in pairs)
                if (retrySet.TryGetValue(p, out var card))
                    card.State = targetState;

            await userContext.SaveChangesAsync();
        }

        return toInsert.Count;
    }

    public async Task AddKnownWord(int wordId, byte readingIndex)
    {
        await AddKnownWords([new DeckWord { WordId = wordId, ReadingIndex = readingIndex }]);
    }

    public async Task RemoveKnownWord(int wordId, byte readingIndex)
    {
        if (!IsAuthenticated) return;

        var card = await userContext.FsrsCards.FirstOrDefaultAsync(u => u.UserId == UserId && u.WordId == wordId &&
                                                                        u.ReadingIndex == readingIndex);
        if (card == null) return;

        userContext.FsrsCards.Remove(card);
        await userContext.SaveChangesAsync();
    }

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
}