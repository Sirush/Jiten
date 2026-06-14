using Jiten.Core.Data;

namespace Jiten.Core.Difficulty;

public record DifficultyVoteInput(
    int Id,
    string UserId,
    int DeckLowId,
    int DeckHighId,
    ComparisonOutcome Outcome,
    DifficultyVoteSource Source);

public record DifficultyRatingInput(string UserId, int DeckId, int Rating);

public record DeckDifficultyInput(int DeckId, MediaType MediaType, decimal MlDifficulty);

public record UserDifficultyInput(string UserId, DateTime CreatedAt);

/// <summary>
/// Per-deck output of the adjustment computation. <see cref="Adjustment"/> is what gets written to
/// <c>DeckDifficulty.UserAdjustment</c>; the remaining fields are diagnostics (used by the backtest).
/// </summary>
public record DifficultyAdjustmentResult(
    int DeckId,
    decimal Adjustment,
    decimal RawAdjustment,
    decimal Neff,
    int EasierVoteCount,
    int HarderVoteCount,
    int DistinctVoterCount,
    decimal Balance,
    decimal Confidence);

/// <summary>
/// Tunable parameters for the difficulty adjustment model. Defaults live in <see cref="Default"/>.
/// </summary>
public record DifficultyAdjustmentParameters
{
    // Optimisation
    public decimal LogisticScaleS { get; init; } = 0.5m;
    public decimal LearningRate { get; init; } = 0.05m;
    public int Iterations { get; init; } = 50;

    // (A) Ordinal threshold cuts: a "harder"/"much harder" vote is satisfied once the harder deck
    // sits at least Tau1/Tau2 above the easier one. Magnitude is a *separation*, not a 2x push.
    public decimal Tau1 { get; init; } = 0.25m;
    public decimal Tau2 { get; init; } = 0.6m;
    public decimal SameDeadband { get; init; } = 0.15m;

    // (C) Ridge prior toward the ML difficulty (the residual is shrunk toward 0), plus a constant
    // safety clamp that replaces the old volume-growing dynamic cap.
    public decimal RidgePull { get; init; } = 0.2m;
    public decimal SafetyClamp { get; init; } = 2.0m;
    public decimal AbsoluteWeight { get; init; } = 0.20m;
    // How far a "Same" pull is allowed to act per step (bounds far-apart Same votes).
    public decimal SameGradientClamp { get; init; } = 0.8m;

    // (D) Robust IRLS: comparisons the converged estimate violates by more than RobustHuberDelta
    // (in difficulty units) lose influence proportionally.
    public int RobustRounds { get; init; } = 3;
    public decimal RobustHuberDelta { get; init; } = 0.35m;

    // (B) Confidence: scale the residual by how two-sided the evidence is (balance) and by N_eff.
    // BalanceFloor keeps one-sided-but-unanimous (and legitimately extreme) decks from being fully
    // discarded — they retain at least this fraction of their movement.
    public decimal BalanceExponent { get; init; } = 0.5m;
    public decimal BalanceFloor { get; init; } = 0.5m;
    public decimal NeffFull { get; init; } = 18m;

    // Gating (preserved from the original model)
    public int GateDistinctUsers { get; init; } = 3;
    public int GateDistinctOpponents { get; init; } = 2;
    public decimal GateNeff { get; init; } = 3m;
    public decimal SingleUserConfidenceThreshold { get; init; } = 0.7m;
    public decimal SingleUserAdjustmentRatio { get; init; } = 0.25m;

    // Per-vote base weighting (preserved from the original model)
    public decimal PerUserDiminishingScale { get; init; } = 1.5m;
    public decimal SourceWeightFloor { get; init; } = 0.3m;
    public decimal SourceFullTrustVotes { get; init; } = 100m;
    public decimal UserWeightAgeFull { get; init; } = 60m;
    public decimal UserWeightMediaFull { get; init; } = 15m;

    public static DifficultyAdjustmentParameters Default { get; } = new();
}

