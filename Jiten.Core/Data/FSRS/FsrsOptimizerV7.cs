namespace Jiten.Core.Data.FSRS;

public class FsrsOptimizerV7Config
{
    public int Epochs { get; init; } = 9;
    public double LearningRate { get; init; } = 0.0118;
    public int BatchSize { get; init; } = 512;
    public double L2Gamma { get; init; } = 1.0;

    /// <summary>Interval-growth and short-interval penalties</summary>
    public bool SchedulePenalties { get; init; } = true;

    /// <summary>Drops cards whose first long-term gap is rare or very long, as fsrs-rs does unless FSRS_NO_OUTLIER is set.</summary>
    public bool FilterOutliers { get; init; } = true;

    public int Seed { get; init; } = 2023;
    public Action<int, int>? Progress { get; init; }
}

public static class FsrsOptimizerV7
{
    private const int P = FsrsHelperV7.ParameterCount;
    private const int MinTargetsForDefaults = 8;
    private const int MinTargetsForTraining = 64;
    private const int MaxSequenceLength = 1024;

    private const double AdamBeta1 = 0.70;
    private const double AdamBeta2 = 0.98;
    private const double AdamEpsilon = 1e-8;
    private const double L2Weight = 0.5;
    private const double BceClamp = 0.0001;

    private const double IntervalGrowthPenaltyWeight = 0.5;
    private const double ShortIntervalPenaltyWeight = 0.0015;
    private const int PenaltyReviews = 10;
    private const double PenaltyGrowthRetention = 0.90;
    private static readonly double[] PenaltyShortRetentions = [0.99];
    private const double MinIntervalDays = 1.0 / 86_400.0;

    /// <summary>Intervals shorter than ten minutes are what the short-interval penalty pushes against.</summary>
    private const double ShortIntervalFloorInverse = 86_400.0 / 600.0;

    private const double InitialStabilityMax = 100.0;

    private const int OutlierMinRemoved = 20;
    private const int OutlierMinGroupSize = 6;
    private const double OutlierMaxGapDays = 100;
    private const double OutlierMaxEasyGapDays = 365;

    private static readonly double[] ParamsStdDev =
    [
        9999.0, 9999.0, 9999.0, 9999.0, 0.523, 0.2528, 0.4329, 0.2966, 0.2139, 0.2889, 0.1862, 0.175,
        0.3812, 0.3013, 0.9104, 0.3234, 0.2448, 0.3273, 0.1842, 0.1735, 0.4608, 0.311, 0.864, 0.0418,
        0.2596, 0.0798, 0.0682, 0.1282, 0.1397, 0.1407, 0.1489, 0.2, 0.15, 0.15,
    ];

    private static readonly double[] InitialStabilityLogAnchors = [-8.09, -3.83, -2.5, -1.0];

    private sealed record Card(FsrsTrainingReview[] Reviews, double[] Weights, int TargetCount);

    public static FsrsOptimizationResult Optimize(List<FsrsTrainingItem> items, FsrsOptimizerV7Config? config = null)
    {
        config ??= new FsrsOptimizerV7Config();
        var cards = BuildWeightedCards(config.FilterOutliers ? FilterOutliers(items) : items);
        var targetCount = cards.Sum(c => c.TargetCount);

        if (targetCount < MinTargetsForDefaults)
            return new FsrsOptimizationResult((double[])FsrsConstants.DefaultParametersV7.Clone(), 0, targetCount, 0);

        var (parameters, initializationCount) = Pretrain(cards);

        if (targetCount == initializationCount || targetCount < MinTargetsForTraining)
            return new FsrsOptimizationResult(parameters, ComputeLoss(cards, parameters), targetCount, 0);

        var initial = (double[])parameters.Clone();
        var batches = BuildBatches(cards, config.BatchSize);
        var totalIterations = (targetCount / config.BatchSize + 1) * config.Epochs;
        var m = new double[P];
        var v = new double[P];
        var step = 0;
        var rng = new Random(config.Seed);
        var order = Enumerable.Range(0, batches.Count).ToArray();

        for (var epoch = 0; epoch < config.Epochs; epoch++)
        {
            rng.Shuffle(order);
            foreach (var batchIndex in order)
            {
                var batch = batches[batchIndex];
                var batchTargets = batch.Sum(c => c.TargetCount);
                var gradients = BatchGradients(batch, parameters);

                var l2Scale = L2Weight * config.L2Gamma * batchTargets / targetCount;
                for (var i = 0; i < P; i++)
                {
                    var sigma = ParamsStdDev[i];
                    gradients[i] += 2 * (parameters[i] - initial[i]) / (sigma * sigma) * l2Scale;
                }

                if (config.SchedulePenalties)
                {
                    var penaltyGradients = SchedulePenaltyGradients(parameters);
                    for (var i = 0; i < P; i++)
                        gradients[i] += penaltyGradients[i] * batchTargets / targetCount;
                }

                var lr = config.LearningRate * 0.5 * (1 + Math.Cos(Math.PI * step / totalIterations));
                step++;
                AdamStep(parameters, gradients, m, v, step, lr);
                FsrsHelperV7.ClampParameters(parameters);
                config.Progress?.Invoke(step, batches.Count * config.Epochs);
            }
        }

        SmoothInitialStabilities(parameters);
        return new FsrsOptimizationResult(parameters, ComputeLoss(cards, parameters), targetCount, config.Epochs);
    }

