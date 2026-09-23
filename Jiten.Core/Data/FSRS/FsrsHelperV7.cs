namespace Jiten.Core.Data.FSRS;

/// <summary>FSRS-7 memory state. Retrievability depends on all three fields, so none of them can be dropped by a caller.</summary>
public readonly record struct FsrsMemoryStateV7(double Stability, double Difficulty, double StabilityFast);

public static class FsrsHelperV7
{
    public const int ParameterCount = 34;
    public const double StabilityMin = FsrsConstants.StabilityMinV7;
    public const double StabilityMax = FsrsConstants.StabilityMax;
    public const double DifficultyMin = 1.0;
    public const double DifficultyMax = 10.0;

    /// <summary>A new fast trace starts, and an Again caps it, at this fraction of the slow trace.</summary>
    private const double FastTraceRatio = 0.8;

    private const int SlowBlock = 7;
    private const int FastBlock = 15;

    private const double DesiredRetentionMin = 0.0001;
    private const double DesiredRetentionMax = 0.9999;
    private const int NewtonIterations = 7;
    private const int BisectionIterations = 50;
    private const double MinIntervalDays = 1.0 / 86_400.0;

    /// <summary>A zero solve (Again at high retention) would make the card due again the instant it was answered.</summary>
    private const double MinReviewIntervalDays = 1.0 / 1_440.0;
    private const double NewtonAcceptTolerance = 1e-3;
    private const double Sm2RetentionTolerance = 1e-6;
    private const double InitialStabilityMax = 100.0;

    public static FsrsMemoryStateV7 InitialState(FsrsRating rating, double[] w)
    {
        var stability = Math.Clamp(w[RatingIndex(rating) - 1], StabilityMin, StabilityMax);
        return new FsrsMemoryStateV7(stability,
                                     Math.Clamp(InitialDifficulty(RatingIndex(rating), w), DifficultyMin, DifficultyMax),
                                     Math.Clamp(stability * FastTraceRatio, StabilityMin, StabilityMax));
    }

    /// <summary>State after a review <paramref name="deltaT"/> fractional days after the previous one; same-day reviews need no special case.</summary>
    public static FsrsMemoryStateV7 NextState(FsrsMemoryStateV7 state, double deltaT, FsrsRating rating, double[] w)
    {
        var last = Clamp(state);
        var g = RatingIndex(rating);
        deltaT = Math.Max(0, deltaT);

        var retrievability = Retrievability(deltaT, last, w);
        var slow = BlockStability(w, last.Stability, last.Difficulty, retrievability, g, SlowBlock);
        var fast = BlockStability(w, last.StabilityFast, last.Difficulty, FastRecall(deltaT, last.StabilityFast, w), g, FastBlock);
        if (g == 1)
            fast = Math.Min(fast, slow * FastTraceRatio);

        return new FsrsMemoryStateV7(slow,
                                     NextDifficulty(last.Difficulty, g, retrievability, w),
                                     Math.Clamp(fast, StabilityMin, StabilityMax));
    }

    /// <summary>Recall probability <paramref name="t"/> fractional days after the last review, squashed into [1e-5, 1 - 1e-5].</summary>
    public static double Retrievability(double t, FsrsMemoryStateV7 state, double[] w)
        => RetrievabilityAndDerivative(t, state, w).Retrievability;

    public static (double Retrievability, double Derivative) RetrievabilityAndDerivative(double t, FsrsMemoryStateV7 state, double[] w)
    {
        t = Math.Max(0, t);
        var s = Math.Max(state.Stability, StabilityMin);
        var sFast = Math.Max(state.StabilityFast, StabilityMin);
        var d = Math.Clamp(state.Difficulty, DifficultyMin, DifficultyMax);

        var (decay1, factor1) = FastCurve(sFast, w);
        var b1 = 1 + factor1 * (t / sFast);
        var r1 = Math.Pow(b1, decay1);
        var dr1 = decay1 * Math.Pow(b1, decay1 - 1) * factor1 / sFast;

        var decay2 = -Math.Clamp(w[24], 0.01, 0.95);
        var factor2 = Math.Pow(w[26], 1 / decay2) - 1;
        var timescale = Math.Exp((d - 5) * (w[32] - 0.3));
        var b2 = 1 + factor2 * timescale * (t / s);
        var r2 = Math.Pow(b2, decay2);
        var dr2 = decay2 * Math.Pow(b2, decay2 - 1) * factor2 * timescale / s;

        var weight1 = w[27] * Math.Pow(sFast, -w[29]);
        var weight2 = w[28] * Math.Pow(s, w[30]) * Math.Exp((d - 5) * (w[31] - 0.5));
        var weightSum = Math.Max(weight1 + weight2, 1e-9);

        var retrievability = (weight1 * r1 + weight2 * r2) / weightSum;
        var derivative = (weight1 * dr1 + weight2 * dr2) / weightSum;
        return (retrievability * (1 - 2e-5) + 1e-5, derivative * (1 - 2e-5));
    }

