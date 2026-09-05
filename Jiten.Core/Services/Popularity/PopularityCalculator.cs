using Jiten.Core.Data;

namespace Jiten.Core.Services.Popularity;

/// <summary>Per-user intent signal against a deck; one row per user, deck and kind, dated for decay.</summary>
public readonly record struct IntentEvent(int DeckId, double Weight, DateTime At, string? UserId = null);

/// <summary>Daily anonymous counters for one deck.</summary>
public readonly record struct ActivityDay(int DeckId, DateOnly Date, int Views, int GuestDownloads);

/// <summary>Rating and release date break score ties the same way the list sort does, so ranks match list order.</summary>
public readonly record struct DeckNode(int DeckId, int? ParentDeckId, MediaType MediaType, DateTime CreatedAt, byte ExternalRating = 0, DateOnly ReleaseDate = default);

/// <summary>Ranks are 0 when the deck falls outside the display window, so a stored rank is always one worth showing.</summary>
public readonly record struct PopularityResult(double Score, int TypeRank, int GlobalRank, bool IsTrending);

public static class PopularityWeights
{
    public const double StudyDeck = 5;
    public const double Completed = 4;
    public const double Ongoing = 3;
    public const double Favourite = 3;
    public const double Download = 2;
    public const double Boost = 2;
    public const double Planning = 1.5;
    public const double Upvote = 1;
    public const double Dropped = 0.5;
    public const double Ignored = -1;

    public const double IntentHalfLifeDays = 90;
    public const double AttentionHalfLifeDays = 5;
    public const double ViewWeight = 0.3;
    public const double GuestDownloadWeight = 0.5;
    public const double AttentionCap = 4;
    public const double NewDeckBoost = 2;
    public const int NewDeckBoostDays = 30;
    public const double DecayedShare = 0.7;

    /// <summary>A rank shows only while it is small enough to mean something: top 100 and top quarter of a pool of at least 20.</summary>
    public const int RankDisplayCap = 100;
    public const double RankDisplayShare = 0.25;
    public const int RankMinPool = 20;

    public const int TrendingWindowDays = 7;
    public const int TrendingBaselineDays = 90;
    public const double TrendingMinRecentPoints = 6;
    public const double TrendingMinRatio = 3;
    /// <summary>One account cannot trend a deck on its own, whatever it toggles.</summary>
    public const int TrendingMinRecentUsers = 3;
    /// <summary>Views feed the trending signal, so no deck trends until the activity table has this much history.</summary>
    public const int TrendingMinActivityDays = 14;

    public static double ForStatus(DeckStatus status) => status switch
    {
        DeckStatus.Completed => Completed,
        DeckStatus.Ongoing => Ongoing,
        DeckStatus.Planning => Planning,
        DeckStatus.Dropped => Dropped,
        _ => 0
    };
}