/// <summary>
/// Pure (EF-free, side-effect-free) computation of community difficulty adjustments from pairwise
/// votes and absolute ratings. See PLAN: ordinal threshold loss (A), bracketing-balance confidence (B),
/// ridge prior + constant clamp (C), and robust IRLS reweighting (D).
/// </summary>
public static class DifficultyAdjustmentCalculator
{
    public static IReadOnlyList<DifficultyAdjustmentResult> Compute(
        IReadOnlyList<DeckDifficultyInput> decks,
        IReadOnlyList<DifficultyVoteInput> votes,
        IReadOnlyList<DifficultyRatingInput> ratings,
        IReadOnlyList<UserDifficultyInput> users,
        DateTime now,
        DifficultyAdjustmentParameters? parameters = null)
    {
        var p = parameters ?? DifficultyAdjustmentParameters.Default;

        var ml = decks.ToDictionary(d => d.DeckId, d => d.MlDifficulty);
        var mediaType = decks.ToDictionary(d => d.DeckId, d => d.MediaType);
        var decksWithMl = ml.Keys.ToHashSet();

        var filteredVotes = votes
            .Where(v => decksWithMl.Contains(v.DeckLowId) && decksWithMl.Contains(v.DeckHighId))
            .ToList();
        var filteredRatings = ratings
            .Where(r => decksWithMl.Contains(r.DeckId))
            .ToList();

        // ---- User weights (age x breadth), preserved from the original model ----
        var userDeckPairs = new HashSet<(string, int)>();
        foreach (var v in filteredVotes)
        {
            userDeckPairs.Add((v.UserId, v.DeckLowId));
            userDeckPairs.Add((v.UserId, v.DeckHighId));
        }
        foreach (var r in filteredRatings)
            userDeckPairs.Add((r.UserId, r.DeckId));

        var userCreationDates = users.ToDictionary(u => u.UserId, u => u.CreatedAt);
        var uniqueMediaPerUser = userDeckPairs.GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.Count());

        var userWeights = new Dictionary<string, decimal>();
        foreach (var userId in userDeckPairs.Select(x => x.Item1).Distinct())
        {
            decimal ageFactor = 1m;
            if (userCreationDates.TryGetValue(userId, out var created))
            {
                var ageDays = (decimal)(now - created).TotalDays;
                ageFactor = Math.Min(1m, ageDays / p.UserWeightAgeFull);
            }

            var mediaCount = uniqueMediaPerUser.GetValueOrDefault(userId, 0);
            var mediaFactor = Math.Min(1m, (decimal)Math.Sqrt(mediaCount / (double)p.UserWeightMediaFull));
            userWeights[userId] = ageFactor * mediaFactor;
        }

        var manualVoteCounts = filteredVotes
            .Where(v => v.Source == DifficultyVoteSource.Manual)
            .GroupBy(v => v.UserId)
            .ToDictionary(g => g.Key, g => (decimal)g.Count());

        // ---- Per-vote base weight: media-type x user x diminishing x source ----
        var baseWeights = new decimal[filteredVotes.Count];
        var userDeckCumulative = new Dictionary<(string, int), decimal>();
        for (var i = 0; i < filteredVotes.Count; i++)
        {
            var v = filteredVotes[i];
            var wType = MediaTypeGroups.GetComparisonWeight(
                mediaType.GetValueOrDefault(v.DeckLowId), mediaType.GetValueOrDefault(v.DeckHighId));
            var wUser = userWeights.GetValueOrDefault(v.UserId, 1m);

            var keyLow = (v.UserId, v.DeckLowId);
            var keyHigh = (v.UserId, v.DeckHighId);
            var cumLow = userDeckCumulative.GetValueOrDefault(keyLow, 0m);
            var cumHigh = userDeckCumulative.GetValueOrDefault(keyHigh, 0m);
            var dimLow = 1m / (1m + cumLow / p.PerUserDiminishingScale);
            var dimHigh = 1m / (1m + cumHigh / p.PerUserDiminishingScale);
            var wDim = Math.Min(dimLow, dimHigh);

            var wSource = v.Source == DifficultyVoteSource.WeakOrder
                ? Math.Min(1.0m, p.SourceWeightFloor + (1m - p.SourceWeightFloor)
                    * Math.Min(manualVoteCounts.GetValueOrDefault(v.UserId, 0m) / p.SourceFullTrustVotes, 1m))
                : 1.0m;

            baseWeights[i] = wType * wUser * wDim * wSource;
            userDeckCumulative[keyLow] = cumLow + baseWeights[i];
            userDeckCumulative[keyHigh] = cumHigh + baseWeights[i];
        }