    /// <summary>Recency-weighted BCE sum over every target and its gradient, as one optimizer batch holding all the items would see it.</summary>
    public static (double Loss, double[] Gradients) WeightedLossAndGradients(List<FsrsTrainingItem> items, double[] parameters)
    {
        var cards = BuildWeightedCards(items);
        var weightSum = cards.Sum(c => c.Weights.Sum());
        return (ComputeLoss(cards, parameters) * weightSum, BatchGradients(cards, parameters));
    }

    /// <summary>Recency-weighted mean BCE over every target, the quantity the optimizer minimises (before regularisation).</summary>
    private static double ComputeLoss(List<Card> cards, double[] w)
    {
        var loss = 0.0;
        var weightSum = 0.0;
        foreach (var card in cards)
        {
            var state = FsrsHelperV7.InitialState((FsrsRating)card.Reviews[0].Rating, w);
            for (var i = 1; i < card.Reviews.Length; i++)
            {
                var review = card.Reviews[i];
                if (card.Weights[i] > 0)
                {
                    var r = Math.Clamp(FsrsHelperV7.Retrievability(review.DeltaT, state, w), BceClamp, 1 - BceClamp);
                    loss -= card.Weights[i] * Math.Log(review.Rating > 1 ? r : 1 - r);
                    weightSum += card.Weights[i];
                }

                state = FsrsHelperV7.NextState(state, review.DeltaT, (FsrsRating)review.Rating, w);
            }
        }

        return weightSum > 0 ? loss / weightSum : 0;
    }

    /// <summary>Cards truncated to MaxSequenceLength, each target carrying its recency weight by chronological rank.</summary>
    private static List<Card> BuildWeightedCards(List<FsrsTrainingItem> items)
    {
        var usable = items.Where(i => i.Reviews.Length >= 2)
                          .Select(i => (Item: i, Reviews: i.Reviews.Length > MaxSequenceLength ? i.Reviews[..MaxSequenceLength] : i.Reviews))
                          .ToList();

        var targets = new List<(int Card, int Index, double Time)>();
        for (var c = 0; c < usable.Count; c++)
        {
            var (item, reviews) = usable[c];
            var time = (double)item.LastReviewTicks;
            for (var i = item.Reviews.Length - 1; i >= reviews.Length; i--)
                time -= item.Reviews[i].DeltaT * TimeSpan.TicksPerDay;
            for (var i = reviews.Length - 1; i >= 1; i--)
            {
                targets.Add((c, i, time));
                time -= reviews[i].DeltaT * TimeSpan.TicksPerDay;
            }
        }

        var weights = usable.Select(u => new double[u.Reviews.Length]).ToArray();
        var ranked = targets.OrderBy(t => t.Time).ToList();
        var denominator = Math.Max(ranked.Count - 1, 1);
        for (var rank = 0; rank < ranked.Count; rank++)
        {
            var ratio = (double)rank / denominator;
            weights[ranked[rank].Card][ranked[rank].Index] = 0.25 + 0.75 * ratio * ratio * ratio;
        }

        return usable.Select((u, c) => new Card(u.Reviews, weights[c], u.Reviews.Length - 1)).ToList();
    }

