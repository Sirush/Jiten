namespace Jiten.Core.Data.FSRS;

public record FsrsTrainingReview(int Rating, double DeltaT);

public record FsrsTrainingItem(FsrsTrainingReview[] Reviews, long LastReviewTicks = 0);

public record FsrsOptimizationResult(
    double[] Parameters,
    double Loss,
    int ReviewCount,
    int Epochs
);

public class FsrsOptimizerConfig
{
    public int Epochs { get; init; } = 5;
    public double LearningRate { get; init; } = 0.04;
    public int BatchSize { get; init; } = 512;
    public bool EnableShortTerm { get; init; } = true;
    public double L2Gamma { get; init; } = 1.0;
    public Action<int, int>? Progress { get; init; }
}

public class FsrsOptimizer
{
    public const int MinimumReviews = 50;
    private const int PARAMETER_COUNT = 21;
    private const double GRADIENT_EPSILON = 1e-5;
    private const int MIN_ITEMS_FOR_DEFAULTS = 8;
    private const int MIN_ITEMS_FOR_FULL_OPTIMIZATION = 64;
    private const int MAX_SEQUENCE_LENGTH = 1024;

    private static readonly double[] ParamsStdDev =
    [
        6.43, 9.66, 17.58, 27.85, 0.57, 0.28, 0.6, 0.12, 0.39, 0.18,
        0.33, 0.3, 0.09, 0.16, 0.57, 0.25, 1.03, 0.31, 0.32, 0.14, 0.27
    ];

    private static readonly (double Min, double Max)[] ParameterBounds =
    [
        (0.001, 100.0),  // w0: init stability Again
        (0.001, 100.0),  // w1: init stability Hard
        (0.001, 100.0),  // w2: init stability Good
        (0.001, 100.0),  // w3: init stability Easy
        (1.0, 10.0),     // w4: init difficulty
        (0.001, 4.0),    // w5: difficulty sensitivity
        (0.001, 4.0),    // w6: difficulty update rate
        (0.001, 0.75),   // w7: mean reversion
        (0.0, 4.5),      // w8: recall stability exp
        (0.0, 0.8),      // w9: stability power
        (0.001, 3.5),    // w10: retrievability effect
        (0.001, 5.0),    // w11: forget stability scale
        (0.001, 0.25),   // w12: forget difficulty power
        (0.001, 0.9),    // w13: forget stability power
        (0.0, 4.0),      // w14: forget retrievability
        (0.0, 1.0),      // w15: hard penalty
        (1.0, 6.0),      // w16: easy bonus
        (0.0, 2.0),      // w17: short-term stability (fsrs-rs uses a dynamic ceiling based on num_relearning_steps; fixed bound is safe for 1 relearning step)
        (0.0, 2.0),      // w18: short-term offset
        (0.01, 0.8),     // w19: short-term power
        (0.1, 0.8),      // w20: decay
    ];

