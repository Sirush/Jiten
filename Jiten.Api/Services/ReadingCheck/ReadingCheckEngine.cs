namespace Jiten.Api.Services.ReadingCheck;

public enum ReadingCheckAnswer : byte
{
    Knew = 0,
    Partly = 1,
    DidntKnow = 2
}

/// <summary><paramref name="Probeable"/> is false for homographs and irregular spellings: they are seeded from the fit but never asked. <paramref name="Offset"/> is the fixed logit adjustment from <see cref="ReadingCheckEngine.WordOffset"/>.</summary>
public readonly record struct ReadingCandidate(
    int WordId,
    byte ReadingIndex,
    int Occurrences,
    int Rank,
    int Works,
    bool Probeable = true,
    double Offset = 0,
    bool Expression = false)
{
    /// <summary>Words met fewer times are only asked, to find where a strong reader's knowledge ends; they are never marked from the fit.</summary>
    public bool Seedable => Occurrences >= ReadingCheckEngine.MinOccurrences;
}

/// <summary>MAP weights of P(known) = σ(A + B·ln(occurrences/20) + C·ln(rank/20000) + E·expression + offset) and their Laplace covariance.</summary>
public sealed record ReadingFit(double A, double B, double C, double E, double[,] Covariance)
{
    public double Logit(ReadingCandidate candidate)
    {
        var x = ReadingCheckEngine.Features(candidate);
        return A * x[0] + B * x[1] + C * x[2] + E * x[3] + candidate.Offset;
    }

    public double Probability(ReadingCandidate candidate) => ReadingCheckEngine.Sigmoid(Logit(candidate));

    public double LogitVariance(ReadingCandidate candidate)
    {
        var x = ReadingCheckEngine.Features(candidate);
        var total = 0.0;
        for (var i = 0; i < x.Length; i++)
            for (var j = 0; j < x.Length; j++)
                total += x[i] * Covariance[i, j] * x[j];
        return total;
    }
}

/// <summary>How much risk the reader accepts: the marked words keep an expected share of known words of at least <paramref name="Precision"/>.</summary>
public sealed record ReadingLevel(int Level, double Precision);

/// <summary>Words one level marks as known, most likely first; <paramref name="ExpectedWrong"/> is the fit's estimate of unknown words among them.</summary>
public sealed record ReadingSelection(int Level, List<ReadingCandidate> Known, double ExpectedWrong);

public static class ReadingCheckEngine
{
    public const int MinOccurrences = 5;
    public const int MinProbeOccurrences = 2;
    public const int MinAnswers = 63;
    public const int MaxAnswers = 98;
    public const int ReviewPageSize = 12;
    public const int UnrankedRank = 200_000;
    public const int MinUnits = 3;
    public const int MinCandidates = 2000;

    /// <summary>Lower bounds of the exposure bands; each round asks one word per band, so the answers show how much exposure matters to this reader.</summary>
    private static readonly int[] ExposureBands = [MinProbeOccurrences, MinOccurrences, 10, 20, 50, 100, 200];

    public static int RoundSize => ExposureBands.Length;

    /// <summary>Expressions are rare in a selection, so the first rounds each include one until their weight has this many answers behind it.</summary>
    public const int ExpressionProbes = 10;

    /// <summary>Each level marks the largest set, best first, whose expected share of known words meets its precision under the fit.</summary>
    public static readonly ReadingLevel[] Levels =
    [
        new(1, 0.95),
        new(2, 0.90),
        new(3, 0.80),
        new(4, 0.75),
        new(5, 0.70)
    ];

    public const int DefaultLevel = 2;

    public static ReadingLevel LevelOf(int? level) => Levels.FirstOrDefault(l => l.Level == level) ?? Levels.First(l => l.Level == DefaultLevel);

    private const double KnownWordFloor = 0.6;

    /// <summary>The check stops once the marked set moves by less than this share between rounds.</summary>
    private const double StableChange = 0.02;

    /// <summary>Outside this range the figure reflects a flat or saturated fit, not the reader, so it isn't shown.</summary>
    private const int MaxRetentionEncounters = 300;
    private const int MinRetentionEncounters = 3;
    private const double MinRetentionSlope = 0.05;

    /// <summary>Prior over intercept, exposure slope, rank slope and expression weight; a short check moves the last three only as far as its answers justify.</summary>
    private static readonly double[] PriorMean = [0, 0.8, -0.5, 0.5];
    private static readonly double[] PriorVariance = [9, 1, 1, 4];

    /// <summary>Kanji not used in the most common words; a word holding them is harder to know at the same exposure and rarity.</summary>
    public const int CommonKanjiRank = 4000;

    /// <summary>Fixed logit adjustments measured across users' cards (2026-11-08), the same for every reader so a short check never has to estimate them.</summary>
    private const double OneUncommonKanji = -0.35;
    private const double TwoUncommonKanji = -0.55;
    private const double VerbOffset = -0.25;

