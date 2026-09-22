using Jiten.Core.Data;

namespace Jiten.Api.Helpers;

/// <summary>The media list's comma-separated status filter; ticked tokens are OR-ed together.</summary>
public sealed class MediaStatusFilter
{
    private static readonly Dictionary<string, DeckStatus> StatusTokens = new()
    {
        ["planning"] = DeckStatus.Planning,
        ["ongoing"] = DeckStatus.Ongoing,
        ["completed"] = DeckStatus.Completed,
        ["dropped"] = DeckStatus.Dropped,
    };

    public HashSet<DeckStatus> Statuses { get; } = [];
    public bool NoStatus { get; private set; }
    public bool Ignored { get; private set; }

    /// <summary>Legacy "fav" token, which predates the standalone favourite flag and must keep working for old URLs and presets.</summary>
    public bool Favourite { get; private set; }

    public bool HasStatusCriteria => Statuses.Count > 0 || NoStatus;

    public static MediaStatusFilter Parse(string? raw)
    {
        var filter = new MediaStatusFilter();
        if (string.IsNullOrWhiteSpace(raw))
            return filter;

        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = token.ToLowerInvariant();
            if (StatusTokens.TryGetValue(normalized, out var status))
                filter.Statuses.Add(status);
            else if (normalized == "nostatus")
                filter.NoStatus = true;
            else if (normalized == "ignore")
                filter.Ignored = true;
            else if (normalized == "fav")
                filter.Favourite = true;
        }

        return filter;
    }

    /// <summary>Keep decks in DeckIds, or drop them when Exclude; "nostatus" must stay an exclusion to cover decks with no preference row.</summary>
    public (HashSet<int> DeckIds, bool Exclude) ResolveDeckIds(IEnumerable<UserDeckPreference> prefs, HashSet<int> ignoredDeckIds)
    {
        if (!HasStatusCriteria && !Ignored)
            return (ignoredDeckIds, true);

        if (NoStatus)
        {
            var excluded = prefs.Where(p => p.Status != DeckStatus.None && !Statuses.Contains(p.Status))
                                .Select(p => p.DeckId)
                                .ToHashSet();
            if (Ignored)
                excluded.ExceptWith(ignoredDeckIds);
            else
                excluded.UnionWith(ignoredDeckIds);
            return (excluded, true);
        }

        var included = prefs.Where(p => Statuses.Contains(p.Status))
                            .Select(p => p.DeckId)
                            .ToHashSet();
        if (Ignored)
            included.UnionWith(ignoredDeckIds);
        else
            included.ExceptWith(ignoredDeckIds);
        return (included, false);
    }
}