    public static FsrsOptimizationResult Optimize(
        List<FsrsTrainingItem> items,
        FsrsOptimizerConfig? config = null)
    {
        config ??= new FsrsOptimizerConfig();

        items = items
            .Where(i => i.Reviews.Length <= MAX_SEQUENCE_LENGTH && HasLongTermReview(i))
            .ToList();

        if (items.Count < MIN_ITEMS_FOR_DEFAULTS)
            return new FsrsOptimizationResult(
                (double[])FsrsConstants.DefaultParameters.Clone(), 0, 0, 0);

        var parameters = (double[])FsrsConstants.DefaultParameters.Clone();
        EstimateInitialStability(items, parameters);
        ClampParameters(parameters);

        var totalReviews = items.Sum(CountLongTermReviews);

        // With sparse data, return only the estimated initial stability without full optimization
        if (items.Count < MIN_ITEMS_FOR_FULL_OPTIMIZATION)
            return new FsrsOptimizationResult(parameters, ComputeLoss(items, parameters), totalReviews, 0);

        // Sort chronologically by last review and compute recency weights
        var sorted = items.OrderBy(i => i.LastReviewTicks).ToList();
        var weights = ComputeRecencyWeights(sorted.Count);

        var bestParameters = (double[])parameters.Clone();
        var bestLoss = double.MaxValue;

        var m = new double[PARAMETER_COUNT]; // Adam first moment
        var v = new double[PARAMETER_COUNT]; // Adam second moment
        var t = 0;

        var totalIterations = config.Epochs * ((sorted.Count + config.BatchSize - 1) / config.BatchSize);
        var iterationCount = 0;

        for (var epoch = 0; epoch < config.Epochs; epoch++)
        {
            var (shuffled, shuffledWeights) = ShuffleItems(sorted, weights, epoch);
            var batchCount = (shuffled.Count + config.BatchSize - 1) / config.BatchSize;

            for (var batchIdx = 0; batchIdx < batchCount; batchIdx++)
            {
                var batchStart = batchIdx * config.BatchSize;
                var batchEnd = Math.Min(batchStart + config.BatchSize, shuffled.Count);
                var batch = shuffled.GetRange(batchStart, batchEnd - batchStart);
                var batchWeights = shuffledWeights.GetRange(batchStart, batchEnd - batchStart);

                var gradients = ComputeGradients(batch, parameters, batchWeights);
                AddL2Gradient(gradients, parameters, FsrsConstants.DefaultParameters,
                    config.L2Gamma, batch.Count, sorted.Count);

                if (!config.EnableShortTerm)
                    FreezeShortTermGradients(gradients);

                t++;
                var lr = CosineAnnealingLr(config.LearningRate, iterationCount, totalIterations);
                AdamStep(parameters, gradients, m, v, t, lr);
                ClampParameters(parameters);

                iterationCount++;
                config.Progress?.Invoke(iterationCount, totalIterations);
            }

            var epochLoss = ComputeLoss(sorted, parameters, weights);
            if (epochLoss < bestLoss)
            {
                bestLoss = epochLoss;
                Array.Copy(parameters, bestParameters, PARAMETER_COUNT);
            }
        }

        return new FsrsOptimizationResult(bestParameters, bestLoss, totalReviews, config.Epochs);
    }

    private static bool HasLongTermReview(FsrsTrainingItem item) => CountLongTermReviews(item) > 0;

    private static int CountLongTermReviews(FsrsTrainingItem item)
    {
        var count = 0;
        for (var i = 1; i < item.Reviews.Length; i++)
            if (item.Reviews[i].DeltaT >= 1.0) count++;
        return count;
    }

    public static double ComputeLoss(List<FsrsTrainingItem> items, double[] parameters, IReadOnlyList<double>? weights = null)
    {
        var totalLoss = 0.0;
        var totalWeight = 0.0;

        for (var idx = 0; idx < items.Count; idx++)
        {
            var w = weights?[idx] ?? 1.0;
            var predictions = ForwardPass(items[idx], parameters);
            for (var i = 0; i < predictions.Count; i++)
            {
                var (predicted, label) = predictions[i];
                totalLoss += w * BinaryCrossEntropy(predicted, label);
                totalWeight += w;
            }
        }

        return totalWeight > 0 ? totalLoss / totalWeight : 0;
    }

    public static List<(double Predicted, double Label)> ForwardPass(FsrsTrainingItem item, double[] parameters)
    {
        var predictions = new List<(double Predicted, double Label)>();

        if (item.Reviews.Length < 2)
            return predictions;

        var firstReview = item.Reviews[0];
        var rating = (FsrsRating)firstReview.Rating;
        var stability = FsrsHelper.CalculateInitialStability(rating, parameters);
        var difficulty = FsrsHelper.CalculateInitialDifficulty(rating, parameters);

        for (var i = 1; i < item.Reviews.Length; i++)
        {
            var review = item.Reviews[i];
            var deltaT = review.DeltaT;
            var currentRating = (FsrsRating)review.Rating;

            // Same-day reviews shape the state walk but are not prediction targets: at a fractional
            // deltaT the curve predicts R about 1 whatever the parameters, so an Again there is pure noise.
            if (deltaT < 1.0)
            {
                stability = FsrsHelper.CalculateShortTermStability(stability, currentRating, parameters);
                difficulty = FsrsHelper.CalculateNextDifficulty(difficulty, currentRating, parameters);
            }
            else
            {
                var retrievability = PowerForgettingCurve(deltaT, stability, parameters);
                var label = review.Rating > 1 ? 1.0 : 0.0;
                predictions.Add((retrievability, label));

                stability = FsrsHelper.CalculateNextStability(difficulty, stability, retrievability, currentRating, parameters);
                difficulty = FsrsHelper.CalculateNextDifficulty(difficulty, currentRating, parameters);
            }
        }

        return predictions;
    }

