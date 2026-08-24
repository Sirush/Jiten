using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Helpers;

/// <summary>CompletedTransition is true when any row moved to or from Completed, so the caller enqueues one accomplishments job.</summary>
public record DeckStatusApplyOutcome(
    int Added,
    int Updated,
    int Unchanged,
    int SkippedIgnored,
    int SkippedExisting,
    bool CompletedTransition,
    Dictionary<int, UserDeckPreference> Preferences);

public static class DeckPreferenceHelper
{
    /// <summary>
    /// Upserts deck statuses with the same rules as the single-deck endpoint
    /// </summary>
    public static async Task<DeckStatusApplyOutcome> ApplyStatusesAsync(
        UserDbContext userContext, string userId,
        IReadOnlyCollection<(int DeckId, DeckStatus Status)> entries,
        bool overwriteExisting, bool skipIgnored)
    {
        var deckIds = entries.Select(e => e.DeckId).ToList();
        var preferences = await userContext.UserDeckPreferences
                                           .Where(p => p.UserId == userId && deckIds.Contains(p.DeckId))
                                           .ToDictionaryAsync(p => p.DeckId);

        int added = 0, updated = 0, unchanged = 0, skippedIgnored = 0, skippedExisting = 0;
        var completedTransition = false;

        foreach (var (deckId, status) in entries)
        {
            if (!preferences.TryGetValue(deckId, out var preference))
            {
                preference = new UserDeckPreference { UserId = userId, DeckId = deckId, Status = status };
                userContext.UserDeckPreferences.Add(preference);
                preferences[deckId] = preference;
                added++;
                completedTransition |= status == DeckStatus.Completed;
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

            completedTransition |= previous == DeckStatus.Completed || status == DeckStatus.Completed;
            preference.Status = status;

            if (previous == DeckStatus.None)
                added++;
            else
                updated++;
        }

        return new DeckStatusApplyOutcome(added, updated, unchanged, skippedIgnored, skippedExisting, completedTransition, preferences);
    }
}