public static class PopularityCalculator
{
    /// <summary>Scores every parent deck 0..1; child signals roll up into the parent.</summary>
    public static Dictionary<int, PopularityResult> Compute(
        IReadOnlyList<DeckNode> decks,
        IEnumerable<IntentEvent> intents,
        IEnumerable<ActivityDay> activity,
        DateTime now)
    {
        var rootOf = decks.ToDictionary(d => d.DeckId, d => d.ParentDeckId ?? d.DeckId);
        var roots = decks.Where(d => d.ParentDeckId == null).ToList();
        var rootIds = roots.Select(r => r.DeckId).ToHashSet();

        var decayed = new Dictionary<int, double>(roots.Count);
        var raw = new Dictionary<int, double>(roots.Count);
        var recentIntent = new Dictionary<int, double>(roots.Count);
        var recentUsers = new Dictionary<int, HashSet<string>>();
        var baselineIntent = new Dictionary<int, double>(roots.Count);
        var views = new Dictionary<int, double>(roots.Count);
        var guestDownloads = new Dictionary<int, double>(roots.Count);
        var recentViews = new Dictionary<int, double>(roots.Count);
        var recentGuestDownloads = new Dictionary<int, double>(roots.Count);
        var baselineViews = new Dictionary<int, double>(roots.Count);
        var baselineGuestDownloads = new Dictionary<int, double>(roots.Count);
        foreach (var id in rootIds)
        {
            decayed[id] = raw[id] = recentIntent[id] = baselineIntent[id] = 0;
            views[id] = guestDownloads[id] = recentViews[id] = recentGuestDownloads[id] = baselineViews[id] = baselineGuestDownloads[id] = 0;
        }

        foreach (var e in intents)
        {
            if (!rootOf.TryGetValue(e.DeckId, out var root) || !rootIds.Contains(root)) continue;
            var age = AgeDays(e.At, now);
            raw[root] += e.Weight;
            decayed[root] += e.Weight * Decay(age, PopularityWeights.IntentHalfLifeDays);
            if (e.Weight <= 0) continue;
            if (age < PopularityWeights.TrendingWindowDays)
            {
                recentIntent[root] += e.Weight;
                if (e.UserId != null)
                {
                    if (!recentUsers.TryGetValue(root, out var users)) recentUsers[root] = users = new HashSet<string>();
                    users.Add(e.UserId);
                }
            }
            if (age < PopularityWeights.TrendingBaselineDays) baselineIntent[root] += e.Weight;
        }

        var activityDates = new HashSet<DateOnly>();
        foreach (var a in activity)
        {
            activityDates.Add(a.Date);
            if (!rootOf.TryGetValue(a.DeckId, out var root) || !rootIds.Contains(root)) continue;
            var age = AgeDays(a.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), now);
            var factor = Decay(age, PopularityWeights.AttentionHalfLifeDays);
            views[root] += a.Views * factor;
            guestDownloads[root] += a.GuestDownloads * factor;
            if (age < PopularityWeights.TrendingWindowDays)
            {
                recentViews[root] += a.Views;
                recentGuestDownloads[root] += a.GuestDownloads;
            }
            if (age < PopularityWeights.TrendingBaselineDays)
            {
                baselineViews[root] += a.Views;
                baselineGuestDownloads[root] += a.GuestDownloads;
            }
        }

        var trendingAllowed = activityDates.Count >= PopularityWeights.TrendingMinActivityDays;
        var decayedTotal = new Dictionary<int, double>(roots.Count);
        var allTime = new Dictionary<int, double>(roots.Count);
        var trending = new Dictionary<int, bool>(roots.Count);
        foreach (var d in roots)
        {
            var id = d.DeckId;
            var age = AgeDays(d.CreatedAt, now);
            var boost = age < PopularityWeights.NewDeckBoostDays ? PopularityWeights.NewDeckBoost : 0;
            decayedTotal[id] = Math.Max(0, decayed[id]) + Attention(views[id], guestDownloads[id]) + boost;
            allTime[id] = Math.Log2(1 + Math.Max(0, raw[id]));

            var recent = recentIntent[id] + Attention(recentViews[id], recentGuestDownloads[id]);
            var baselineWeekly = (baselineIntent[id] + Attention(baselineViews[id], baselineGuestDownloads[id]))
                                 * PopularityWeights.TrendingWindowDays / PopularityWeights.TrendingBaselineDays;
            trending[id] = trendingAllowed
                           && age >= PopularityWeights.NewDeckBoostDays
                           && (recentUsers.GetValueOrDefault(id)?.Count ?? 0) >= PopularityWeights.TrendingMinRecentUsers
                           && recent >= PopularityWeights.TrendingMinRecentPoints
                           && recent >= PopularityWeights.TrendingMinRatio * baselineWeekly;
        }

        var decayedRank = Percentile(decayedTotal);
        var allTimeRank = Percentile(allTime);

        var scores = new Dictionary<int, double>(roots.Count);
        foreach (var d in roots)
        {
            var hasSignal = decayedTotal[d.DeckId] > 0 || allTime[d.DeckId] > 0;
            scores[d.DeckId] = hasSignal
                ? PopularityWeights.DecayedShare * decayedRank[d.DeckId] + (1 - PopularityWeights.DecayedShare) * allTimeRank[d.DeckId]
                : 0;
        }