    /// <summary>Fractional days until retrievability falls to <paramref name="desiredRetention"/>; 0 at the retention ceiling.</summary>
    public static double NextInterval(FsrsMemoryStateV7 state, double desiredRetention, double[] w)
    {
        desiredRetention = Math.Clamp(desiredRetention, DesiredRetentionMin, DesiredRetentionMax);
        if (desiredRetention >= DesiredRetentionMax)
            return 0;

        state = Clamp(state);
        var minLogT = Math.Log(MinIntervalDays);
        var maxLogT = Math.Log(StabilityMax);
        var logT = Math.Log(Math.Max(Math.Max(state.Stability, state.StabilityFast), MinIntervalDays));

        // Newton on log t converges in a few steps for well-behaved states; the bisection below catches the rest.
        for (var i = 0; i < NewtonIterations; i++)
        {
            logT = Math.Clamp(logT, minLogT, maxLogT);
            var t = Math.Clamp(Math.Exp(logT), MinIntervalDays, StabilityMax);
            var (r, derivative) = RetrievabilityAndDerivative(t, state, w);
            var slope = Math.Min(derivative * t, -1e-12);
            logT -= Math.Clamp((r - desiredRetention) / slope, -4, 4);
            if (!double.IsFinite(logT))
                return BisectInterval(state, desiredRetention, w);
        }

        var interval = Math.Clamp(Math.Exp(logT), 0, StabilityMax);
        var reached = Retrievability(interval, state, w);
        return double.IsFinite(reached) && Math.Abs(reached - desiredRetention) <= NewtonAcceptTolerance
            ? interval
            : BisectInterval(state, desiredRetention, w);
    }

    /// <summary>Review interval in fractional days, capped at the user's maximum.</summary>
    public static double NextIntervalDays(FsrsMemoryStateV7 state, double desiredRetention, double[] w, int maximumInterval)
        => Math.Clamp(NextInterval(state, desiredRetention, w), MinReviewIntervalDays, maximumInterval);

    /// <summary>A null fast trace means FSRS-6 last wrote the card; it reads as the initial ratio until a states-only recompute.</summary>
    public static FsrsMemoryStateV7 FromCard(FsrsCard card)
    {
        var stability = card.Stability ?? 1.0;
        return new FsrsMemoryStateV7(stability, card.Difficulty ?? 1.0, card.StabilityFast ?? stability * FastTraceRatio);
    }

    /// <summary>Days until R = 90%: FSRS-6's meaning of stability, and what displays and day thresholds must read under FSRS-7.</summary>
    public static double S90(FsrsMemoryStateV7 state, double[] w) => NextInterval(state, 0.9, w);

    /// <summary>SM-2/Anki bridge: the D = 5 state whose curve reaches <paramref name="retention"/> at <paramref name="interval"/>.</summary>
    public static FsrsMemoryStateV7 FromSm2(double interval, double retention, double[] w)
    {
        if (!double.IsFinite(interval) || !double.IsFinite(retention) || retention is < 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(retention), "Interval must be finite and retention in [0, 1).");

        interval = Math.Clamp(interval, StabilityMin, StabilityMax);

        FsrsMemoryStateV7 StateAt(double logStability)
        {
            var stability = Math.Clamp(Math.Exp(logStability), StabilityMin, StabilityMax);
            return new FsrsMemoryStateV7(stability, 5.0, Math.Clamp(stability * FastTraceRatio, StabilityMin, StabilityMax));
        }

        var low = Math.Log(StabilityMin);
        var high = Math.Log(StabilityMax);
        if (retention <= Retrievability(interval, StateAt(low), w))
            return StateAt(low);
        if (retention >= Retrievability(interval, StateAt(high), w))
            return StateAt(high);

        for (var i = 0; i < BisectionIterations; i++)
        {
            var mid = (low + high) * 0.5;
            var state = StateAt(mid);
            var r = Retrievability(interval, state, w);
            if (Math.Abs(r - retention) <= Sm2RetentionTolerance)
                return state;
            if (r < retention)
                low = mid;
            else
                high = mid;
        }

        return StateAt((low + high) * 0.5);
    }