        var allDeckIds = filteredVotes
            .SelectMany(v => new[] { v.DeckLowId, v.DeckHighId })
            .Union(filteredRatings.Select(r => r.DeckId))
            .ToHashSet();

        // ---- (D) Robust IRLS: optimise, reweight gross violators, repeat ----
        var robust = new decimal[filteredVotes.Count];
        Array.Fill(robust, 1m);
        var effective = new decimal[filteredVotes.Count];

        Dictionary<int, decimal> adj = new();
        for (var round = 0; round <= p.RobustRounds; round++)
        {
            for (var i = 0; i < filteredVotes.Count; i++)
                effective[i] = baseWeights[i] * robust[i];

            adj = Optimize(filteredVotes, filteredRatings, ml, effective, userWeights, allDeckIds, p);

            if (round < p.RobustRounds)
                for (var i = 0; i < filteredVotes.Count; i++)
                    robust[i] = RobustWeight(filteredVotes[i], ml, adj, p);
        }

        // ---- Per-deck aggregation: N_eff, two-sided balance, vote counts ----
        var neff = allDeckIds.ToDictionary(id => id, _ => 0m);
        var aboveMass = allDeckIds.ToDictionary(id => id, _ => 0m); // mass anchoring the deck from ABOVE
        var belowMass = allDeckIds.ToDictionary(id => id, _ => 0m); // mass anchoring it from BELOW
        var easierCounts = allDeckIds.ToDictionary(id => id, _ => 0);
        var harderCounts = allDeckIds.ToDictionary(id => id, _ => 0);
        var deckUsers = allDeckIds.ToDictionary(id => id, _ => new HashSet<string>());
        var deckOpponents = allDeckIds.ToDictionary(id => id, _ => new HashSet<int>());

        for (var i = 0; i < filteredVotes.Count; i++)
        {
            var v = filteredVotes[i];
            var w = effective[i];
            neff[v.DeckLowId] += w;
            neff[v.DeckHighId] += w;

            deckUsers[v.DeckLowId].Add(v.UserId);
            deckUsers[v.DeckHighId].Add(v.UserId);
            deckOpponents[v.DeckLowId].Add(v.DeckHighId);
            deckOpponents[v.DeckHighId].Add(v.DeckLowId);

            if (v.Outcome == ComparisonOutcome.Same)
            {
                // Same votes anchor a deck from both directions equally.
                aboveMass[v.DeckLowId] += w * 0.5m;
                belowMass[v.DeckLowId] += w * 0.5m;
                aboveMass[v.DeckHighId] += w * 0.5m;
                belowMass[v.DeckHighId] += w * 0.5m;
            }
            else
            {
                var (easierId, harderId) = ResolveDirection(v);
                easierCounts[easierId]++;
                harderCounts[harderId]++;
                // The easier deck is anchored from above (something harder than it); the harder deck from below.
                aboveMass[easierId] += w;
                belowMass[harderId] += w;
            }
        }

        var results = new List<DifficultyAdjustmentResult>(allDeckIds.Count);
        foreach (var id in allDeckIds)
        {
            var n = neff[id];
            var userCount = deckUsers[id].Count;
            var opponents = deckOpponents[id].Count;

            var above = aboveMass[id];
            var below = belowMass[id];
            var total = above + below;
            var balance = total > 0 ? 2m * Math.Min(above, below) / total : 0m;
            var balanceFactor = (decimal)Math.Pow((double)balance, (double)p.BalanceExponent);
            var balanceConfidence = p.BalanceFloor + (1m - p.BalanceFloor) * balanceFactor;
            var neffSat = Math.Clamp(n / p.NeffFull, 0m, 1m);

            decimal gate;
            if (userCount >= p.GateDistinctUsers && opponents >= p.GateDistinctOpponents && n >= p.GateNeff)
            {
                gate = 1m;
            }
            else if (userCount is >= 1 and <= 2)
            {
                var maxUserConfidence = deckUsers[id].Select(u => userWeights.GetValueOrDefault(u, 0m))
                    .DefaultIfEmpty(0m).Max();
                gate = maxUserConfidence > p.SingleUserConfidenceThreshold ? p.SingleUserAdjustmentRatio : 0m;
            }
            else
            {
                gate = 0m;
            }

            var confidence = gate * balanceConfidence * neffSat;
            var raw = adj.GetValueOrDefault(id, 0m);
            var adjustment = Math.Clamp(raw * confidence, -p.SafetyClamp, p.SafetyClamp);

            results.Add(new DifficultyAdjustmentResult(
                id,
                Math.Round(adjustment, 2),
                Math.Round(raw, 4),
                Math.Round(n, 2),
                easierCounts[id],
                harderCounts[id],
                userCount,
                Math.Round(balance, 3),
                Math.Round(confidence, 3)));
        }

