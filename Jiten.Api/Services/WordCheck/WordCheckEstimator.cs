namespace Jiten.Api.Services.WordCheck;

/// <summary>Fits how the share of known words falls with frequency rank from every answer at once, so no single unlucky round decides the result.</summary>
public static class WordCheckEstimator
{
    /// <summary>Share of the whole marked range, ranks 1 to the cutoff, that must be known; wrong marks gather near the cutoff.</summary>
    public const double RangeKnownShare = 0.9;

    /// <summary>Share of words missed even well inside known territory, so one stray miss on a common word doesn't collapse the fit.</summary>
    private const double Lapse = 0.03;

    private const int CutoffStep = 50;
    private const int MinCutoff = 100;
    private const int HalfRankSteps = 90;
    private const double LowestHalfRank = 30;
    private const double HighestHalfRank = 150000;

    private static readonly double[] Slopes = [0.8, 1.2, 1.6, 2.0, 2.5, 3.0, 4.0, 5.0];

    private static readonly double[] LogHalfRanks = Enumerable.Range(0, HalfRankSteps)
                                                              .Select(i => Math.Log(LowestHalfRank)
                                                                           + i * (Math.Log(HighestHalfRank) - Math.Log(LowestHalfRank)) / (HalfRankSteps - 1))
                                                              .ToArray();

    private static readonly int[] Cutoffs = Enumerable.Range(0, (WordCheckEngine.MaxCutoffRank - MinCutoff) / CutoffStep + 1)
                                                      .Select(i => MinCutoff + i * CutoffStep)
                                                      .ToArray();

    private static readonly Lazy<double[][]> RangeShares = new(BuildRangeShares);

    /// <summary>Largest rank whose whole range is expected to be known at <see cref="RangeKnownShare"/>; 0 when not even the lowest range is.</summary>
    public static int Cutoff(IReadOnlyCollection<WordCheckObservation> observations, double falseAlarmRate)
    {
        var weights = Posterior(observations, falseAlarmRate);
        var shares = RangeShares.Value;

        for (var c = Cutoffs.Length - 1; c >= 0; c--)
        {
            var expected = 0.0;
            for (var cell = 0; cell < weights.Length; cell++)
                expected += weights[cell] * shares[cell][c];

            if (expected >= RangeKnownShare) return Cutoffs[c];
        }

        return 0;
    }

    private static double[] Posterior(IReadOnlyCollection<WordCheckObservation> observations, double falseAlarmRate)
    {
        var logs = new double[LogHalfRanks.Length * Slopes.Length];
        foreach (var observation in observations)
        {
            var logRank = Math.Log(Math.Max(1, observation.Rank));
            for (var h = 0; h < LogHalfRanks.Length; h++)
            for (var s = 0; s < Slopes.Length; s++)
            {
                var claimed = falseAlarmRate + (1 - falseAlarmRate) * Known(logRank, LogHalfRanks[h], Slopes[s]);
                logs[h * Slopes.Length + s] += Math.Log(Math.Clamp(observation.Known ? claimed : 1 - claimed, 1e-9, 1));
            }
        }

        var max = logs.Max();
        var total = 0.0;
        for (var i = 0; i < logs.Length; i++)
        {
            logs[i] = Math.Exp(logs[i] - max);
            total += logs[i];
        }

        for (var i = 0; i < logs.Length; i++)
            logs[i] /= total;

        return logs;
    }

    private static double Known(double logRank, double logHalfRank, double slope)
        => (1 - Lapse) / (1 + Math.Exp(slope * (logRank - logHalfRank)));

    private static double[][] BuildRangeShares()
    {
        var logRanks = Enumerable.Range(1, WordCheckEngine.MaxCutoffRank).Select(r => Math.Log(r)).ToArray();
        var shares = new double[LogHalfRanks.Length * Slopes.Length][];

        for (var h = 0; h < LogHalfRanks.Length; h++)
        for (var s = 0; s < Slopes.Length; s++)
        {
            var row = new double[Cutoffs.Length];
            var sum = 0.0;
            var next = 0;
            for (var rank = 1; rank <= WordCheckEngine.MaxCutoffRank; rank++)
            {
                sum += Known(logRanks[rank - 1], LogHalfRanks[h], Slopes[s]);
                if (next < Cutoffs.Length && rank == Cutoffs[next])
                    row[next++] = sum / rank;
            }

            shares[h * Slopes.Length + s] = row;
        }

        return shares;
    }
}