    public static double WordOffset(int uncommonKanji, bool verb)
        => (uncommonKanji >= 2 ? TwoUncommonKanji : uncommonKanji == 1 ? OneUncommonKanji : 0) + (verb ? VerbOffset : 0);

    /// <summary>All zero but the first for a plain word met 20 times at rank 20,000.</summary>
    public static double[] Features(ReadingCandidate c)
        => [1, Math.Log(Math.Max(1, c.Occurrences) / 20.0), Math.Log(Math.Max(1, c.Rank) / 20000.0), c.Expression ? 1 : 0];

    public static double Sigmoid(double z) => 1 / (1 + Math.Exp(-z));

    public static int Band(int occurrences) => Math.Max(0, Array.FindLastIndex(ExposureBands, lower => occurrences >= lower));

    /// <summary>One word per exposure band, taken from the nearest band that still has words; one of them an expression while <paramref name="expressionsAsked"/> is short of <see cref="ExpressionProbes"/>.</summary>
    public static List<ReadingCandidate> NextRound(IEnumerable<ReadingCandidate> pool, int expressionsAsked, Random random)
    {
        var bands = pool.GroupBy(c => Band(c.Occurrences)).ToDictionary(g => g.Key, g => g.ToList());
        if (bands.Count == 0) return [];

        var picked = new List<ReadingCandidate>(RoundSize);
        var expressionBand = expressionsAsked < ExpressionProbes
            ? bands.Where(b => b.Value.Any(c => c.Expression)).Select(b => (int?)b.Key).OrderBy(_ => random.Next()).FirstOrDefault()
            : null;

        for (var band = 0; band < RoundSize; band++)
        {
            var source = bands.Keys.Where(k => bands[k].Count > 0).OrderBy(k => Math.Abs(k - band)).ThenBy(k => k).Cast<int?>().FirstOrDefault();
            if (source == null) break;

            var words = bands[source.Value];
            var expressions = source == expressionBand ? words.Where(c => c.Expression).ToList() : [];
            if (source == expressionBand) expressionBand = null;
            var choices = expressions.Count > 0 ? expressions : words;
            var word = choices[random.Next(choices.Count)];
            words.Remove(word);
            picked.Add(word);
        }

        return picked;
    }

    /// <summary><paramref name="previousKnown"/> is negative before the first count.</summary>
    public static bool IsStable(int previousKnown, int known)
        => previousKnown >= 0 && (previousKnown == known || (previousKnown > 0 && Math.Abs(known - previousKnown) / (double)previousKnown < StableChange));

    /// <summary>What every level marks, from one pass over <paramref name="open"/>.</summary>
    public static List<ReadingSelection> Select(ReadingFit fit, IReadOnlyList<ReadingCandidate> open)
    {
        var logits = open.Select(fit.Logit).ToArray();
        var order = Enumerable.Range(0, open.Count).OrderByDescending(i => logits[i]).ToArray();
        var mean = order.Select(i => PosteriorMean(logits[i], fit.LogitVariance(open[i]))).TakeWhile(m => m >= KnownWordFloor).ToArray();

        var shares = new double[mean.Length];
        var sum = 0.0;
        for (var k = 0; k < shares.Length; k++)
        {
            sum += Sigmoid(logits[order[k]]);
            shares[k] = sum / (k + 1);
        }

        return Levels.Select(level =>
        {
            var known = Array.FindLastIndex(shares, share => share >= level.Precision) + 1;
            return new ReadingSelection(level.Level, order.Take(known).Select(i => open[i]).ToList(), mean.Take(known).Sum(m => 1 - m));
        }).ToList();
    }

    /// <summary>The askable words nearest the edge of what <paramref name="level"/> marks, half from each side, for the reader to settle one by one.</summary>
    public static List<ReadingCandidate> ReviewPage(ReadingFit fit, IReadOnlyList<ReadingCandidate> open, int level)
    {
        var marked = Select(fit, open).First(s => s.Level == level).Known;
        var markedKeys = marked.Select(c => (c.WordId, c.ReadingIndex)).ToHashSet();

        var inside = marked.AsEnumerable().Reverse().Where(c => c.Probeable).Take(ReviewPageSize).ToList();
        var outside = open.Where(c => c.Probeable && !markedKeys.Contains((c.WordId, c.ReadingIndex)))
                          .OrderByDescending(fit.Logit)
                          .Take(ReviewPageSize)
                          .ToList();

        var fromInside = Math.Min(inside.Count, Math.Max(ReviewPageSize / 2, ReviewPageSize - outside.Count));
        return inside.Take(fromInside).Concat(outside.Take(ReviewPageSize - fromInside)).ToList();
    }

    /// <summary>P(known) averaged over the fit's uncertainty, by the probit approximation to the logistic-normal integral.</summary>
    private static double PosteriorMean(double logit, double variance)
        => Sigmoid(logit / Math.Sqrt(1 + Math.PI * variance / 8));

