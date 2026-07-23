using Jiten.Api.Services;

namespace Jiten.Api.Helpers;

/// <summary>
/// Shared wording for tier-limited collections, so the same cap reads the same whether it is hit
/// through a controller or an import. Free users get the Jiten+ number appended as an upsell.
/// </summary>
public static class LimitMessages
{
    public static string StudyDeckCount(UserLimits limits) =>
        $"Maximum of {limits.StudyDecks} study decks reached."
        + Upsell(limits, limits.Allowances.StudyDecks.Plus, "study decks");

    public static string StudyDeckWordsTotal(UserLimits limits, int wordsToAdd)
    {
        var reason = wordsToAdd <= 1
            ? $"Maximum of {limits.StudyDeckWords:N0} total word list deck words reached."
            : $"Adding {wordsToAdd:N0} words would exceed the {limits.StudyDeckWords:N0} word total limit.";
        return reason + Upsell(limits, limits.Allowances.StudyDeckWords.Plus, "words", thousands: true);
    }

    public static string ImportTooLarge(UserLimits limits) =>
        $"Import too large (maximum {limits.ImportWords:N0} words per import)."
        + Upsell(limits, limits.Allowances.ImportWords.Plus, "words", thousands: true);

    public static string CustomSentencesPerWord(UserLimits limits) =>
        $"Maximum of {limits.CustomSentencesPerWord} custom sentences per word."
        + Upsell(limits, limits.Allowances.CustomSentencesPerWord.Plus, "sentences");

    public static string ActiveMediaRequests(UserLimits limits) =>
        $"You have reached the limit of {limits.ActiveMediaRequests} active requests. "
        + "Wait for existing requests to be fulfilled or rejected."
        + Upsell(limits, limits.Allowances.ActiveMediaRequests.Plus, "slots");

    private static string Upsell(UserLimits limits, int plusValue, string noun, bool thousands = false)
    {
        if (limits.IsPlus) return string.Empty;
        var rendered = thousands ? plusValue.ToString("N0") : plusValue.ToString();
        return $" Jiten+ raises this to {rendered} {noun}.";
    }
}