    /// <summary>dataset.rs filter_outlier; a removed card keeps only the reviews before its first long-term gap.</summary>
    public static List<FsrsTrainingItem> FilterOutliers(List<FsrsTrainingItem> items)
    {
        var firstLongTerm = new int[items.Count];
        var groups = new Dictionary<(int Rating, double Bucket), int>();
        for (var c = 0; c < items.Count; c++)
        {
            var reviews = items[c].Reviews;
            firstLongTerm[c] = reviews.Length < 2 ? -1 : Array.FindIndex(reviews, 1, r => r.DeltaT >= 1.0);
            if (firstLongTerm[c] < 0)
                continue;

            var secondLongTerm = firstLongTerm[c] + 1 < reviews.Length ? Array.FindIndex(reviews, firstLongTerm[c] + 1, r => r.DeltaT >= 1.0) : -1;
            var prefixes = (secondLongTerm < 0 ? reviews.Length : secondLongTerm) - firstLongTerm[c];
            var key = (reviews[0].Rating, OutlierBucket(reviews[firstLongTerm[c]].DeltaT));
            groups[key] = groups.GetValueOrDefault(key) + prefixes;
        }

        var removed = new HashSet<(int Rating, double Bucket)>();
        foreach (var byRating in groups.GroupBy(g => g.Key.Rating))
        {
            var total = byRating.Sum(g => g.Value);
            var removalTarget = Math.Max(OutlierMinRemoved, total / 20);
            var removedCount = 0;
            foreach (var (key, count) in byRating.OrderBy(g => g.Value).ThenBy(g => g.Key.Bucket))
            {
                if (removedCount + count < removalTarget)
                {
                    removedCount += count;
                    removed.Add(key);
                }
                else if (count < OutlierMinGroupSize || key.Bucket > (key.Rating == (int)FsrsRating.Easy ? OutlierMaxEasyGapDays : OutlierMaxGapDays))
                {
                    removed.Add(key);
                }
            }
        }

        var result = new List<FsrsTrainingItem>(items.Count);
        for (var c = 0; c < items.Count; c++)
        {
            var item = items[c];
            var cut = firstLongTerm[c];
            if (cut < 0 || !removed.Contains((item.Reviews[0].Rating, OutlierBucket(item.Reviews[cut].DeltaT))))
            {
                result.Add(item);
                continue;
            }

            var droppedDays = item.Reviews.Skip(cut).Sum(r => r.DeltaT);
            result.Add(new FsrsTrainingItem(item.Reviews[..cut], item.LastReviewTicks - (long)(droppedDays * TimeSpan.TicksPerDay)));
        }

        return result;
    }

    private static double OutlierBucket(double deltaT) => double.IsFinite(deltaT) ? Math.Floor(Math.Max(deltaT, 1.0)) : 1.0;

    /// <summary>build_windowed_batches: cards sorted by length, packed whole until a batch would exceed its target budget.</summary>
    private static List<List<Card>> BuildBatches(List<Card> cards, int batchSize)
    {
        var batches = new List<List<Card>>();
        var current = new List<Card>();
        var currentTargets = 0;
        foreach (var card in cards.Select((c, i) => (c, i)).OrderBy(x => x.c.Reviews.Length).ThenBy(x => x.i).Select(x => x.c))
        {
            if (current.Count > 0 && currentTargets + card.TargetCount > batchSize)
            {
                batches.Add(current);
                current = [];
                currentTargets = 0;
            }

            current.Add(card);
            currentTargets += card.TargetCount;
        }

        if (current.Count > 0)
            batches.Add(current);
        return batches;
    }

    /// <summary>Gradient of the batch's weighted BCE sum; a sum, not a mean, so the L2 scale matches upstream.</summary>
    private static double[] BatchGradients(List<Card> batch, double[] parameters)
    {
        var total = new double[P];
        var sync = new object();

        Parallel.ForEach(batch,
            () => (Tape: new AdTape(4096, P), Grads: new double[P]),
            (card, _, local) =>
            {
                local.Tape.Reset();
                var w = local.Tape.LoadParams(parameters);
                var loss = CardLoss(w, card);
                var grads = local.Tape.Backward(loss);
                for (var i = 0; i < P; i++)
                    local.Grads[i] += grads[i];
                return local;
            },
            local =>
            {
                lock (sync)
                    for (var i = 0; i < P; i++)
                        total[i] += local.Grads[i];
            });

        return total;
    }