    public static double PowerForgettingCurve(double elapsedDays, double stability, double[] parameters)
    {
        if (stability < FsrsConstants.StabilityMin)
            stability = FsrsConstants.StabilityMin;

        var decay = -parameters[20];
        var factor = Math.Pow(0.9, 1.0 / decay) - 1;
        return Math.Pow(1 + factor * elapsedDays / stability, decay);
    }

    public static double BinaryCrossEntropy(double predicted, double label)
    {
        predicted = Math.Clamp(predicted, 1e-7, 1.0 - 1e-7);
        return -(label * Math.Log(predicted) + (1 - label) * Math.Log(1 - predicted));
    }

    public static double[] ComputeNumericalGradients(List<FsrsTrainingItem> batch, double[] parameters, IReadOnlyList<double>? weights = null)
    {
        var gradients = new double[PARAMETER_COUNT];

        Parallel.For(0, PARAMETER_COUNT, i =>
        {
            var localParams = (double[])parameters.Clone();

            localParams[i] = parameters[i] + GRADIENT_EPSILON;
            var lossPlus = ComputeLoss(batch, localParams, weights);

            localParams[i] = parameters[i] - GRADIENT_EPSILON;
            var lossMinus = ComputeLoss(batch, localParams, weights);

            gradients[i] = (lossPlus - lossMinus) / (2 * GRADIENT_EPSILON);
        });

        return gradients;
    }

    public static double[] ComputeGradients(List<FsrsTrainingItem> batch, double[] parameters, IReadOnlyList<double>? weights = null)
    {
        var totalGradients = new double[PARAMETER_COUNT];
        var totalWeight = 0.0;
        var sync = new object();

        Parallel.For(0, batch.Count,
            () => (Tape: new AdTape(), Grads: new double[PARAMETER_COUNT], Weight: 0.0),
            (idx, _, local) =>
            {
                var item = batch[idx];
                if (item.Reviews.Length < 2)
                    return local;

                local.Tape.Reset();
                var w = local.Tape.LoadParams(parameters);
                var itemWeight = weights?[idx] ?? 1.0;

                var firstRating = (FsrsRating)item.Reviews[0].Rating;
                var stability = DiffInitStability(w, firstRating);
                var difficulty = DiffInitDifficulty(w, firstRating);

                var totalBce = local.Tape.Const(0.0);
                var predCount = 0;

                for (var i = 1; i < item.Reviews.Length; i++)
                {
                    var review = item.Reviews[i];
                    var deltaT = review.DeltaT;
                    var currentRating = (FsrsRating)review.Rating;

                    if (deltaT < 1.0)
                    {
                        stability = DiffShortTermStability(w, stability, currentRating);
                        difficulty = DiffNextDifficulty(w, difficulty, currentRating);
                    }
                    else
                    {
                        var retrievability = DiffForgettingCurve(w, deltaT, stability);
                        var label = review.Rating > 1 ? 1.0 : 0.0;
                        totalBce = totalBce + DiffBce(retrievability, label);
                        predCount++;

                        stability = DiffNextStability(w, difficulty, stability, retrievability, currentRating);
                        difficulty = DiffNextDifficulty(w, difficulty, currentRating);
                    }
                }

                var grads = local.Tape.Backward(totalBce);
                var weight = itemWeight * predCount;
                for (var i = 0; i < PARAMETER_COUNT; i++)
                    local.Grads[i] += grads[i] * itemWeight;

                return (local.Tape, local.Grads, local.Weight + weight);
            },
            local =>
            {
                lock (sync)
                {
                    for (var i = 0; i < PARAMETER_COUNT; i++)
                        totalGradients[i] += local.Grads[i];
                    totalWeight += local.Weight;
                }
            });

        if (totalWeight > 0)
            for (var i = 0; i < PARAMETER_COUNT; i++)
                totalGradients[i] /= totalWeight;

        return totalGradients;
    }

