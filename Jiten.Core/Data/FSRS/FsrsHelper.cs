namespace Jiten.Core.Data.FSRS;

public static class FsrsHelper
{
    private static Random Random => Random.Shared;

    /// <summary>
    /// Calculates initial difficulty for a new card
    /// </summary>
    public static double CalculateInitialDifficulty(FsrsRating rating, double[] parameters)
    {
        var initialDifficulty = parameters[4] - Math.Exp(parameters[5] * ((int)rating - 1)) + 1;

        return ClampDifficulty(initialDifficulty);
    }

    /// <summary>
    /// Updates difficulty based on review performance
    /// </summary>
    public static double CalculateNextDifficulty(double difficulty, FsrsRating rating, double[] parameters)
    {
        var arg1 = CalculateInitialDifficulty(FsrsRating.Easy, parameters);
        var deltaDifficulty = -(parameters[6] * ((int)rating - 3));
        var arg2 = difficulty + LinearDamping(deltaDifficulty, difficulty);
        var nextDifficulty = MeanReversion(arg1, arg2, parameters[7]);

        return ClampDifficulty(nextDifficulty);
    }

    private static double LinearDamping(double deltaDifficulty, double difficulty)
    {
        return (10.0 - difficulty) * deltaDifficulty / 9.0;
    }

    private static double MeanReversion(double arg1, double arg2, double parameter)
    {
        return parameter * arg1 + (1 - parameter) * arg2;
    }

    private static double ClampDifficulty(double difficulty)
    {
        if (double.IsNaN(difficulty) || double.IsInfinity(difficulty))
            return 5.0;
        return Math.Clamp(difficulty, 1.0, 10.0);
    }

    /// <summary>
    /// Applies fuzzing to a review interval. When a <paramref name="loadBalancer"/> and
    /// <paramref name="anchorDate"/> are supplied, the fuzzed interval is chosen so the resulting due
    /// date lands on the least-loaded day within the fuzz window (ties resolved toward the un-fuzzed
    /// centre), instead of a uniformly random day. The window is identical either way, so this adds no
    /// scheduling deviation beyond ordinary fuzz — it only spreads day-to-day review load.
    /// </summary>
    /// <param name="interval">Base interval to fuzz</param>
    /// <param name="maximumInterval">Maximum allowed interval</param>
    /// <param name="anchorDate">Review time the interval is measured from (required for load balancing)</param>
    /// <param name="loadBalancer">Optional load balancer used to pick the least-loaded day</param>
    /// <returns>Fuzzed interval</returns>
    public static TimeSpan ApplyFuzzing(TimeSpan interval, int maximumInterval, DateTime? anchorDate = null,
                                        IFsrsLoadBalancer? loadBalancer = null, EasyDaysPolicy? easyDays = null)
    {
        var intervalDays = interval.Days;

        if (intervalDays < 2.5)
            return interval;

        var (minInterval, maxInterval) = GetFuzzRange(intervalDays, maximumInterval);

        if (loadBalancer != null && anchorDate.HasValue)
        {
            var balancedDays = SelectBalancedInterval(minInterval, maxInterval, intervalDays, anchorDate.Value, loadBalancer, easyDays);
            return TimeSpan.FromDays(balancedDays);
        }

        return TimeSpan.FromDays(Random.Next(minInterval, maxInterval + 1));
    }

    // Floor on an Easy-Days weekday weight, so a fully-avoided day stays finitely costly ("avoid if
    // possible") instead of dividing by zero.
    private const double MinEasyDayWeight = 0.01;
    private const double CostEpsilon = 1e-9;

    /// <summary>
    /// Picks the interval (in days, within [<paramref name="minDays"/>, <paramref name="maxDays"/>]) whose
    /// resulting due date has the lowest load relative to that day's capacity — i.e. the least-loaded day,
    /// weighted down on Easy-Days weekdays — breaking ties toward <paramref name="centerDays"/> (the
    /// un-fuzzed interval). With no Easy-Days policy and a flat calendar this returns the centre, reproducing
    /// no-fuzz behaviour with no early/late bias. Registers the chosen day so later cards in the same batch
    /// balance around it.
    /// </summary>
    private static int SelectBalancedInterval(int minDays, int maxDays, int centerDays, DateTime anchorDate,
                                              IFsrsLoadBalancer loadBalancer, EasyDaysPolicy? easyDays)
    {
        var bestDays = centerDays;
        var bestCost = double.MaxValue;
        var bestDistance = int.MaxValue;

        for (var days = minDays; days <= maxDays; days++)
        {
            var due = anchorDate.AddDays(days);
            var load = loadBalancer.GetLoad(due);
            var weight = easyDays?.Weight(due) ?? 1.0;
            var cost = (load + 1) / Math.Max(weight, MinEasyDayWeight);
            var distance = Math.Abs(days - centerDays);

            if (cost < bestCost - CostEpsilon || (Math.Abs(cost - bestCost) <= CostEpsilon && distance < bestDistance))
            {
                bestCost = cost;
                bestDistance = distance;
                bestDays = days;
            }
        }

        loadBalancer.Register(anchorDate.AddDays(bestDays));
        return bestDays;
    }