    /// <summary>Occurrences at which a plain median-rank word reaches P(known) = 0.9.</summary>
    public static int? RetentionEncounters(ReadingFit fit, int medianRank)
    {
        const double retentionProbability = 0.9;
        if (fit.B < MinRetentionSlope) return null;

        var logit = Math.Log(retentionProbability / (1 - retentionProbability));
        var rankTerm = fit.C * Math.Log(Math.Max(1, medianRank) / 20000.0);
        var n = 20 * Math.Exp((logit - fit.A - rankTerm) / fit.B);
        return n is > MaxRetentionEncounters or < MinRetentionEncounters ? null : (int)Math.Round(n);
    }

    /// <summary>MAP weights by Newton-Raphson under the Gaussian prior.</summary>
    public static ReadingFit Fit(IReadOnlyList<(ReadingCandidate Candidate, bool Known)> answers)
    {
        var rows = answers.Select(a => (X: Features(a.Candidate), a.Candidate.Offset, a.Known)).ToList();
        var size = PriorMean.Length;
        var weights = (double[])PriorMean.Clone();
        var covariance = new double[size, size];

        for (var iter = 0; iter < 60; iter++)
        {
            var gradient = new double[size];
            var hessian = new double[size, size];
            for (var i = 0; i < size; i++)
            {
                gradient[i] = -(weights[i] - PriorMean[i]) / PriorVariance[i];
                hessian[i, i] = 1 / PriorVariance[i];
            }

            foreach (var (x, offset, known) in rows)
            {
                var p = Sigmoid(Dot(weights, x) + offset);
                var curvature = p * (1 - p);
                for (var i = 0; i < size; i++)
                {
                    gradient[i] += ((known ? 1 : 0) - p) * x[i];
                    for (var j = 0; j < size; j++)
                        hessian[i, j] += curvature * x[i] * x[j];
                }
            }

            covariance = Invert(hessian);
            var step = new double[size];
            for (var i = 0; i < size; i++)
                for (var j = 0; j < size; j++)
                    step[i] += covariance[i, j] * gradient[j];

            var current = LogPosterior(weights, rows);
            var scale = 1.0;
            while (scale > 1e-6 && LogPosterior(Moved(weights, step, scale), rows) < current)
                scale /= 2;
            weights = Moved(weights, step, scale);
            if (step.Max(Math.Abs) * scale < 1e-8) break;
        }

        return new ReadingFit(weights[0], weights[1], weights[2], weights[3], covariance);
    }

    private static double Dot(double[] a, double[] b)
    {
        var total = 0.0;
        for (var i = 0; i < a.Length; i++) total += a[i] * b[i];
        return total;
    }

    private static double[] Moved(double[] weights, double[] step, double scale)
        => weights.Select((w, i) => w + scale * step[i]).ToArray();

    /// <summary>Full Newton steps overshoot on nearly separable answers (all or almost all known), so each step is halved until this improves.</summary>
    private static double LogPosterior(double[] weights, List<(double[] X, double Offset, bool Known)> rows)
    {
        var total = 0.0;
        for (var i = 0; i < weights.Length; i++)
            total -= (weights[i] - PriorMean[i]) * (weights[i] - PriorMean[i]) / (2 * PriorVariance[i]);

        foreach (var (x, offset, known) in rows)
        {
            var eta = Dot(weights, x) + offset;
            var softplus = eta > 0 ? eta + Math.Log(1 + Math.Exp(-eta)) : Math.Log(1 + Math.Exp(eta));
            total += (known ? eta : 0) - softplus;
        }

        return total;
    }

    /// <summary>Gauss-Jordan inverse; the prior keeps the matrix positive definite, so no pivot is zero.</summary>
    private static double[,] Invert(double[,] matrix)
    {
        var n = matrix.GetLength(0);
        var work = (double[,])matrix.Clone();
        var inverse = new double[n, n];
        for (var i = 0; i < n; i++) inverse[i, i] = 1;

        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < n; row++)
                if (Math.Abs(work[row, col]) > Math.Abs(work[pivot, col]))
                    pivot = row;
            if (pivot != col)
                for (var k = 0; k < n; k++)
                {
                    (work[col, k], work[pivot, k]) = (work[pivot, k], work[col, k]);
                    (inverse[col, k], inverse[pivot, k]) = (inverse[pivot, k], inverse[col, k]);
                }

            var divisor = work[col, col];
            for (var k = 0; k < n; k++)
            {
                work[col, k] /= divisor;
                inverse[col, k] /= divisor;
            }

            for (var row = 0; row < n; row++)
            {
                if (row == col) continue;
                var factor = work[row, col];
                if (factor == 0) continue;
                for (var k = 0; k < n; k++)
                {
                    work[row, k] -= factor * work[col, k];
                    inverse[row, k] -= factor * inverse[col, k];
                }
            }
        }

        return inverse;
    }
}