    private static Var DiffInitStability(Var[] w, FsrsRating rating) =>
        Var.Clamp(w[(int)rating - 1], FsrsConstants.StabilityMin, FsrsConstants.StabilityMax);

    private static Var DiffInitDifficulty(Var[] w, FsrsRating rating) =>
        Var.Clamp(w[4] - Var.Exp(w[5] * ((int)rating - 1)) + 1.0, 1.0, 10.0);

    private static Var DiffForgettingCurve(Var[] w, double deltaT, Var stability)
    {
        var s = Var.Max(stability, FsrsConstants.StabilityMin);
        var decay = -w[20];
        var factor = Var.Pow(0.9, 1.0 / decay) - 1.0;
        return Var.Pow(1.0 + factor * deltaT / s, decay);
    }

    private static Var DiffBce(Var predicted, double label)
    {
        var p = Var.Clamp(predicted, 1e-7, 1.0 - 1e-7);
        return -(label * Var.Log(p) + (1.0 - label) * Var.Log(1.0 - p));
    }

    private static Var DiffShortTermStability(Var[] w, Var stability, FsrsRating rating)
    {
        var sinc = Var.Exp(w[17] * ((int)rating - 3 + w[18])) * Var.Pow(stability, -w[19]);
        if (rating is FsrsRating.Good or FsrsRating.Easy)
            sinc = Var.Max(sinc, 1.0);
        return Var.Clamp(stability * sinc, FsrsConstants.StabilityMin, FsrsConstants.StabilityMax);
    }

    private static Var DiffNextDifficulty(Var[] w, Var difficulty, FsrsRating rating)
    {
        var dEasy = Var.Clamp(w[4] - Var.Exp(w[5] * 3.0) + 1.0, 1.0, 10.0);
        var deltaD = -(w[6] * ((int)rating - 3));
        var linear = (10.0 - difficulty) * deltaD / 9.0;
        var next = difficulty + linear;
        var result = w[7] * dEasy + (1.0 - w[7]) * next;
        return Var.Clamp(result, 1.0, 10.0);
    }

    private static Var DiffNextStability(Var[] w, Var difficulty, Var stability, Var retrievability, FsrsRating rating) =>
        Var.Clamp(
            rating == FsrsRating.Again
                ? DiffForgetStability(w, difficulty, stability, retrievability)
                : DiffRecallStability(w, difficulty, stability, retrievability, rating),
            FsrsConstants.StabilityMin, FsrsConstants.StabilityMax);

    private static Var DiffRecallStability(Var[] w, Var difficulty, Var stability, Var retrievability, FsrsRating rating)
    {
        var hardPenalty = rating == FsrsRating.Hard ? w[15] : w[0].Tape.Const(1.0);
        var easyBonus = rating == FsrsRating.Easy ? w[16] : w[0].Tape.Const(1.0);
        return stability * (1.0 + Var.Exp(w[8]) * (11.0 - difficulty)
                                 * Var.Pow(stability, -w[9])
                                 * (Var.Exp((1.0 - retrievability) * w[10]) - 1.0)
                                 * hardPenalty * easyBonus);
    }

    private static Var DiffForgetStability(Var[] w, Var difficulty, Var stability, Var retrievability)
    {
        var longTerm = w[11]
                       * Var.Pow(difficulty, -w[12])
                       * (Var.Pow(stability + 1.0, w[13]) - 1.0)
                       * Var.Exp((1.0 - retrievability) * w[14]);
        var shortTerm = stability / Var.Exp(w[17] * w[18]);
        return Var.Min(longTerm, shortTerm);
    }

    private static void AddL2Gradient(double[] gradients, double[] parameters, double[] defaultParams,
        double gamma, int batchSize, int totalSize)
    {
        if (gamma <= 0) return;

        var scale = gamma * batchSize / totalSize;
        for (var i = 0; i < PARAMETER_COUNT; i++)
        {
            var diff = parameters[i] - defaultParams[i];
            var stddev = ParamsStdDev[i];
            gradients[i] += scale * 2.0 * diff / (stddev * stddev);
        }
    }

