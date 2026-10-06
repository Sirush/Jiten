using System;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public static class DifficultyRankingSync
{
    public static async Task SyncDerivedVotes(
        JitenDbContext context,
        UserDbContext userContext,
        string userId,
        MediaTypeGroup group)
    {
        var completedDeckIds = await MediaListEntryHelper.CompletedDeckIds(userContext, userId).ToListAsync();

        if (completedDeckIds.Count == 0)
            return;

        var deckRows = await context.Decks.AsNoTracking()
            .Where(d => completedDeckIds.Contains(d.DeckId) && d.ParentDeckId == null)
            .Select(d => new { d.DeckId, d.MediaType })
            .ToListAsync();

        var groups = await context.DifficultyRankGroups
            .Where(g => g.UserId == userId && g.MediaTypeGroup == group)
            .Include(g => g.Items)
            .OrderBy(g => g.SortIndex)
            .ToListAsync();

        var rankedDeckIdsRaw = groups
            .SelectMany(g => g.Items)
            .Select(i => i.DeckId)
            .ToHashSet();

        var groupDeckIds = deckRows
            .Where(d => MediaTypeGroups.GetGroup(d.MediaType) == group)
            .Select(d => d.DeckId)
            .ToHashSet();

        var rankedDeckIds = rankedDeckIdsRaw
            .Where(id => groupDeckIds.Contains(id))
            .ToHashSet();

        var implied = new Dictionary<(int lowId, int highId), ComparisonOutcome>();

        void AddPair(int deckAId, int deckBId, ComparisonOutcome outcomeForA)
        {
            var low = Math.Min(deckAId, deckBId);
            var high = Math.Max(deckAId, deckBId);
            var outcome = outcomeForA;
            if (outcome != ComparisonOutcome.Same && deckAId > deckBId)
                outcome = (ComparisonOutcome)(-(int)outcome);
            implied[(low, high)] = outcome;
        }

        var orderedGroups = groups
            .Select(g => g.Items.Select(i => i.DeckId)
                .Where(id => groupDeckIds.Contains(id))
                .OrderBy(id => id)
                .ToList())
            .Where(list => list.Count > 0)
            .ToList();

        var rankIndexByDeckId = new Dictionary<int, int>();
        for (var i = 0; i < orderedGroups.Count; i++)
        {
            foreach (var id in orderedGroups[i])
                rankIndexByDeckId[id] = i;
        }

        foreach (var groupDecks in orderedGroups)
        {
            for (var i = 0; i < groupDecks.Count; i++)
            for (var j = i + 1; j < groupDecks.Count; j++)
                AddPair(groupDecks[i], groupDecks[j], ComparisonOutcome.Same);
        }

        for (var i = 0; i < orderedGroups.Count; i++)
        {
            var easierGroup = orderedGroups[i];
            for (var j = i + 1; j < orderedGroups.Count; j++)
            {
                var harderGroup = orderedGroups[j];
                var outcome = j - i >= 2 ? ComparisonOutcome.MuchEasier : ComparisonOutcome.Easier;
                foreach (var easier in easierGroup)
                foreach (var harder in harderGroup)
                    AddPair(easier, harder, outcome);
            }
        }

        var staleDerived = await context.DifficultyVotes
            .Where(v => v.UserId == userId
                && v.Source == DifficultyVoteSource.WeakOrder
                && v.IsValid
                && (!rankedDeckIds.Contains(v.DeckLowId) || !rankedDeckIds.Contains(v.DeckHighId))
                && (rankedDeckIdsRaw.Contains(v.DeckLowId) || rankedDeckIdsRaw.Contains(v.DeckHighId)
                    || groupDeckIds.Contains(v.DeckLowId) || groupDeckIds.Contains(v.DeckHighId)))
            .ToListAsync();
        if (staleDerived.Count > 0)
            context.DifficultyVotes.RemoveRange(staleDerived);

        if (rankedDeckIds.Count < 2)
        {
            await context.SaveChangesAsync();
            return;
        }

        var manualPairSet = await context.DifficultyVotes
            .Where(v => v.UserId == userId
                && v.IsValid
                && v.Source == DifficultyVoteSource.Manual
                && rankedDeckIds.Contains(v.DeckLowId)
                && rankedDeckIds.Contains(v.DeckHighId))
            .Select(v => new { v.DeckLowId, v.DeckHighId })
            .ToListAsync();
        var manualPairs = manualPairSet
            .Select(v => (v.DeckLowId, v.DeckHighId))
            .ToHashSet();

        foreach (var pair in manualPairs)
            implied.Remove(pair);

        var invalidWeakOrderPairs = await context.DifficultyVotes
            .Where(v => v.UserId == userId
                && v.Source == DifficultyVoteSource.WeakOrder
                && !v.IsValid
                && rankedDeckIds.Contains(v.DeckLowId)
                && rankedDeckIds.Contains(v.DeckHighId))
            .Select(v => new { v.DeckLowId, v.DeckHighId })
            .ToListAsync();
        var blockedPairs = invalidWeakOrderPairs
            .Select(v => (v.DeckLowId, v.DeckHighId))
            .ToHashSet();

        foreach (var pair in blockedPairs)
            implied.Remove(pair);

        var existingWeakOrder = await context.DifficultyVotes
            .Where(v => v.UserId == userId
                && v.Source == DifficultyVoteSource.WeakOrder
                && v.IsValid
                && rankedDeckIds.Contains(v.DeckLowId)
                && rankedDeckIds.Contains(v.DeckHighId))
            .ToListAsync();
        existingWeakOrder = existingWeakOrder
            .Where(v => !manualPairs.Contains((v.DeckLowId, v.DeckHighId)))
            .ToList();

        var existingPairs = existingWeakOrder.ToDictionary(v => (v.DeckLowId, v.DeckHighId));
        var now = DateTimeOffset.UtcNow;

        foreach (var vote in existingWeakOrder)
        {
            var rankLow = rankIndexByDeckId[vote.DeckLowId];
            var rankHigh = rankIndexByDeckId[vote.DeckHighId];
            var dist = Math.Abs(rankLow - rankHigh);
            var outcome = dist == 0
                ? ComparisonOutcome.Same
                : rankLow < rankHigh
                    ? (dist >= 2 ? ComparisonOutcome.MuchEasier : ComparisonOutcome.Easier)
                    : (dist >= 2 ? ComparisonOutcome.MuchHarder : ComparisonOutcome.Harder);

            if (vote.Outcome != outcome || vote.Source != DifficultyVoteSource.WeakOrder)
            {
                vote.Outcome = outcome;
                vote.Source = DifficultyVoteSource.WeakOrder;
                vote.UpdatedAt = now;
            }
        }

        foreach (var kvp in implied)
        {
            if (existingPairs.ContainsKey(kvp.Key)) continue;
            context.DifficultyVotes.Add(new DifficultyVote
            {
                UserId = userId,
                DeckLowId = kvp.Key.lowId,
                DeckHighId = kvp.Key.highId,
                Outcome = kvp.Value,
                Source = DifficultyVoteSource.WeakOrder,
                CreatedAt = now,
                IsValid = true
            });
        }

        await context.SaveChangesAsync();
    }
}