    private static Var CardLoss(Var[] w, Card card)
    {
        var tape = w[0].Tape;
        var state = InitialState(w, (FsrsRating)card.Reviews[0].Rating);
        var loss = tape.Const(0);

        for (var i = 1; i < card.Reviews.Length; i++)
        {
            var review = card.Reviews[i];
            var deltaT = tape.Const(Math.Max(0, review.DeltaT));
            if (card.Weights[i] > 0)
            {
                var r = Var.Clamp(Curve(w, deltaT, state), BceClamp, 1 - BceClamp);
                loss = loss - card.Weights[i] * Var.Log(review.Rating > 1 ? r : 1.0 - r);
            }

            if (i + 1 < card.Reviews.Length)
                state = NextState(w, state, deltaT, (FsrsRating)review.Rating);
        }

        return loss;
    }

    private readonly record struct TapeState(Var Stability, Var Difficulty, Var StabilityFast);

    private static TapeState InitialState(Var[] w, FsrsRating rating)
    {
        var g = (int)rating;
        var stability = Var.Clamp(w[g - 1], FsrsHelperV7.StabilityMin, FsrsHelperV7.StabilityMax);
        return new TapeState(stability,
                             Var.Clamp(InitialDifficulty(w, g), FsrsHelperV7.DifficultyMin, FsrsHelperV7.DifficultyMax),
                             Var.Clamp(stability * 0.8, FsrsHelperV7.StabilityMin, FsrsHelperV7.StabilityMax));
    }

    private static Var InitialDifficulty(Var[] w, int g) => w[4] - Var.Exp(w[5] * (g - 1)) + 1.0;

    private static TapeState NextState(Var[] w, TapeState state, Var deltaT, FsrsRating rating)
    {
        var g = (int)rating;
        var s = Var.Clamp(state.Stability, FsrsHelperV7.StabilityMin, FsrsHelperV7.StabilityMax);
        var d = Var.Clamp(state.Difficulty, FsrsHelperV7.DifficultyMin, FsrsHelperV7.DifficultyMax);
        var sFast = Var.Clamp(state.StabilityFast, FsrsHelperV7.StabilityMin, FsrsHelperV7.StabilityMax);
        var clamped = new TapeState(s, d, sFast);

        var r = Curve(w, deltaT, clamped);
        var slow = BlockStability(w, s, d, r, g, 7);
        var fast = BlockStability(w, sFast, d, FastRecall(w, deltaT, sFast), g, 15);
        if (g == 1)
            fast = Var.Min(fast, slow * 0.8);

        return new TapeState(Var.Clamp(slow, FsrsHelperV7.StabilityMin, FsrsHelperV7.StabilityMax),
                             NextDifficulty(w, d, g, r),
                             Var.Clamp(fast, FsrsHelperV7.StabilityMin, FsrsHelperV7.StabilityMax));
    }

    private static Var Curve(Var[] w, Var t, TapeState state)
    {
        var s = Var.Clamp(state.Stability, FsrsHelperV7.StabilityMin, FsrsHelperV7.StabilityMax);
        var sFast = Var.Clamp(state.StabilityFast, FsrsHelperV7.StabilityMin, FsrsHelperV7.StabilityMax);
        var d = Var.Clamp(state.Difficulty, FsrsHelperV7.DifficultyMin, FsrsHelperV7.DifficultyMax);

        var r1 = FastRecall(w, t, sFast);

        var decay2 = -Var.Clamp(w[24], 0.01, 0.95);
        var factor2 = Var.Pow(w[26], 1.0 / decay2) - 1.0;
        var timescale = Var.Exp((d - 5.0) * (w[32] - 0.3));
        var r2 = Var.Pow(t / s * factor2 * timescale + 1.0, decay2);

        var weight1 = w[27] * Var.Pow(sFast, -w[29]);
        var weight2 = w[28] * Var.Pow(s, w[30]) * Var.Exp((d - 5.0) * (w[31] - 0.5));
        var retention = (weight1 * r1 + weight2 * r2) / (weight1 + weight2);
        return retention * (1 - 2e-5) + 1e-5;
    }

    private static Var FastRecall(Var[] w, Var t, Var sFast)
    {
        var decay1 = -Var.Clamp(w[23] * Var.Pow(sFast, w[33] - 0.3), 0.01, 0.95);
        var factor1 = Var.Exp(Var.Clamp(Var.Log(w[25]) / decay1, double.NegativeInfinity, 60)) - 1.0;
        return Var.Pow(t / sFast * factor1 + 1.0, decay1);
    }