        return results;
    }

    private static Dictionary<int, decimal> Optimize(
        List<DifficultyVoteInput> votes,
        List<DifficultyRatingInput> ratings,
        Dictionary<int, decimal> ml,
        decimal[] weights,
        Dictionary<string, decimal> userWeights,
        HashSet<int> allDeckIds,
        DifficultyAdjustmentParameters p)
    {
        var adj = allDeckIds.ToDictionary(id => id, _ => 0m);
        var s = p.LogisticScaleS;

        for (var iter = 0; iter < p.Iterations; iter++)
        {
            for (var i = 0; i < votes.Count; i++)
            {
                var w = weights[i];
                if (w <= 0) continue;
                var v = votes[i];

                if (v.Outcome == ComparisonOutcome.Same)
                {
                    var e = ml[v.DeckLowId] + adj[v.DeckLowId] - (ml[v.DeckHighId] + adj[v.DeckHighId]);
                    var over = Math.Abs(e) - p.SameDeadband;
                    if (over <= 0) continue;
                    var g = Math.Sign(e) * Math.Min(over, p.SameGradientClamp);
                    var push = p.LearningRate * 0.5m * w * g;
                    adj[v.DeckLowId] -= push;
                    adj[v.DeckHighId] += push;
                }
                else
                {
                    var tau = Math.Abs((int)v.Outcome) == 2 ? p.Tau2 : p.Tau1;
                    var (easierId, harderId) = ResolveDirection(v);
                    var gap = ml[harderId] + adj[harderId] - (ml[easierId] + adj[easierId]);
                    // Push only until the asserted separation (tau) is met; vanishes once satisfied.
                    var push = p.LearningRate * w * Sigmoid((tau - gap) / s);
                    adj[harderId] += push;
                    adj[easierId] -= push;
                }
            }

            foreach (var r in ratings)
            {
                var target = r.Rating + 0.5m;
                var current = ml[r.DeckId] + adj[r.DeckId];
                var wUser = userWeights.GetValueOrDefault(r.UserId, 1m);
                adj[r.DeckId] += p.LearningRate * p.AbsoluteWeight * wUser * (target - current) / s;
            }

            foreach (var id in allDeckIds)
            {
                adj[id] -= p.LearningRate * p.RidgePull * adj[id]; // ridge: shrink residual toward ML prior
                adj[id] = Math.Clamp(adj[id], -p.SafetyClamp, p.SafetyClamp);
            }
        }

        return adj;
    }

    /// <summary>Huber weight: 1 while the comparison is satisfied/close, decaying for gross violators.</summary>
    private static decimal RobustWeight(
        DifficultyVoteInput v, Dictionary<int, decimal> ml, Dictionary<int, decimal> adj,
        DifficultyAdjustmentParameters p)
    {
        decimal residual;
        if (v.Outcome == ComparisonOutcome.Same)
        {
            var e = ml[v.DeckLowId] + adj[v.DeckLowId] - (ml[v.DeckHighId] + adj[v.DeckHighId]);
            residual = Math.Max(0m, Math.Abs(e) - p.SameDeadband);
        }
        else
        {
            var tau = Math.Abs((int)v.Outcome) == 2 ? p.Tau2 : p.Tau1;
            var (easierId, harderId) = ResolveDirection(v);
            var gap = ml[harderId] + adj[harderId] - (ml[easierId] + adj[easierId]);
            residual = Math.Max(0m, tau - gap);
        }

        return residual <= p.RobustHuberDelta ? 1m : p.RobustHuberDelta / residual;
    }

    private static (int easier, int harder) ResolveDirection(DifficultyVoteInput v) =>
        (int)v.Outcome < 0 ? (v.DeckLowId, v.DeckHighId) : (v.DeckHighId, v.DeckLowId);

    private static decimal Sigmoid(decimal x)
    {
        var d = (double)x;
        return (decimal)(1.0 / (1.0 + Math.Exp(-d)));
    }
}
