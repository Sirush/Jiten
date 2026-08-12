using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Services;

public class CurrentUserService(
    IHttpContextAccessor httpContextAccessor,
    JitenDbContext jitenDbContext,
    UserDbContext userContext,
    IWordFormSiblingCache wordFormCache,
    IDerivationLinkCache derivationCache,
    IMemoryCache memoryCache)
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

        // Family keys are a pure function of the requested keys, so they can widen the card and word-set
        // queries below instead of costing a second round trip.
        var coversByKey = await ResolveDerivationCovers(keysSet);
        var lookupWordIds = wordIds;
        if (coversByKey.Count > 0)
        {
            var widened = wordIds.ToHashSet();
            foreach (var covers in coversByKey.Values)
            foreach (var cover in covers)
                widened.Add(cover.WordId);
            lookupWordIds = widened.ToList();
        }

        var candidates = await userContext.FsrsCards
                                          .Where(u => u.UserId == UserId && lookupWordIds.Contains(u.WordId))
                                          .ToListAsync();

        var cardsByKey = candidates
                          .DistinctBy(w => (w.WordId, w.ReadingIndex))
                          .ToDictionary(w => (w.WordId, w.ReadingIndex));

        var fsrsCardDict = cardsByKey
                            .Where(kv => keysSet.Contains(kv.Key))
                            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var setDerivedStates = await GetWordSetDerivedStates(lookupWordIds);

        var candidatesByWordId = candidates.GroupBy(c => c.WordId)
                                           .ToDictionary(g => g.Key, g => g.ToList());

        return keysSet.ToDictionary(k => k, k =>
        {
            // An existing card always wins over word-set membership, matching coverage,
            // the known-word counters and the scheduler, which all ignore set state once a card exists.
            if (fsrsCardDict.TryGetValue(k, out var card))
                return GetKnownStatesFromCard(card);

            if (setDerivedStates.TryGetValue((k.WordId, k.ReadingIndex), out var setState))
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

            if (coversByKey.TryGetValue(k, out var covers))
            {
                var derived = ResolveFromDerivationFamily(covers, cardsByKey, setDerivedStates);
                if (derived != null)
                    return derived.Value.States;
            }

            return [KnownState.New];
        });
    }

    /// <summary>The known family member a card-less derived form is covered by, for the word-page chip.</summary>
    public async Task<DerivationCover?> GetCoveringDerivation(int wordId, byte readingIndex)
    {
        if (!IsAuthenticated) return null;

        var key = (wordId, readingIndex);
        var coversByKey = await ResolveDerivationCovers([key]);
        if (!coversByKey.TryGetValue(key, out var covers))
            return null;

        // The requested word is in the query so its own card can shadow the cover, as it does everywhere else.
        var familyWordIds = covers.Select(c => c.WordId).Append(wordId).Distinct().ToList();
        var cards = await userContext.FsrsCards
                                     .Where(u => u.UserId == UserId && familyWordIds.Contains(u.WordId))
                                     .ToListAsync();

        var cardsByKey = cards.DistinctBy(c => (c.WordId, c.ReadingIndex))
                              .ToDictionary(c => (c.WordId, c.ReadingIndex));

        if (cardsByKey.ContainsKey(key))
            return null;

        var setStates = await GetWordSetDerivedStates(familyWordIds);
        return ResolveFromDerivationFamily(covers, cardsByKey, setStates)?.Cover;
    }

    /// <summary>
    /// Copies the best-ranked family member's tier onto a card-less derived form, exactly as the kana-sibling
    /// path does. A card on a family key shadows that key's word-set state, matching the rest of the platform.
    /// </summary>
    private static (List<KnownState> States, DerivationCover Cover)? ResolveFromDerivationFamily(
        IReadOnlyList<DerivationCover> covers,
        Dictionary<(int WordId, byte ReadingIndex), FsrsCard> cardsByKey,
        Dictionary<(int, byte), WordSetStateType> setDerivedStates)
    {
        List<KnownState>? best = null;
        DerivationCover bestCover = default;
        var bestRank = -1;

        foreach (var cover in covers)
        {
            var coverKey = (cover.WordId, cover.ReadingIndex);
            List<KnownState> states;
            int rank;

            if (cardsByKey.TryGetValue(coverKey, out var card))
            {
                states = GetKnownStatesFromCard(card);
                rank = GetKnownStateRank(card);
            }
            else if (setDerivedStates.TryGetValue(coverKey, out var setState) &&
                     setState is WordSetStateType.Mastered or WordSetStateType.Blacklisted)
            {
                var mastered = setState == WordSetStateType.Mastered;
                states = mastered ? [KnownState.Mastered] : [KnownState.Blacklisted];
                rank = mastered ? MasteredRank : BlacklistedRank;
            }
            else
            {
                continue;
            }

            if (rank <= bestRank) continue;
            bestRank = rank;
            best = states;
            bestCover = cover;
        }

        if (best == null)
            return null;

        // Due belongs to the covering family member's card; the redundant form has nothing to review.
        best.Remove(KnownState.Due);
        if (best.Count == 0)
            best.Add(KnownState.New);
        best.Add(KnownState.Redundant);
        return (best, bestCover);
    }

    private async Task<Dictionary<(int WordId, byte ReadingIndex), IReadOnlyList<DerivationCover>>>
        ResolveDerivationCovers(HashSet<(int WordId, byte ReadingIndex)> keys)
    {
        var result = new Dictionary<(int, byte), IReadOnlyList<DerivationCover>>();
        if (derivationCache.IsEmpty)
            return result;

        var categories = await DerivationSettingsHelper.GetEnabledCategories(memoryCache, userContext, UserId!);
        if (categories.Count == 0)
            return result;

        foreach (var key in keys)
        {
            var covers = derivationCache.GetCoveringKeys(key.WordId, key.ReadingIndex, categories);
            if (covers.Count > 0)
                result[key] = covers;
        }

        return result;
    }

    private const int MasteredRank = 4;
    private const int BlacklistedRank = 3;

    private static int GetKnownStateRank(FsrsCard card) => card.State switch
    {
        FsrsState.Mastered => MasteredRank,
        FsrsState.Blacklisted => BlacklistedRank,
        FsrsState.Review or FsrsState.Relearning or FsrsState.Learning or FsrsState.Suspended when
            card.LastReview != null && (card.Due - card.LastReview.Value).TotalDays >= 21 => 2,
        FsrsState.Review or FsrsState.Relearning or FsrsState.Learning or FsrsState.Suspended when card.LastReview != null => 1,
        _ => 0
    };

    private static List<KnownState> GetKnownStatesFromCard(FsrsCard card)
    {
        switch (card.State)
        {
            case FsrsState.Mastered:
                return [KnownState.Mastered];
            case FsrsState.Blacklisted:
                return [KnownState.Blacklisted];
        }

        // Suspended cards are parked: they keep their tier (so they still count toward coverage),
        // but must never present as Due/Overdue — mirroring how Redundant strips Due.
        if (card.State == FsrsState.Suspended)
        {
            if (card.LastReview == null)
                return [KnownState.Suspended];

            var suspendedInterval = (card.Due - card.LastReview.Value).TotalDays;
            return [suspendedInterval < 21 ? KnownState.Young : KnownState.Mature, KnownState.Suspended];
        }

        List<KnownState> knownState = new();

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

    public Task<VocabularyUpsertResult> AddKnownWords(IEnumerable<DeckWord> deckWords, bool overwriteExisting = true,
                                                      bool countAsNewlyLearned = false) =>
        UpsertCardsWithState(deckWords, FsrsState.Mastered, overwriteExisting, countAsNewlyLearned);

    public Task<VocabularyUpsertResult> BlacklistWords(IEnumerable<DeckWord> deckWords, bool overwriteExisting = true) =>
        UpsertCardsWithState(deckWords, FsrsState.Blacklisted, overwriteExisting, false);

    // overwriteExisting=false leaves cards the user already has at their current state, preserving study history.
    private async Task<VocabularyUpsertResult> UpsertCardsWithState(IEnumerable<DeckWord> deckWords, FsrsState targetState,
                                                                    bool overwriteExisting, bool countAsNewlyLearned)
    {
        if (!IsAuthenticated) return new VocabularyUpsertResult(0, 0);
        var words = deckWords?.ToList() ?? [];
        if (words.Count == 0) return new VocabularyUpsertResult(0, 0);

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

        if (pairs.Count == 0) return new VocabularyUpsertResult(0, 0);

        DateTime now = DateTime.UtcNow;
        List<int> pairWordIds = pairs.Select(p => p.WordId).Distinct().ToList();
        List<FsrsCard> existing = await userContext.FsrsCards
                                                   .Where(uk => uk.UserId == UserId && pairWordIds.Contains(uk.WordId))
                                                   .ToListAsync();
        var existingSet = existing.DistinctBy(e => (e.WordId, e.ReadingIndex))
                                  .ToDictionary(e => (e.WordId, e.ReadingIndex));

        List<FsrsCard> toInsert = new();
        var updated = 0;

        foreach (var p in pairs)
        {
            if (!existingSet.TryGetValue(p, out var existingUk))
            {
                toInsert.Add(new FsrsCard(UserId!, p.WordId, p.ReadingIndex, due: now, lastReview: now,
                                           state: targetState));
            }
            else if (overwriteExisting && existingUk.State != targetState)
            {
                existingUk.State = targetState;
                updated++;
            }
        }

        // Spread backwards from now, oldest first, so the batch keeps the order the deck supplied and no card
        // carries a future timestamp. Spacing them past the cluster gap is what makes the coverage journey read
        // each as its own decision instead of collapsing the lot into a starting-point baseline.
        if (countAsNewlyLearned && toInsert.Count > 1)
        {
            for (var i = 0; i < toInsert.Count; i++)
            {
                var declaredAt = now - CoverageJourneyService.DistinctDeclarationSpacing * (toInsert.Count - 1 - i);
                toInsert[i].CreatedAt = declaredAt;
                toInsert[i].LastReview = declaredAt;
                toInsert[i].Due = declaredAt;
            }
        }

        var autoRestored = 0;
        if (toInsert.Count > 0)
        {
            autoRestored = await CardRestoreService.AutoRestoreAsync(userContext, UserId!, toInsert);
            await userContext.FsrsCards.AddRangeAsync(toInsert);
        }

        try
        {
            await userContext.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            foreach (var entry in userContext.ChangeTracker.Entries().Where(e => e.State == EntityState.Added))
                entry.State = EntityState.Detached;

            foreach (var entry in userContext.ChangeTracker.Entries<FsrsCardArchive>()
                                             .Where(e => e.State == EntityState.Deleted))
                entry.State = EntityState.Unchanged;

            var retryExisting = await userContext.FsrsCards
                .Where(uk => uk.UserId == UserId && pairWordIds.Contains(uk.WordId))
                .ToListAsync();
            var retrySet = retryExisting.DistinctBy(e => (e.WordId, e.ReadingIndex))
                                        .ToDictionary(e => (e.WordId, e.ReadingIndex));

            // The inserts were detached, so nothing was created on this path however the first attempt was counted.
            updated = 0;
            if (overwriteExisting)
            {
                foreach (var p in pairs)
                    if (retrySet.TryGetValue(p, out var card) && card.State != targetState)
                    {
                        card.State = targetState;
                        updated++;
                    }
            }

            await userContext.SaveChangesAsync();
            return new VocabularyUpsertResult(0, updated);
        }

        if (autoRestored > 0)
            await ReviewRollupHelper.MarkDirty(userContext, UserId!);

        return new VocabularyUpsertResult(toInsert.Count, updated, autoRestored);
    }

    public Task<VocabularyUpsertResult> AddKnownWord(int wordId, byte readingIndex)
    {
        return AddKnownWords([new DeckWord { WordId = wordId, ReadingIndex = readingIndex }]);
    }

    public async Task RemoveKnownWord(int wordId, byte readingIndex)
    {
        if (!IsAuthenticated) return;

        var card = await userContext.FsrsCards.FirstOrDefaultAsync(u => u.UserId == UserId && u.WordId == wordId &&
                                                                        u.ReadingIndex == readingIndex);
        if (card == null) return;

        await CardArchiveService.ArchiveCardsAsync(userContext, UserId!, [card], CardArchiveReason.Forget);
        userContext.FsrsCards.Remove(card);
        await userContext.SaveChangesAsync();
    }

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
}