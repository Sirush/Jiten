using System.Collections.Frozen;
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
    IMemoryCache memoryCache,
    IWordSetMemberCache wordSetCache)
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

        // Family keys are a pure function of the requested keys, so they can widen the card
        // query below instead of costing a second round trip.
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

        var cardsByKey = await LoadCardSnapshots(lookupWordIds);
        var wordSets = await GetWordSets();

        return keysSet.ToDictionary(k => k, k =>
        {
            // An existing card always wins over word-set membership, matching coverage,
            // the known-word counters and the scheduler, which all ignore set state once a card exists.
            if (cardsByKey.TryGetValue(k, out var card))
                return GetKnownStatesFromCard(card);

            if (wordSets.TryGetState(k, out var setState))
            {
                return setState switch
                {
                    WordSetStateType.Blacklisted => [KnownState.Blacklisted],
                    WordSetStateType.Mastered => [KnownState.Mastered],
                    _ => [KnownState.New]
                };
            }

            coversByKey.TryGetValue(k, out var covers);
            return ResolveRedundantStates(k, cardsByKey, wordSets, covers) ?? [KnownState.New];
        });
    }

    /// <summary>Matches <see cref="GetKnownWordsState"/>; a card only changes forms it reaches by kana sibling or derivation, so only those resolve per request.</summary>
    public async Task<RedundantTiers> ResolveRedundantTiers(
        IReadOnlyDictionary<(int WordId, byte ReadingIndex), KnownCardSnapshot> cardsByKey,
        UserWordSets wordSets)
    {
        if (!IsAuthenticated)
            return RedundantTiers.Empty;

        var categories = derivationCache.IsEmpty
            ? new HashSet<DerivationCategory>()
            : await DerivationSettingsHelper.GetEnabledCategories(memoryCache, userContext, UserId!);

        var candidates = new HashSet<(int WordId, byte ReadingIndex)>();
        foreach (var (wordId, ri) in cardsByKey.Keys)
        {
            var kanaIndexes = wordFormCache.GetKanaIndexesForKanji(wordId, ri);
            if (kanaIndexes != null)
                foreach (var kanaRi in kanaIndexes)
                    candidates.Add((wordId, kanaRi));

            if (categories.Count > 0)
                foreach (var covered in derivationCache.GetCoveredKeys(wordId, ri, categories))
                    candidates.Add((covered.WordId, covered.ReadingIndex));
        }

        var cardDriven = new Dictionary<long, VocabularyTier>();
        foreach (var key in candidates)
        {
            if (cardsByKey.ContainsKey(key) || wordSets.Contains(key)) continue;

            var states = ResolveRedundantStates(key, cardsByKey, wordSets, CoveringKeys(key, categories));
            if (states != null)
                cardDriven[WordFormHelper.EncodeWordKey(key.WordId, key.ReadingIndex)] = VocabularyDisplayFilter.ResolveTier(states);
        }

        var (setDriven, setDrivenCacheKey) = await GetSetDrivenRedundantTiers(wordSets, categories);
        var cardKeys = setDriven.Count == 0
            ? []
            : cardsByKey.Keys.Select(k => WordFormHelper.EncodeWordKey(k.WordId, k.ReadingIndex)).ToHashSet();
        return new RedundantTiers(cardDriven, setDriven, setDrivenCacheKey, cardKeys);
    }

    private static readonly TimeSpan SetDrivenTiersLifetime = TimeSpan.FromHours(1);

    /// <summary>Keyed on the subscription mix and derivation categories only, so users sharing both read one copy.</summary>
    private async Task<(FrozenDictionary<long, VocabularyTier> Tiers, string CacheKey)> GetSetDrivenRedundantTiers(
        UserWordSets sets, IReadOnlySet<DerivationCategory> categories)
    {
        if (sets.IsEmpty)
            return (FrozenDictionary<long, VocabularyTier>.Empty, "");

        var cacheKey = $"wordset-redundant:{sets.CacheKey}:{DerivationSettingsHelper.ToCacheKey(categories)}";
        var tiers = await memoryCache.GetOrCreateOnceAsync(cacheKey,
                                                           new MemoryCacheEntryOptions().SetAbsoluteExpiration(SetDrivenTiersLifetime),
                                                           () => BuildSetDrivenRedundantTiers(sets, categories));
        return (tiers, cacheKey);
    }

    private async Task<FrozenDictionary<long, VocabularyTier>> BuildSetDrivenRedundantTiers(
        UserWordSets sets, IReadOnlySet<DerivationCategory> categories)
    {
        var candidates = new HashSet<long>();
        foreach (var set in sets.Sets)
        {
            candidates.UnionWith(await wordSetCache.GetKanaRedundantAsync(set.SetId));
            candidates.UnionWith(await wordSetCache.GetDerivationRedundantAsync(set.SetId, categories));
        }

        var noCards = new Dictionary<(int WordId, byte ReadingIndex), KnownCardSnapshot>();
        var tiers = new Dictionary<long, VocabularyTier>();
        foreach (var encoded in candidates)
        {
            if (sets.Contains(encoded)) continue;

            var key = WordFormHelper.DecodeWordKey(encoded);
            var states = ResolveRedundantStates(key, noCards, sets, CoveringKeys(key, categories));
            if (states != null)
                tiers[encoded] = VocabularyDisplayFilter.ResolveTier(states);
        }

        return tiers.ToFrozenDictionary();
    }

    private IReadOnlyList<DerivationCover>? CoveringKeys((int WordId, byte ReadingIndex) key, IReadOnlySet<DerivationCategory> categories)
        => categories.Count > 0 ? derivationCache.GetCoveringKeys(key.WordId, key.ReadingIndex, categories) : null;

    /// <summary>Kana-sibling cover is tried before the derivation family, so both entry points agree.</summary>
    private List<KnownState>? ResolveRedundantStates(
        (int WordId, byte ReadingIndex) key,
        IReadOnlyDictionary<(int WordId, byte ReadingIndex), KnownCardSnapshot> cardsByKey,
        UserWordSets wordSets,
        IReadOnlyList<DerivationCover>? covers)
    {
        var kanjiIndexes = wordFormCache.GetKanjiIndexesForKana(key.WordId, key.ReadingIndex);
        if (kanjiIndexes != null)
        {
            KnownCardSnapshot? bestKanjiCard = null;
            var bestRank = -1;
            foreach (var kanjiRi in kanjiIndexes)
            {
                if (!cardsByKey.TryGetValue((key.WordId, kanjiRi), out var card)) continue;
                var rank = GetKnownStateRank(card);
                if (rank <= bestRank) continue;
                bestRank = rank;
                bestKanjiCard = card;
            }

            if (bestKanjiCard != null)
                return AsRedundant(GetKnownStatesFromCard(bestKanjiCard.Value));

            // A card on any kanji sibling shadows word-set state, as it does in the derivation family.
            var bestSetRank = -1;
            List<KnownState>? bestSetStates = null;
            foreach (var kanjiRi in kanjiIndexes)
            {
                if (!wordSets.TryGetState((key.WordId, kanjiRi), out var setState)) continue;
                var mastered = setState == WordSetStateType.Mastered;
                var rank = mastered ? MasteredRank : BlacklistedRank;
                if (rank <= bestSetRank) continue;
                bestSetRank = rank;
                bestSetStates = mastered ? [KnownState.Mastered] : [KnownState.Blacklisted];
            }

            if (bestSetStates != null)
                return AsRedundant(bestSetStates);
        }

        if (covers != null && covers.Count > 0)
            return ResolveFromDerivationFamily(covers, cardsByKey, wordSets)?.States;

        return null;
    }

    /// <summary>Due belongs to the covering card; a redundant form has nothing to review and always keeps a tier.</summary>
    private static List<KnownState> AsRedundant(List<KnownState> states)
    {
        states.Remove(KnownState.Due);
        if (states.Count == 0)
            states.Add(KnownState.New);
        states.Add(KnownState.Redundant);
        return states;
    }

    private async Task<Dictionary<(int WordId, byte ReadingIndex), KnownCardSnapshot>> LoadCardSnapshots(List<int> wordIds)
    {
        var cards = await userContext.FsrsCards
                                     .AsNoTracking()
                                     .Where(u => u.UserId == UserId && wordIds.Contains(u.WordId))
                                     .Select(u => new { u.WordId, u.ReadingIndex, u.State, u.Due, u.LastReview })
                                     .ToListAsync();

        var result = new Dictionary<(int WordId, byte ReadingIndex), KnownCardSnapshot>();
        foreach (var c in cards)
            result.TryAdd((c.WordId, c.ReadingIndex), new KnownCardSnapshot(c.State, c.Due, c.LastReview));
        return result;
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
        var cardsByKey = await LoadCardSnapshots(familyWordIds);

        if (cardsByKey.ContainsKey(key))
            return null;

        return ResolveFromDerivationFamily(covers, cardsByKey, await GetWordSets())?.Cover;
    }

    /// <summary>
    /// Copies the best-ranked family member's tier onto a card-less derived form, exactly as the kana-sibling
    /// path does. A card on a family key shadows that key's word-set state, matching the rest of the platform.
    /// </summary>
    private static (List<KnownState> States, DerivationCover Cover)? ResolveFromDerivationFamily(
        IReadOnlyList<DerivationCover> covers,
        IReadOnlyDictionary<(int WordId, byte ReadingIndex), KnownCardSnapshot> cardsByKey,
        UserWordSets wordSets)
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
            else if (wordSets.TryGetState(coverKey, out var setState))
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

        return (AsRedundant(best), bestCover);
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

    private static int GetKnownStateRank(KnownCardSnapshot card) => card.State switch
    {
        FsrsState.Mastered => MasteredRank,
        FsrsState.Blacklisted => BlacklistedRank,
        FsrsState.Review or FsrsState.Relearning or FsrsState.Learning or FsrsState.Suspended when
            card.LastReview != null && (card.Due - card.LastReview.Value).TotalDays >= 21 => 2,
        FsrsState.Review or FsrsState.Relearning or FsrsState.Learning or FsrsState.Suspended when card.LastReview != null => 1,
        _ => 0
    };

    private static List<KnownState> GetKnownStatesFromCard(KnownCardSnapshot card)
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

    public Task<UserWordSets> GetWordSets() =>
        IsAuthenticated ? wordSetCache.LoadForUserAsync(userContext, UserId!) : Task.FromResult(UserWordSets.Empty);

    public async Task<List<KnownState>> GetKnownWordState(int wordId, byte readingIndex)
    {
        var key = (wordId, readingIndex);
        var result = await GetKnownWordsState([key]);
        return result.TryGetValue(key, out var states) ? states : [KnownState.New];
    }

    public async Task<HashSet<(int WordId, byte ReadingIndex)>> GetLeechKeys(IEnumerable<(int WordId, byte ReadingIndex)> keys)
    {
        if (!IsAuthenticated)
            return [];

        var keysSet = keys.ToHashSet();
        if (keysSet.Count == 0)
            return [];

        var settings = await FsrsSettingsHelper.LoadAsync(userContext, UserId!);
        var threshold = FsrsSettingsHelper.GetStudySettings(settings).LeechThreshold;
        if (threshold <= 0)
            return [];

        var wordIds = keysSet.Select(k => k.WordId).Distinct().ToList();
        var candidates = await userContext.FsrsCards
                                          .AsNoTracking()
                                          .Where(c => c.UserId == UserId && wordIds.Contains(c.WordId) && c.Lapses >= threshold
                                                      && c.State != FsrsState.Mastered && c.State != FsrsState.Blacklisted)
                                          .Select(c => new
                                                       {
                                                           c.WordId, c.ReadingIndex, c.State, c.Stability, c.Difficulty, c.StabilityFast,
                                                           c.LastReview, c.Due, c.Lapses
                                                       })
                                          .ToListAsync();

        var leeches = new HashSet<(int WordId, byte ReadingIndex)>();
        if (candidates.Count == 0)
            return leeches;

        var scheduler = FsrsSettingsHelper.CreateScheduler(settings, enableFuzzing: false);
        foreach (var c in candidates)
        {
            var key = (c.WordId, c.ReadingIndex);
            if (!keysSet.Contains(key))
                continue;

            var memory = new FsrsCard
                         {
                             State = c.State, Stability = c.Stability, Difficulty = c.Difficulty, StabilityFast = c.StabilityFast,
                             LastReview = c.LastReview, Due = c.Due
                         };
            double? stabilityDays = c.Stability != null ? scheduler.GetStabilityDays(memory) : null;
            if (LeechHelper.IsLeech(c.Lapses, stabilityDays, threshold))
                leeches.Add(key);
        }

        return leeches;
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

        if (targetState == FsrsState.Mastered)
        {
            var redundantCreated = await WordFormHelper.ArchiveRedundantImportCards(
                userContext, wordFormCache, UserId!, toInsert, pairs.ToHashSet(), _ => []);
            toInsert.RemoveAll(redundantCreated.Contains);
            await WordFormHelper.RemoveRedundantKanaSrsCards(userContext, wordFormCache, UserId!, pairs);
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

/// <summary>The three card fields known-state derivation reads, so callers can pass projections instead of tracked entities.</summary>
public readonly record struct KnownCardSnapshot(FsrsState State, DateTime Due, DateTime? LastReview);