    private static Var BlockStability(Var[] w, Var s, Var d, Var r, int g, int start)
    {
        var failStability = w[start + 3] * (Var.Pow(s + 1.0, w[start + 4]) - 1.0) * Var.Exp((1.0 - r) * w[start + 5]);
        var postLapse = Var.Min(s, failStability);
        if (g <= 1)
            return postLapse;

        var increase = Var.Exp(w[start] - 1.5) * (11.0 - d) * Var.Pow(s, -w[start + 1]) * (Var.Exp((1.0 - r) * w[start + 2]) - 1.0);
        if (g == 2) increase = increase * w[start + 6];
        if (g == 4) increase = increase * w[start + 7];
        return Var.Max(postLapse, s * (increase + 1.0));
    }

    private static Var NextDifficulty(Var[] w, Var d, int g, Var r)
    {
        var delta = -w[6] * (g - 3.0);
        if (g == 1)
            delta = delta * (r + 0.1);
        var damped = d + (10.0 - d) * delta / 9.0;
        return Var.Clamp(InitialDifficulty(w, 4) * 0.01 + damped * 0.99, FsrsHelperV7.DifficultyMin, FsrsHelperV7.DifficultyMax);
    }

    /// <summary>training_v7.rs schedule_penalty_dual on a fresh tape; zero when the penalty is not finite.</summary>
    internal static double[] SchedulePenaltyGradients(double[] parameters)
    {
        var tape = new AdTape(8192, P);
        var w = tape.LoadParams(parameters);
        var growth = IntervalGrowthPenalty(parameters, w);
        var shortInterval = ShortIntervalPenalty(parameters, w);
        var penalty = growth * IntervalGrowthPenaltyWeight + shortInterval * ShortIntervalPenaltyWeight;
        if (!double.IsFinite(penalty.Value))
            return new double[P];

        var grads = tape.Backward(penalty);
        for (var i = 0; i < P; i++)
            if (!double.IsFinite(grads[i]))
                grads[i] = 0;
        return grads;
    }

    internal static double SchedulePenaltyValue(double[] parameters)
    {
        var tape = new AdTape(8192, P);
        var w = tape.LoadParams(parameters);
        return (IntervalGrowthPenalty(parameters, w) * IntervalGrowthPenaltyWeight
                + ShortIntervalPenalty(parameters, w) * ShortIntervalPenaltyWeight).Value;
    }

    /// <summary>Squared largest ratio between consecutive Good intervals at 90%, once intervals reach a day.</summary>
    private static Var IntervalGrowthPenalty(double[] parameters, Var[] w)
    {
        var state = InitialState(w, FsrsRating.Good);
        Var? previous = null;
        Var? best = null;
        for (var i = 0; i < PenaltyReviews; i++)
        {
            var interval = LiftedInterval(parameters, w, state, PenaltyGrowthRetention);
            if (previous is { Value: >= 1.0 } prev)
            {
                var ratio = interval / prev;
                if (best is not { } b || ratio.Value > b.Value)
                    best = ratio;
            }

            previous = interval;
            state = NextState(w, state, interval, FsrsRating.Good);
        }

        return best is { } bestRatio ? bestRatio * bestRatio : w[0].Tape.Const(0);
    }

    /// <summary>How far the average sub-day Good interval at high retention falls below ten minutes, in inverse days.</summary>
    private static Var ShortIntervalPenalty(double[] parameters, Var[] w)
    {
        var tape = w[0].Tape;
        var sum = tape.Const(0);
        var count = 0;
        foreach (var retention in PenaltyShortRetentions)
        {
            var state = InitialState(w, FsrsRating.Good);
            var shortSum = tape.Const(0);
            var shortCount = 0;
            for (var i = 0; i < PenaltyReviews; i++)
            {
                var interval = LiftedInterval(parameters, w, state, retention);
                if (interval.Value < 1.0)
                {
                    shortSum = shortSum + interval;
                    shortCount++;
                }

                state = NextState(w, state, interval, FsrsRating.Good);
            }

            if (shortCount == 0)
                continue;

            var average = Var.Clamp(shortSum / shortCount, MinIntervalDays, double.PositiveInfinity);
            sum = sum + (Var.Max(1.0 / average, ShortIntervalFloorInverse) - ShortIntervalFloorInverse);
            count++;
        }

        return count == 0 ? tape.Const(0) : sum / count;
    }

