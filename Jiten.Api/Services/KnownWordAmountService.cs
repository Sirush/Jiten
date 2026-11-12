using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Services;

public interface IKnownWordAmountService
{
    Task<KnownWordAmountDto> ComputeAsync(string userId, bool includeDerivations = true);
}

public sealed class KnownWordAmountService(
    UserDbContext userContext,
    JitenDbContext jitenContext,
    IWordFormSiblingCache wordFormCache,
    IDerivationLinkCache derivationCache,
    IMemoryCache memoryCache) : IKnownWordAmountService
{
    public async Task<KnownWordAmountDto> ComputeAsync(string userId, bool includeDerivations = true)
    {
        var fsrsCards = await userContext.FsrsCards
                                         .AsNoTracking()
                                         .Where(uk => uk.UserId == userId)
                                         .Select(uk => new { uk.WordId, uk.ReadingIndex, uk.State, uk.Due, uk.LastReview })
                                         .ToListAsync();

        // Same conductor set as new-card selection: every card plus Mastered/Blacklisted set members.
        var derivationConductors = fsrsCards.Select(c => (c.WordId, c.ReadingIndex)).ToList();

        var cardForms = new Dictionary<(int WordId, int ReadingIndex), KnownState>();
        foreach (var c in fsrsCards)
            cardForms[(c.WordId, c.ReadingIndex)] = KnownWordRules.CardTier(c.State, c.Due, c.LastReview);

        int youngForms = 0, matureForms = 0, masteredForms = 0, blacklistedForms = 0;
        foreach (var state in cardForms.Values)
        {
            switch (state)
            {
                case KnownState.Young: youngForms++; break;
                case KnownState.Mature: matureForms++; break;
                case KnownState.Mastered: masteredForms++; break;
                case KnownState.Blacklisted: blacklistedForms++; break;
            }
        }

        var effectiveForms = new Dictionary<(int WordId, int ReadingIndex), KnownState>(cardForms);
        int wsMasteredForms = 0, wsBlacklistedForms = 0;

        var userSetStates = await userContext.UserWordSetStates
            .Where(uwss => uwss.UserId == userId)
            .ToListAsync();

        if (userSetStates.Count > 0)
        {
            var masteredSetIds = userSetStates
                .Where(s => s.State == WordSetStateType.Mastered).Select(s => s.SetId).ToList();
            var blacklistedSetIds = userSetStates
                .Where(s => s.State == WordSetStateType.Blacklisted).Select(s => s.SetId).ToList();

            if (masteredSetIds.Count > 0)
            {
                var masteredSetForms = await jitenContext.WordSetMembers
                    .Where(wsm => masteredSetIds.Contains(wsm.SetId))
                    .Select(wsm => new { wsm.WordId, ReadingIndex = (int)wsm.ReadingIndex })
                    .Distinct()
                    .ToListAsync();

                foreach (var m in masteredSetForms)
                {
                    derivationConductors.Add((m.WordId, (byte)m.ReadingIndex));
                    if (effectiveForms.TryAdd((m.WordId, m.ReadingIndex), KnownWordRules.WordSetTier(WordSetStateType.Mastered)))
                        wsMasteredForms++;
                }
            }

            if (blacklistedSetIds.Count > 0)
            {
                var blacklistedSetForms = await jitenContext.WordSetMembers
                    .Where(wsm => blacklistedSetIds.Contains(wsm.SetId))
                    .Select(wsm => new { wsm.WordId, ReadingIndex = (int)wsm.ReadingIndex })
                    .Distinct()
                    .ToListAsync();

                foreach (var m in blacklistedSetForms)
                {
                    derivationConductors.Add((m.WordId, (byte)m.ReadingIndex));
                    if (effectiveForms.TryAdd((m.WordId, m.ReadingIndex), KnownWordRules.WordSetTier(WordSetStateType.Blacklisted)))
                        wsBlacklistedForms++;
                }
            }
        }

        int youngWords = 0, matureWords = 0, masteredWords = 0, blacklistedWords = 0, wsMasteredWords = 0, wsBlacklistedWords = 0;
        foreach (var wordGroup in effectiveForms.GroupBy(kvp => kvp.Key.WordId))
        {
            var tier = KnownWordRules.WordTier(wordGroup.Select(kvp => kvp.Value));
            var fromCard = wordGroup.Any(kvp => kvp.Value == tier && cardForms.ContainsKey(kvp.Key));
            switch (tier)
            {
                case KnownState.Young: youngWords++; break;
                case KnownState.Mature: matureWords++; break;
                case KnownState.Mastered when fromCard: masteredWords++; break;
                case KnownState.Mastered: wsMasteredWords++; break;
                case KnownState.Blacklisted when fromCard: blacklistedWords++; break;
                case KnownState.Blacklisted: wsBlacklistedWords++; break;
            }
        }

        // Same conductor set as the study-batch picker: every card plus Mastered/Blacklisted set members.
        var redundantExpansions = new Dictionary<(int, int), KnownState>();
        foreach (var kvp in effectiveForms)
        {
            var kanaIndexes = wordFormCache.GetKanaIndexesForKanji(kvp.Key.WordId, (byte)kvp.Key.ReadingIndex);
            if (kanaIndexes == null) continue;
            foreach (var kanaIdx in kanaIndexes)
            {
                var kanaKey = (kvp.Key.WordId, (int)kanaIdx);
                if (effectiveForms.ContainsKey(kanaKey)) continue;

                if (!redundantExpansions.TryGetValue(kanaKey, out var existing) ||
                    KnownWordRules.FormRank(kvp.Value) > KnownWordRules.FormRank(existing))
                    redundantExpansions[kanaKey] = kvp.Value;
            }
        }

        // Blacklisted covers still resolve as redundant, but they are not knowledge and would swamp the line
        // for anyone who blacklisted the name set.
        int redundantForms = 0;
        foreach (var (key, state) in redundantExpansions)
        {
            if (effectiveForms.TryAdd(key, state) && state != KnownState.Blacklisted)
                redundantForms++;
        }

        int derivationCoveredForms = 0;
        var derivationCoveredWordIds = new HashSet<int>();
        if (includeDerivations && !derivationCache.IsEmpty)
        {
            var derivationCategories = await DerivationSettingsHelper.GetEnabledCategories(memoryCache, userContext, userId);
            if (derivationCategories.Count > 0)
            {
                var covered = new HashSet<long>();
                WordFormHelper.ExpandDerivationRedundancyKeys(derivationCache, derivationCategories,
                                                              derivationConductors, covered);

                var knownWordIds = new HashSet<int>(effectiveForms.Keys.Select(k => k.WordId));
                foreach (var key in covered)
                {
                    var wordId = (int)(key >> 8);
                    var readingIndex = (int)(key & 0xFF);
                    if (effectiveForms.ContainsKey((wordId, readingIndex))) continue;

                    derivationCoveredForms++;
                    if (!knownWordIds.Contains(wordId))
                        derivationCoveredWordIds.Add(wordId);
                }
            }
        }

        return new KnownWordAmountDto
               {
                   Young = youngWords,
                   YoungForm = youngForms,
                   Mature = matureWords,
                   MatureForm = matureForms,
                   Mastered = masteredWords,
                   MasteredForm = masteredForms,
                   Blacklisted = blacklistedWords,
                   BlacklistedForm = blacklistedForms,
                   WordSetMastered = wsMasteredWords,
                   WordSetMasteredForm = wsMasteredForms,
                   WordSetBlacklisted = wsBlacklistedWords,
                   WordSetBlacklistedForm = wsBlacklistedForms,
                   RedundantForms = redundantForms,
                   DerivationCovered = derivationCoveredWordIds.Count,
                   DerivationCoveredForm = derivationCoveredForms
               };
    }
}
