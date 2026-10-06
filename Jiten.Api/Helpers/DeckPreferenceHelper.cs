using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Helpers;

/// <summary>TotalsChanged means reading totals may have moved, so the caller enqueues one accomplishments job. SkippedFull counts changes left out because they needed an entry on a full history.</summary>
public record DeckStatusApplyOutcome(
    int Added,
    int Updated,
    int Unchanged,
    int SkippedIgnored,
    int SkippedExisting,
    int SkippedFull,
    bool TotalsChanged,
    Dictionary<int, UserDeckPreference> Preferences);

public static class DeckPreferenceHelper
{
    /// <summary>
    /// Upserts deck statuses with the same rules as the single-deck endpoint, keeping each deck's current media list entry in step.
    /// <paramref name="date"/> is the day the status took effect for the entries it opens or closes; null records it as unknown.
    /// <paramref name="newEntry"/> starts a new pass on titles already completed instead of reopening their completion.
    /// <paramref name="undoCompletion"/> turns the completion of a title dropped from Completed into a dropped pass.
    /// </summary>
    public static async Task<DeckStatusApplyOutcome> ApplyStatusesAsync(
        UserDbContext userContext, JitenDbContext jitenContext, string userId,
        IReadOnlyCollection<(int DeckId, DeckStatus Status)> entries,
        bool overwriteExisting, bool skipIgnored,
        DateOnly? date = null, bool newEntry = false, bool undoCompletion = false)
    {
        var deckIds = entries.Select(e => e.DeckId).ToList();
        var preferences = await userContext.UserDeckPreferences
                                           .Include(p => p.CurrentEntry)
                                           .Where(p => p.UserId == userId && deckIds.Contains(p.DeckId))
                                           .ToDictionaryAsync(p => p.DeckId);

        var loggedDeckIds = entries.Where(e => e.Status == DeckStatus.Completed &&
                                               preferences.GetValueOrDefault(e.DeckId)?.CurrentEntry?.CharactersRead != null)
                                   .Select(e => e.DeckId)
                                   .ToList();
        var deckCharacters = loggedDeckIds.Count == 0
            ? new Dictionary<int, int>()
            : await jitenContext.Decks.AsNoTracking()
                                .Where(d => loggedDeckIds.Contains(d.DeckId))
                                .ToDictionaryAsync(d => d.DeckId, d => d.CharacterCount);

        var fullDeckIds = await MediaListEntryHelper.FullHistoryDeckIdsAsync(
            userContext, userId,
            entries.Where(e => preferences.TryGetValue(e.DeckId, out var p) && MediaListEntryHelper.StartsEntry(p, e.Status, newEntry))
                   .Select(e => e.DeckId)
                   .ToList());

        var seriesPasses = await MediaListEntryHelper.SeriesPassesAsync(userContext, jitenContext, userId, deckIds);

        int added = 0, updated = 0, unchanged = 0, skippedIgnored = 0, skippedExisting = 0, skippedFull = 0;
        var totalsChanged = false;
        var clearedDeckIds = new List<int>();

        foreach (var (deckId, status) in entries)
        {
            if (!preferences.TryGetValue(deckId, out var preference))
            {
                preference = new UserDeckPreference { UserId = userId, DeckId = deckId };
                userContext.UserDeckPreferences.Add(preference);
                preferences[deckId] = preference;
                added++;
                totalsChanged |= MediaListEntryHelper.ApplyStatus(userContext, preference, status, date, newEntry,
                                                                  seriesPass: seriesPasses.GetValueOrDefault(deckId));
                preference.Status = status;
                continue;
            }

            if (skipIgnored && preference.IsIgnored)
            {
                skippedIgnored++;
                continue;
            }

            var previous = preference.Status;
            if (previous == status)
            {
                unchanged++;
                continue;
            }

            if (previous != DeckStatus.None && !overwriteExisting)
            {
                skippedExisting++;
                continue;
            }

            if (fullDeckIds.Contains(deckId) && MediaListEntryHelper.StartsEntry(preference, status, newEntry))
            {
                skippedFull++;
                continue;
            }

            totalsChanged |= previous == DeckStatus.Completed || status == DeckStatus.Completed;
            totalsChanged |= MediaListEntryHelper.ApplyStatus(userContext, preference, status, date, newEntry,
                                                              deckCharacters.TryGetValue(deckId, out var characters) ? characters : null, undoCompletion,
                                                              seriesPasses.GetValueOrDefault(deckId));
            preference.Status = status;
            if (status == DeckStatus.None)
                clearedDeckIds.Add(deckId);

            if (previous == DeckStatus.None)
                added++;
            else
                updated++;
        }

        await MediaListEntryHelper.RemoveReadsAsync(userContext, userId, clearedDeckIds);

        return new DeckStatusApplyOutcome(added, updated, unchanged, skippedIgnored, skippedExisting, skippedFull, totalsChanged, preferences);
    }
}