    /// <summary>training_v7.rs next_interval_dual: the solver's interval, differentiated by one implicit-function Newton step.</summary>
    private static Var LiftedInterval(double[] parameters, Var[] w, TapeState state, double retention)
    {
        var tape = w[0].Tape;
        var scalar = new FsrsMemoryStateV7(state.Stability.Value, state.Difficulty.Value, state.StabilityFast.Value);
        var interval = Math.Clamp(FsrsHelperV7.NextInterval(scalar, retention, parameters), MinIntervalDays, FsrsHelperV7.StabilityMax);
        var residual = Curve(w, tape.Const(interval), state) - retention;
        var (_, derivative) = FsrsHelperV7.RetrievabilityAndDerivative(interval, scalar, parameters);
        var slope = Math.Clamp(derivative * interval, -1e9, -1e-9);
        var lifted = Var.Exp(Var.Clamp(tape.Const(Math.Log(interval)) - residual / slope,
                                       Math.Log(MinIntervalDays), Math.Log(FsrsHelperV7.StabilityMax)));
        return tape.WithValue(lifted, interval);
    }

    private static void AdamStep(double[] parameters, double[] gradients, double[] m, double[] v, int t, double lr)
    {
        var mCorrection = 1 - Math.Pow(AdamBeta1, t);
        var vCorrection = 1 - Math.Pow(AdamBeta2, t);
        for (var i = 0; i < P; i++)
        {
            m[i] = AdamBeta1 * m[i] + (1 - AdamBeta1) * gradients[i];
            v[i] = AdamBeta2 * v[i] + (1 - AdamBeta2) * gradients[i] * gradients[i];
            parameters[i] -= lr * (m[i] / mCorrection) / (Math.Sqrt(v[i] / vCorrection) + AdamEpsilon);
        }
    }

    /// <summary>initialize_parameters_fsrs7: fits only w0..w3 to first long-term recall; the curve parameters stay at defaults.</summary>
    private static (double[] Parameters, int InitializationCount) Pretrain(List<Card> cards)
    {
        var parameters = (double[])FsrsConstants.DefaultParametersV7.Clone();
        var labels = 0.0;
        var labelCount = 0;
        var groups = new Dictionary<int, Dictionary<double, (int Recalled, int Total)>>();
        var initializationCount = 0;

        foreach (var card in cards)
        {
            var longTerm = 0;
            FsrsTrainingReview? firstLongTerm = null;
            for (var i = 1; i < card.Reviews.Length; i++)
            {
                var review = card.Reviews[i];
                labels += review.Rating > 1 ? 1 : 0;
                labelCount++;
                if (review.DeltaT >= 1.0)
                {
                    longTerm++;
                    firstLongTerm ??= review;
                }

                // Every prefix with exactly one long-term review feeds initialisation, as upstream counts them.
                if (longTerm != 1)
                    continue;

                initializationCount++;
                var rating = card.Reviews[0].Rating;
                var bucket = Math.Floor(Math.Max(firstLongTerm!.DeltaT, 1.0));
                if (!groups.TryGetValue(rating, out var byBucket))
                    groups[rating] = byBucket = new Dictionary<double, (int, int)>();
                var (recalled, total) = byBucket.GetValueOrDefault(bucket);
                byBucket[bucket] = (recalled + (firstLongTerm.Rating > 1 ? 1 : 0), total + 1);
            }
        }

        var averageRecall = labelCount > 0 ? labels / labelCount : 0;
        var stabilities = new double?[4];
        var counts = new int[4];
        foreach (var (rating, byBucket) in groups)
        {
            var data = byBucket.OrderBy(kv => kv.Key)
                               .Select(kv => (DeltaT: kv.Key, Recall: (kv.Value.Recalled + averageRecall) / (kv.Value.Total + 1.0), Count: (double)kv.Value.Total))
                               .ToList();
            stabilities[rating - 1] = SearchInitialStability(data, FsrsConstants.DefaultParametersV7[rating - 1]);
            counts[rating - 1] = byBucket.Values.Sum(b => b.Total);
        }

        foreach (var (small, big) in new[] { (0, 1), (1, 2), (2, 3), (0, 2), (1, 3), (0, 3) })
        {
            if (stabilities[small] is not { } smallValue || stabilities[big] is not { } bigValue || smallValue <= bigValue)
                continue;
            if (counts[small] > counts[big])
                stabilities[big] = smallValue;
            else
                stabilities[small] = bigValue;
        }

        var filled = FillInitialStabilities(stabilities);
        if (filled != null)
            Array.Copy(filled, parameters, 4);
        return (parameters, initializationCount);
    }