    /// <summary>fsrs-rs parameter_clipper_v7.rs; non-finite values fall to the lower bound.</summary>
    public static void ClampParameters(double[] w)
    {
        if (w.Length < ParameterCount)
            return;

        w[0] = ClampSafe(w[0], StabilityMin, InitialStabilityMax / 2);
        w[1] = ClampSafe(w[1], w[0], InitialStabilityMax);
        w[2] = ClampSafe(w[2], w[1], InitialStabilityMax);
        w[3] = ClampSafe(w[3], w[2], InitialStabilityMax);

        w[4] = ClampSafe(w[4], 1.0, 10.0);
        w[5] = ClampSafe(w[5], 0.001, 4.0);
        w[6] = ClampSafe(w[6], 0.1, 4.0);

        w[7] = ClampSafe(w[7], 0.0, 4.0);
        w[8] = ClampSafe(w[8], 0.0, 1.2);
        w[9] = ClampSafe(w[9], 0.3, 3.0);
        w[10] = ClampSafe(w[10], 0.01, 1.5);
        w[11] = ClampSafe(w[11], 0.1, 1.0);
        w[12] = ClampSafe(w[12], 0.0, 3.5);
        w[13] = ClampSafe(w[13], 0.0, 1.0);
        w[14] = ClampSafe(w[14], 1.0, 7.0);

        w[15] = ClampSafe(w[15], 0.0, 4.0);
        w[16] = ClampSafe(w[16], 0.0, 2.0);
        w[17] = ClampSafe(w[17], 0.5, 6.0);
        w[18] = ClampSafe(w[18], 0.001, 1.5);
        w[19] = ClampSafe(w[19], 0.001, 1.0);
        w[20] = ClampSafe(w[20], 0.0, 5.0);
        w[21] = ClampSafe(w[21], 0.0, 1.0);
        w[22] = ClampSafe(w[22], 1.0, 7.0);

        w[23] = ClampSafe(w[23], 0.01, 0.25);
        w[24] = ClampSafe(w[24], 0.01, 0.95);
        w[25] = ClampSafe(w[25], 0.2, 0.85);
        w[26] = ClampSafe(w[26], w[25], 0.99);
        w[27] = ClampSafe(w[27], 0.01, 1.0);
        w[28] = ClampSafe(w[28], 0.1, 1.0);
        w[29] = ClampSafe(w[29], 0.0, 0.9);
        w[30] = ClampSafe(w[30], 0.1, 1.1);
        w[31] = ClampSafe(w[31], 0.0, 1.0);
        w[32] = ClampSafe(w[32], 0.0, 0.6);
        w[33] = ClampSafe(w[33], 0.0, 0.6);
    }

    private static double InitialDifficulty(int g, double[] w) => w[4] - Math.Exp(w[5] * (g - 1)) + 1;

    private static double NextDifficulty(double d, int g, double retrievability, double[] w)
    {
        var delta = -w[6] * (g - 3);
        if (g == 1)
            delta *= retrievability + 0.1;

        var damped = d + (10 - d) * delta / 9;
        var reverted = 0.01 * InitialDifficulty(4, w) + 0.99 * damped;
        return Math.Clamp(reverted, DifficultyMin, DifficultyMax);
    }

    /// <summary>Shared stability update for one trace; <paramref name="start"/> selects the slow (7) or fast (15) parameter block.</summary>
    private static double BlockStability(double[] w, double s, double d, double r, int g, int start)
    {
        var hardPenalty = g == 2 ? w[start + 6] : 1;
        var easyBonus = g == 4 ? w[start + 7] : 1;

        var failStability = w[start + 3] * (Math.Pow(s + 1, w[start + 4]) - 1) * Math.Exp((1 - r) * w[start + 5]);
        var postLapse = Math.Min(s, failStability);
        var increase = Math.Exp(w[start] - 1.5)
                       * (11 - d)
                       * Math.Pow(s, -w[start + 1])
                       * (Math.Exp((1 - r) * w[start + 2]) - 1)
                       * hardPenalty
                       * easyBonus
                       + 1;
        var success = Math.Max(postLapse, s * increase);
        return Math.Clamp(g > 1 ? success : postLapse, StabilityMin, StabilityMax);
    }

    private static double FastRecall(double t, double sFast, double[] w)
    {
        sFast = Math.Clamp(sFast, StabilityMin, StabilityMax);
        var (decay1, factor1) = FastCurve(sFast, w);
        return Math.Pow(1 + factor1 * (Math.Max(0, t) / sFast), decay1);
    }

    private static (double Decay, double Factor) FastCurve(double sFast, double[] w)
    {
        var decay = -Math.Clamp(w[23] * Math.Pow(sFast, w[33] - 0.3), 0.01, 0.95);
        var factor = Math.Exp(Math.Min(Math.Log(w[25]) / decay, 60)) - 1;
        return (decay, factor);
    }

    private static double BisectInterval(FsrsMemoryStateV7 state, double desiredRetention, double[] w)
    {
        var low = 0.0;
        var high = Math.Max(Math.Max(state.Stability, state.StabilityFast), 1);
        while (Retrievability(high, state, w) > desiredRetention && high < StabilityMax)
            high = Math.Min(high * 2, StabilityMax);

        for (var i = 0; i < BisectionIterations; i++)
        {
            var mid = (low + high) * 0.5;
            if (Retrievability(mid, state, w) > desiredRetention)
                low = mid;
            else
                high = mid;
        }

        return Math.Clamp((low + high) * 0.5, 0, StabilityMax);
    }

    private static FsrsMemoryStateV7 Clamp(FsrsMemoryStateV7 state)
        => new(Math.Clamp(state.Stability, StabilityMin, StabilityMax),
               Math.Clamp(state.Difficulty, DifficultyMin, DifficultyMax),
               Math.Clamp(state.StabilityFast, StabilityMin, StabilityMax));

    private static int RatingIndex(FsrsRating rating) => Math.Clamp((int)rating, 1, 4);

    private static double ClampSafe(double value, double low, double high)
    {
        if (!double.IsFinite(low)) low = 0;
        if (!double.IsFinite(high)) high = low;
        if (low > high) (low, high) = (high, low);
        return Math.Clamp(double.IsFinite(value) ? value : low, low, high);
    }
}