    private static void AdamStep(double[] parameters, double[] gradients,
        double[] m, double[] v, int t, double lr)
    {
        const double beta1 = 0.9;
        const double beta2 = 0.999;
        const double epsilon = 1e-8;

        for (var i = 0; i < PARAMETER_COUNT; i++)
        {
            m[i] = beta1 * m[i] + (1 - beta1) * gradients[i];
            v[i] = beta2 * v[i] + (1 - beta2) * gradients[i] * gradients[i];

            var mHat = m[i] / (1 - Math.Pow(beta1, t));
            var vHat = v[i] / (1 - Math.Pow(beta2, t));

            parameters[i] -= lr * mHat / (Math.Sqrt(vHat) + epsilon);
        }
    }

    private static double CosineAnnealingLr(double initialLr, int currentStep, int totalSteps)
    {
        if (totalSteps <= 1) return initialLr;
        return initialLr * 0.5 * (1 + Math.Cos(Math.PI * currentStep / totalSteps));
    }

    public static void ClampParameters(double[] parameters)
    {
        for (var i = 0; i < Math.Min(parameters.Length, PARAMETER_COUNT); i++)
        {
            parameters[i] = Math.Clamp(parameters[i], ParameterBounds[i].Min, ParameterBounds[i].Max);
        }
    }

    public static void EstimateInitialStability(List<FsrsTrainingItem> items, double[] parameters)
    {
        // Compute average recall across all first long-term reviews (for Laplace smoothing)
        var allRecalled = 0;
        var allTotal = 0;
        foreach (var item in items)
        {
            if (item.Reviews.Length < 2) continue;
            var deltaT = item.Reviews[1].DeltaT;
            if (deltaT < 1.0) continue;
            allTotal++;
            if (item.Reviews[1].Rating > 1) allRecalled++;
        }
        var avgRecall = allTotal > 0 ? (double)allRecalled / allTotal : 0.9;

        var estimated = new double?[4];

        for (var rating = 1; rating <= 4; rating++)
        {
            // Group by integer delta_t and compute binned recall rates
            var bins = new Dictionary<int, (int Recalled, int Total)>();
            foreach (var item in items)
            {
                if (item.Reviews.Length < 2) continue;
                if (item.Reviews[0].Rating != rating) continue;

                var deltaT = item.Reviews[1].DeltaT;
                if (deltaT < 1.0) continue;

                var bin = (int)Math.Round(deltaT);
                if (bin < 1) bin = 1;

                var recalled = item.Reviews[1].Rating > 1 ? 1 : 0;
                if (bins.TryGetValue(bin, out var existing))
                    bins[bin] = (existing.Recalled + recalled, existing.Total + 1);
                else
                    bins[bin] = (recalled, 1);
            }

            var totalReviews = bins.Values.Sum(b => b.Total);
            if (totalReviews < 4) continue;

            // Build smoothed data points from bins with Laplace smoothing
            var dataPoints = new List<(double DeltaT, double SmoothedRecall, int Count)>();
            foreach (var (bin, (recalled, total)) in bins)
            {
                var smoothedRecall = (recalled + avgRecall) / (total + 1.0);
                dataPoints.Add((bin, smoothedRecall, total));
            }

            var defaultS = FsrsConstants.DefaultParameters[rating - 1];

            // Ternary search on log-space for optimal stability
            var lo = -2.0;
            var hi = Math.Log10(ParameterBounds[0].Max);
            for (var iter = 0; iter < 60; iter++)
            {
                var m1 = lo + (hi - lo) / 3.0;
                var m2 = hi - (hi - lo) / 3.0;
                var loss1 = InitStabilityLoss(Math.Pow(10, m1), dataPoints, defaultS, parameters);
                var loss2 = InitStabilityLoss(Math.Pow(10, m2), dataPoints, defaultS, parameters);
                if (loss1 < loss2)
                    hi = m2;
                else
                    lo = m1;
            }
            var bestS = Math.Pow(10, (lo + hi) / 2.0);

            estimated[rating - 1] = Math.Clamp(bestS, ParameterBounds[rating - 1].Min, ParameterBounds[rating - 1].Max);
        }

        // smooth_and_fill: interpolate missing ratings using power-law relationships
        SmoothAndFill(estimated, parameters);

        for (var i = 0; i < 4; i++)
            parameters[i] = estimated[i] ?? parameters[i];

        // Enforce monotonicity: w0 <= w1 <= w2 <= w3 (Again <= Hard <= Good <= Easy)
        for (var i = 1; i < 4; i++)
        {
            if (parameters[i] < parameters[i - 1])
                parameters[i] = parameters[i - 1];
        }
    }