        var globalRank = DisplayRanks(roots, scores);
        var typeRank = new Dictionary<int, int>(roots.Count);
        foreach (var group in roots.GroupBy(r => r.MediaType))
        {
            foreach (var (id, rank) in DisplayRanks(group.ToList(), scores))
                typeRank[id] = rank;
        }

        var result = new Dictionary<int, PopularityResult>(roots.Count);
        foreach (var d in roots)
            result[d.DeckId] = new PopularityResult(scores[d.DeckId], typeRank[d.DeckId], globalRank[d.DeckId], trending[d.DeckId]);
        return result;
    }

    public static List<IntentEvent> CollapsePerRoot(IEnumerable<IntentEvent> intents, IReadOnlyDictionary<int, int> rootOf)
    {
        var byUser = new Dictionary<(int Root, string UserId), IntentEvent>();
        var anonymous = new List<IntentEvent>();
        foreach (var e in intents)
        {
            var root = rootOf.GetValueOrDefault(e.DeckId, e.DeckId);
            if (e.UserId == null)
            {
                anonymous.Add(e with { DeckId = root });
                continue;
            }

            var key = (root, e.UserId);
            if (!byUser.TryGetValue(key, out var kept))
            {
                byUser[key] = e with { DeckId = root };
                continue;
            }

            var weight = Math.Abs(e.Weight) > Math.Abs(kept.Weight) ? e.Weight : kept.Weight;
            var at = e.At > kept.At ? e.At : kept.At;
            byUser[key] = kept with { Weight = weight, At = at };
        }

        anonymous.AddRange(byUser.Values);
        return anonymous;
    }

    private static double Attention(double views, double guestDownloads) =>
        Math.Min(PopularityWeights.AttentionCap,
                 PopularityWeights.ViewWeight * Math.Log2(1 + views) + PopularityWeights.GuestDownloadWeight * Math.Log2(1 + guestDownloads));

    private static double AgeDays(DateTime at, DateTime now) => Math.Max(0, (now - at).TotalDays);

    private static double Decay(double ageDays, double halfLife) => Math.Pow(0.5, ageDays / halfLife);

    /// <summary>Position in list order within a pool, zeroed outside the display window.</summary>
    private static Dictionary<int, int> DisplayRanks(IReadOnlyList<DeckNode> pool, Dictionary<int, double> scores)
    {
        var result = pool.ToDictionary(p => p.DeckId, _ => 0);
        var ordered = pool.Where(p => scores[p.DeckId] > 0)
                          .OrderByDescending(p => scores[p.DeckId])
                          .ThenByDescending(p => p.ExternalRating)
                          .ThenByDescending(p => p.ReleaseDate)
                          .ThenBy(p => p.DeckId)
                          .ToList();
        if (ordered.Count < PopularityWeights.RankMinPool) return result;

        var limit = Math.Min(PopularityWeights.RankDisplayCap, (int)Math.Floor(ordered.Count * PopularityWeights.RankDisplayShare));
        for (var i = 0; i < ordered.Count && i < limit; i++)
            result[ordered[i].DeckId] = i + 1;

        return result;
    }

    /// <summary>Ranks only decks carrying signal so the scale spreads over engaged decks instead of the whole catalogue; ties share a rank, zero stays 0.</summary>
    private static Dictionary<int, double> Percentile(Dictionary<int, double> values)
    {
        var result = new Dictionary<int, double>(values.Count);
        var ordered = values.Where(kv => kv.Value > 0).OrderBy(kv => kv.Value).ToList();
        foreach (var kv in values) result[kv.Key] = 0;
        if (ordered.Count == 0) return result;

        var i = 0;
        while (i < ordered.Count)
        {
            var j = i;
            while (j + 1 < ordered.Count && ordered[j + 1].Value == ordered[i].Value) j++;
            var value = (double)(j + 1) / ordered.Count;
            for (var k = i; k <= j; k++) result[ordered[k].Key] = value;
            i = j + 1;
        }

        return result;
    }
}