    private static double SearchInitialStability(List<(double DeltaT, double Recall, double Count)> data, double defaultS0)
    {
        var w = FsrsConstants.DefaultParametersV7;
        var decay1 = -w[23];
        var decay2 = -w[24];
        var factor1 = Math.Pow(w[25], 1 / decay1) - 1;
        var factor2 = Math.Pow(w[26], 1 / decay2) - 1;

        // loss_with_curve fits S0 with a D-free, single-S simplification of the curve: S_fast = S.
        double Loss(double s0)
        {
            var weight1 = w[27] * Math.Pow(s0, -w[29]);
            var weight2 = w[28] * Math.Pow(s0, w[30]);
            var loss = 0.0;
            foreach (var (deltaT, recall, count) in data)
            {
                var r1 = Math.Pow(deltaT / s0 * factor1 + 1, decay1);
                var r2 = Math.Pow(deltaT / s0 * factor2 + 1, decay2);
                var predicted = Math.Clamp((r1 * weight1 + r2 * weight2) / (weight1 + weight2), 0.0001, 0.9999);
                loss -= (recall * Math.Log(predicted) + (1 - recall) * Math.Log(1 - predicted)) * count;
            }

            return loss + Math.Abs(s0 - defaultS0) / 16.0;
        }

        var low = FsrsHelperV7.StabilityMin;
        var high = InitialStabilityMax;
        var optimal = defaultS0;
        for (var iter = 0; iter < 1000 && high - low > double.Epsilon; iter++)
        {
            var mid1 = low + (high - low) / 3;
            var mid2 = high - (high - low) / 3;
            if (Loss(mid1) < Loss(mid2))
                high = mid2;
            else
                low = mid1;
            optimal = (high + low) / 2;
        }

        return optimal;
    }

    /// <summary>fill_initial_stabilities_fsrs7; null when no rating has data.</summary>
    private static double[]? FillInitialStabilities(double?[] stabilities)
    {
        var known = Enumerable.Range(0, 4).Where(i => stabilities[i].HasValue).ToList();
        if (known.Count == 0)
            return null;

        var values = new double[4];
        if (known.Count == 1)
        {
            var anchor = known[0];
            var factor = stabilities[anchor]!.Value / FsrsConstants.DefaultParametersV7[anchor];
            for (var i = 0; i < 4; i++)
                values[i] = FsrsConstants.DefaultParametersV7[i] * factor;
            Array.Sort(values);
            return values.Select(v => Math.Clamp(v, FsrsHelperV7.StabilityMin, InitialStabilityMax)).ToArray();
        }

        var logS = new double?[4];
        foreach (var i in known)
            logS[i] = Math.Log(stabilities[i]!.Value);

        for (var target = 0; target < 4; target++)
        {
            if (logS[target].HasValue)
                continue;

            int? lower = null, upper = null;
            for (var r = target - 1; r >= 0 && lower == null; r--) if (logS[r].HasValue) lower = r;
            for (var r = target + 1; r < 4 && upper == null; r++) if (logS[r].HasValue) upper = r;

            var a = InitialStabilityLogAnchors;
            logS[target] = (lower, upper) switch
            {
                ({ } lo, { } hi) => logS[lo] + (a[target] - a[lo]) / (a[hi] - a[lo]) * (logS[hi] - logS[lo]),
                ({ } lo, null) => logS[lo] + (a[target] - a[lo]),
                (null, { } hi) => logS[hi] + (a[target] - a[hi]),
                _ => null,
            };
        }

        for (var i = 0; i < 4; i++)
            values[i] = Math.Clamp(Math.Exp(logS[i]!.Value), FsrsHelperV7.StabilityMin, InitialStabilityMax);
        for (var i = 1; i < 4; i++)
            values[i] = Math.Max(values[i], values[i - 1]);
        return values;
    }

    /// <summary>smooth_initial_stabilities_fsrs7: clamp w0..w3 and make them non-decreasing.</summary>
    private static void SmoothInitialStabilities(double[] parameters)
    {
        var filled = FillInitialStabilities([parameters[0], parameters[1], parameters[2], parameters[3]]);
        if (filled != null)
            Array.Copy(filled, parameters, 4);
    }
}