    private static double InitStabilityLoss(double candidateS, List<(double DeltaT, double SmoothedRecall, int Count)> dataPoints,
        double defaultS, double[] parameters)
    {
        var loss = 0.0;
        foreach (var (deltaT, smoothedRecall, count) in dataPoints)
        {
            var predicted = PowerForgettingCurve(deltaT, candidateS, parameters);
            loss += count * BinaryCrossEntropy(predicted, smoothedRecall);
        }
        // L1 regularization toward default
        loss += Math.Abs(candidateS - defaultS) / 16.0;
        return loss;
    }

    private static void SmoothAndFill(double?[] estimated, double[] defaults)
    {
        // Power-law exponents for interpolation between ratings (from fsrs-rs)
        const double w1 = 0.41;
        const double w2 = 0.54;

        // If we have at least one estimated value, fill missing ones using power-law ratios
        var hasAny = estimated.Any(e => e.HasValue);
        if (!hasAny) return;

        // Use the first available estimate as anchor and fill others
        // Ratio between adjacent ratings follows: s[r+1] / s[r] = ratio^power
        // We approximate using default ratios scaled by estimated values
        for (var i = 0; i < 4; i++)
        {
            if (estimated[i].HasValue) continue;

            // Find nearest estimated value and interpolate
            double? filled = null;
            var minDistance = int.MaxValue;
            for (var j = 0; j < 4; j++)
            {
                if (!estimated[j].HasValue) continue;
                var distance = Math.Abs(i - j);
                if (distance >= minDistance) continue;
                minDistance = distance;
                var defaultRatio = defaults[i] / Math.Max(defaults[j], 1e-10);
                var power = (i - j) > 0 ? w2 : w1;
                filled = estimated[j].Value * Math.Pow(Math.Max(defaultRatio, 1e-10), Math.Abs(power));
            }

            if (filled.HasValue)
                estimated[i] = Math.Clamp(filled.Value, ParameterBounds[i].Min, ParameterBounds[i].Max);
        }
    }

    private static double[] ComputeRecencyWeights(int count)
    {
        var weights = new double[count];
        for (var i = 0; i < count; i++)
        {
            var ratio = count > 1 ? (double)i / (count - 1) : 1.0;
            weights[i] = 0.25 + 0.75 * ratio * ratio * ratio;
        }
        return weights;
    }

    private static void FreezeShortTermGradients(double[] gradients)
    {
        gradients[17] = 0;
        gradients[18] = 0;
        gradients[19] = 0;
    }

    private static (List<FsrsTrainingItem>, List<double>) ShuffleItems(List<FsrsTrainingItem> items, double[] weights, int seed)
    {
        var rng = new Random(seed + 2023);
        var indices = Enumerable.Range(0, items.Count).ToArray();
        for (var i = indices.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }
        return (indices.Select(i => items[i]).ToList(), indices.Select(i => weights[i]).ToList());
    }

    public static List<FsrsTrainingItem> ConvertReviewLogs(
        IEnumerable<IGrouping<long, FsrsReviewLog>> reviewLogsByCard)
    {
        var items = new List<FsrsTrainingItem>();

        foreach (var group in reviewLogsByCard)
        {
            var logs = group.Where(l => l.Rating.IsValid()).OrderBy(l => l.ReviewDateTime).ToList();
            if (logs.Count < 2) continue;

            var reviews = new FsrsTrainingReview[logs.Count];
            reviews[0] = new FsrsTrainingReview((int)logs[0].Rating, 0);

            for (var i = 1; i < logs.Count; i++)
            {
                var deltaT = (logs[i].ReviewDateTime - logs[i - 1].ReviewDateTime).TotalDays;
                reviews[i] = new FsrsTrainingReview((int)logs[i].Rating, Math.Max(0, deltaT));
            }

            items.Add(new FsrsTrainingItem(reviews, logs[^1].ReviewDateTime.Ticks));
        }

        return items;
    }
}