    private static (int Min, int Max) GetFuzzRange(int intervalDays, int maximumInterval)
    {
        var delta = 1.0;

        foreach (var fuzzRange in FsrsConstants.FuzzRanges)
        {
            delta += fuzzRange.Factor * Math.Max(
                                                 Math.Min(intervalDays, fuzzRange.End) - fuzzRange.Start, 0.0);
        }

        var minInterval = Math.Max(2, (int)Math.Round(intervalDays - delta));
        var maxInterval = Math.Min((int)Math.Round(intervalDays + delta), maximumInterval);
        minInterval = Math.Min(minInterval, maxInterval);

        return (minInterval, maxInterval);
    }

    /// <summary>
    /// Calculates the next review interval in days
    /// </summary>
    /// <param name="stability">Current card stability</param>
    /// <param name="desiredRetention">Target retention rate (0-1)</param>
    /// <param name="parameters">FSRS algorithm parameters</param>
    /// <param name="maximumInterval">Maximum allowed interval in days</param>
    /// <returns>Next review interval in days</returns>
    public static int CalculateNextInterval(double stability, double desiredRetention, double[] parameters, int maximumInterval)
    {
        var decay = -parameters[20];
        var factor = Math.Pow(0.9, 1.0 / decay) - 1;

        var nextInterval = (stability / factor) * (Math.Pow(desiredRetention, 1.0 / decay) - 1);
        var roundedInterval = (int)Math.Round(nextInterval);

        return Math.Clamp(roundedInterval, 1, maximumInterval);
    }

    /// <summary>
    /// Calculates the current retrievability (recall probability) of a card
    /// </summary>
    /// <param name="card">The card to calculate retrievability for</param>
    /// <param name="currentDateTime">Current time (defaults to now)</param>
    /// <param name="parameters">FSRS parameters (defaults to standard parameters)</param>
    /// <returns>Retrievability value between 0 and 1</returns>
    public static double CalculateRetrievability(FsrsCard card, DateTime? currentDateTime = null, double[]? parameters = null)
    {
        if (card.LastReview == null)
            return 0;

        currentDateTime ??= DateTime.UtcNow;
        parameters ??= FsrsConstants.DefaultParameters;

        var decay = -parameters[20];
        var factor = Math.Pow(0.9, 1.0 / decay) - 1;
        var elapsedDays = Math.Max(0, (currentDateTime.Value - card.LastReview.Value).TotalDays);

        var stability = card.Stability ?? 1.0d;
        
        return Math.Pow(1 + factor * elapsedDays / stability, decay);
    }

    /// <summary>
    /// Calculates initial stability for a new card
    /// </summary>
    public static double CalculateInitialStability(FsrsRating rating, double[] parameters)
    {
        var initialStability = parameters[(int)rating - 1];

        return ClampStability(initialStability);
    }

    /// <summary>
    /// Calculates stability for short-term reviews (within same day)
    /// </summary>
    public static double CalculateShortTermStability(double stability, FsrsRating rating, double[] parameters)
    {
        var shortTermStabilityIncrease = Math.Exp(parameters[17] * ((int)rating - 3 + parameters[18]))
                                         * Math.Pow(stability, -parameters[19]);

        if (rating is FsrsRating.Good or FsrsRating.Easy)
        {
            shortTermStabilityIncrease = Math.Max(shortTermStabilityIncrease, 1.0);
        }

        var shortTermStability = stability * shortTermStabilityIncrease;

        return ClampStability(shortTermStability);
    }

    /// <summary>
    /// Calculates stability for long-term reviews
    /// </summary>
    public static double CalculateNextStability(double difficulty, double stability, double retrievability, FsrsRating rating,
                                                double[] parameters)
    {
        var nextStability = rating == FsrsRating.Again
            ? FsrsHelper.CalculateNextForgetStability(difficulty, stability, retrievability, parameters)
            : FsrsHelper.CalculateNextRecallStability(difficulty, stability, retrievability, rating, parameters);

        return ClampStability(nextStability);
    }

    private static double CalculateNextForgetStability(double difficulty, double stability, double retrievability, double[] parameters)
    {
        var longTermParams = parameters[11]
                             * Math.Pow(difficulty, -parameters[12])
                             * (Math.Pow(stability + 1, parameters[13]) - 1)
                             * Math.Exp((1 - retrievability) * parameters[14]);

        var shortTermParams = stability / Math.Exp(parameters[17] * parameters[18]);

        return Math.Min(longTermParams, shortTermParams);
    }

    private static double CalculateNextRecallStability(double difficulty, double stability, double retrievability, FsrsRating rating,
                                                double[] parameters)
    {
        var hardPenalty = rating == FsrsRating.Hard ? parameters[15] : 1;
        var easyBonus = rating == FsrsRating.Easy ? parameters[16] : 1;

        return stability * (1 + Math.Exp(parameters[8]) * (11 - difficulty)
                                                        * Math.Pow(stability, -parameters[9])
                                                        * (Math.Exp((1 - retrievability) * parameters[10]) - 1)
                                                        * hardPenalty * easyBonus);
    }

    private static double ClampStability(double stability)
    {
        if (double.IsNaN(stability) || double.IsNegativeInfinity(stability))
            return FsrsConstants.StabilityMin;
        if (double.IsPositiveInfinity(stability))
            return FsrsConstants.StabilityMax;
        return Math.Clamp(stability, FsrsConstants.StabilityMin, FsrsConstants.StabilityMax);
    }
}