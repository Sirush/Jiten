using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jiten.Core.Data.FSRS;

namespace Jiten.Cli.Commands;

public class FsrsEvalCommands
{
    private const double TrainFraction = 0.8;

    private record Review(DateTime Utc, int Rating);

    private record UserData(int UserIdx, int ParamCount, List<Review[]> Cards);

    private record Prediction(double P, double Y, bool SameDay, (double DeltaT, double Index, double Lapses) Bin);

    private interface IEvalModel
    {
        string Name { get; }
        double[] Train(List<FsrsTrainingItem> items);

        /// <summary>Predicted recall for each review of a card's history; index 0 is unused.</summary>
        double[] Predict(FsrsTrainingReview[] reviews, double[] parameters);
    }

    private sealed class Fsrs6Defaults : IEvalModel
    {
        public string Name => "fsrs6-defaults";
        public double[] Train(List<FsrsTrainingItem> items) => (double[])FsrsConstants.DefaultParameters.Clone();
        public double[] Predict(FsrsTrainingReview[] reviews, double[] parameters) => PredictFsrs6(reviews, parameters);
    }

    private sealed class Fsrs6Optimised : IEvalModel
    {
        public string Name => "fsrs6-optimised";
        public double[] Train(List<FsrsTrainingItem> items) => FsrsOptimizer.Optimize(items).Parameters;
        public double[] Predict(FsrsTrainingReview[] reviews, double[] parameters) => PredictFsrs6(reviews, parameters);
    }

    /// <summary>Reference floor: predicts the training period's interday pass rate for every review.</summary>
    private sealed class ConstantPassRate : IEvalModel
    {
        public string Name => "constant";

        public double[] Train(List<FsrsTrainingItem> items)
        {
            var interday = items.SelectMany(i => i.Reviews.Skip(1)).Where(r => r.DeltaT >= 1.0).ToList();
            return [interday.Count == 0 ? 0.9 : interday.Count(r => r.Rating > 1) / (double)interday.Count];
        }

        public double[] Predict(FsrsTrainingReview[] reviews, double[] parameters) =>
            Enumerable.Repeat(parameters[0], reviews.Length).ToArray();
    }

    private class Fsrs7Defaults : IEvalModel
    {
        public virtual string Name => "fsrs7-defaults";
        public virtual double[] Train(List<FsrsTrainingItem> items) => (double[])FsrsConstants.DefaultParametersV7.Clone();

        public double[] Predict(FsrsTrainingReview[] reviews, double[] parameters)
        {
            var result = new double[reviews.Length];
            var state = FsrsHelperV7.InitialState((FsrsRating)reviews[0].Rating, parameters);
            for (var i = 1; i < reviews.Length; i++)
            {
                result[i] = FsrsHelperV7.Retrievability(reviews[i].DeltaT, state, parameters);
                state = FsrsHelperV7.NextState(state, reviews[i].DeltaT, (FsrsRating)reviews[i].Rating, parameters);
            }

            return result;
        }
    }

    private sealed class Fsrs7Optimised(string name, FsrsOptimizerV7Config config) : Fsrs7Defaults
    {
        public override string Name => name;

        public override double[] Train(List<FsrsTrainingItem> items) => FsrsOptimizerV7.Optimize(items, config).Parameters;
    }

    /// <summary>Mirrors FsrsOptimizer.ForwardPass, but also scores same-day reviews with the curve FSRS-6 would use.</summary>
    private static double[] PredictFsrs6(FsrsTrainingReview[] reviews, double[] parameters)
    {
        var result = new double[reviews.Length];
        var first = (FsrsRating)reviews[0].Rating;
        var stability = FsrsHelper.CalculateInitialStability(first, parameters);
        var difficulty = FsrsHelper.CalculateInitialDifficulty(first, parameters);

        for (var i = 1; i < reviews.Length; i++)
        {
            var rating = (FsrsRating)reviews[i].Rating;
            var deltaT = reviews[i].DeltaT;
            var r = FsrsOptimizer.PowerForgettingCurve(deltaT, stability, parameters);
            result[i] = r;

            if (deltaT < 1.0)
                stability = FsrsHelper.CalculateShortTermStability(stability, rating, parameters);
            else
                stability = FsrsHelper.CalculateNextStability(difficulty, stability, r, rating, parameters);
            difficulty = FsrsHelper.CalculateNextDifficulty(difficulty, rating, parameters);
        }

        return result;
    }

    public async Task Run(CliOptions options)
    {
        var dir = options.FsrsEval!;
        var users = LoadUsers(dir);
        if (options.FsrsEvalMaxUsers is { } max)
            users = users.Take(max).ToList();

        Console.WriteLine($"Loaded {users.Count} users, {users.Sum(u => u.Cards.Sum(c => c.Length)):N0} reviews, {users.Sum(u => u.Cards.Count):N0} cards");

        IEvalModel[] models = [new ConstantPassRate(), new Fsrs6Defaults(), new Fsrs6Optimised(), new Fsrs7Defaults(), new Fsrs7Optimised("fsrs7-optimised", new FsrsOptimizerV7Config()),
            new Fsrs7Optimised("fsrs7-opt-nopenalty", new FsrsOptimizerV7Config { SchedulePenalties = false }),
            new Fsrs7Optimised("fsrs7-opt-nooutlier", new FsrsOptimizerV7Config { FilterOutliers = false })];
        var perUser = new List<object>();
        var pooled = models.ToDictionary(m => m.Name, _ => new List<Prediction>());
        var perUserMetrics = models.ToDictionary(m => m.Name, _ => new List<(Metrics Inter, Metrics Same, int ParamCount)>());

        foreach (var user in users)
        {
            var (train, test, cutoff) = Split(user);
            var userModels = new Dictionary<string, object>();

            foreach (var model in models)
            {
                var sw = Stopwatch.StartNew();
                var parameters = model.Train(train);
                var trainMs = sw.ElapsedMilliseconds;

                var predictions = new List<Prediction>();
                foreach (var (reviews, firstTestIndex) in test)
                {
                    var p = model.Predict(reviews, parameters);
                    var interdayCount = 0;
                    var lapses = 0;
                    for (var i = 1; i < reviews.Length; i++)
                    {
                        var interday = reviews[i].DeltaT >= 1.0;
                        if (interday) interdayCount++;
                        if (i >= firstTestIndex)
                            predictions.Add(new Prediction(p[i], reviews[i].Rating > 1 ? 1 : 0, !interday,
                                                           BenchmarkBin(reviews[i].DeltaT, interdayCount + 1, lapses)));
                        if (interday && reviews[i].Rating == 1) lapses++;
                    }
                }

                pooled[model.Name].AddRange(predictions);
                var inter = Metrics.From(predictions.Where(p => !p.SameDay));
                var same = Metrics.From(predictions.Where(p => p.SameDay));
                perUserMetrics[model.Name].Add((inter, same, user.ParamCount));
                userModels[model.Name] = new { parameters, trainMs, interday = inter, sameDay = same };
            }

            perUser.Add(new
            {
                user.UserIdx, user.ParamCount, cutoff,
                trainItems = train.Count, testCards = test.Count,
                models = userModels
            });

            var line = string.Join("  ", models.Select(m =>
            {
                var last = perUserMetrics[m.Name][^1];
                return $"{m.Name}: LL {last.Inter.LogLoss:F4} RMSE {last.Inter.Rmse:F4} AUC {last.Inter.Auc:F4}";
            }));
            Console.WriteLine($"user {user.UserIdx,2} ({user.ParamCount,2}p, {user.Cards.Sum(c => c.Length),7:N0} rev)  {line}");
        }

        var summary = models.ToDictionary(m => m.Name, m => new
        {
            pooledInterday = Metrics.From(pooled[m.Name].Where(p => !p.SameDay)),
            pooledSameDay = Metrics.From(pooled[m.Name].Where(p => p.SameDay)),
            meanInterday = Metrics.Mean(perUserMetrics[m.Name].Select(x => x.Inter)),
            meanInterdayNeverOptimised = Metrics.Mean(perUserMetrics[m.Name].Where(x => x.ParamCount == 0).Select(x => x.Inter)),
            meanSameDay = Metrics.Mean(perUserMetrics[m.Name].Select(x => x.Same)),
        });

        Console.WriteLine();
        Console.WriteLine($"{"model",-18} {"scope",-26} {"n",10} {"LogLoss",8} {"RMSE",8} {"AUC",8}");
        foreach (var (name, s) in summary)
        {
            PrintRow(name, "pooled interday", s.pooledInterday);
            PrintRow(name, "per-user mean interday", s.meanInterday);
            PrintRow(name, "  never-optimised users", s.meanInterdayNeverOptimised);
            PrintRow(name, "pooled same-day", s.pooledSameDay);
        }

        var output = options.FsrsEvalOutput ?? Path.Combine(dir, $"fsrs_eval_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            createdUtc = DateTime.UtcNow,
            trainFraction = TrainFraction,
            rmse = "srs-benchmark rmse_matrix bins (elapsed days, interday review index, prior interday lapses)",
            models = models.Select(m => m.Name),
            summary,
            users = perUser
        }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        Console.WriteLine($"\nWrote {output}");
    }

    private static void PrintRow(string model, string scope, Metrics m) =>
        Console.WriteLine($"{model,-18} {scope,-26} {m.Count,10:N0} {m.LogLoss,8:F4} {m.Rmse,8:F4} {m.Auc,8:F4}");

    /// <summary>Time split: training sees only pre-cutoff history; later reviews are predicted from each card's full prior history.</summary>
    private static (List<FsrsTrainingItem> Train, List<(FsrsTrainingReview[] Reviews, int FirstTestIndex)> Test, DateTime Cutoff)
        Split(UserData user)
    {
        var allTimes = user.Cards.SelectMany(c => c.Select(r => r.Utc)).OrderBy(t => t).ToArray();
        var cutoff = allTimes[(int)(allTimes.Length * TrainFraction)];

        var train = new List<FsrsTrainingItem>();
        var test = new List<(FsrsTrainingReview[], int)>();

        foreach (var card in user.Cards)
        {
            var reviews = ToTrainingReviews(card);
            var firstTest = Array.FindIndex(card, r => r.Utc >= cutoff);
            var trainLength = firstTest < 0 ? card.Length : firstTest;

            if (trainLength >= 2)
                train.Add(new FsrsTrainingItem(reviews[..trainLength], card[trainLength - 1].Utc.Ticks));

            if (firstTest >= 0 && card.Length >= 2 && Math.Max(1, firstTest) < card.Length)
                test.Add((reviews, firstTest));
        }

        return (train, test, cutoff);
    }

    /// <summary>srs-benchmark utils.rmse_matrix bucketing, so RMSE(bins) matches the published tables' definition.</summary>
    private static (double, double, double) BenchmarkBin(double deltaT, int index, int lapses) =>
    (
        Math.Round(2.48 * Math.Pow(3.62, Math.Floor(Math.Log(Math.Max(deltaT, 1e-6)) / Math.Log(3.62))), 2),
        Math.Round(1.99 * Math.Pow(1.89, Math.Floor(Math.Log(index) / Math.Log(1.89)))),
        lapses == 0 ? 0 : Math.Round(1.65 * Math.Pow(1.73, Math.Floor(Math.Log(lapses) / Math.Log(1.73))))
    );

    private static FsrsTrainingReview[] ToTrainingReviews(Review[] card)
    {
        var reviews = new FsrsTrainingReview[card.Length];
        reviews[0] = new FsrsTrainingReview(card[0].Rating, 0);
        for (var i = 1; i < card.Length; i++)
            reviews[i] = new FsrsTrainingReview(card[i].Rating, Math.Max(0, (card[i].Utc - card[i - 1].Utc).TotalDays));
        return reviews;
    }

    private static List<UserData> LoadUsers(string dir)
    {
        var paramCounts = ReadCsv(Path.Combine(dir, "fsrs_users.csv"))
            .ToDictionary(f => int.Parse(f[0]), f => int.Parse(f[1]));

        var cards = new Dictionary<int, Dictionary<long, List<Review>>>();
        List<Review> CardList(int user, long card)
        {
            if (!cards.TryGetValue(user, out var byCard))
                cards[user] = byCard = new Dictionary<long, List<Review>>();
            if (!byCard.TryGetValue(card, out var list))
                byCard[card] = list = [];
            return list;
        }

        foreach (var f in ReadCsv(Path.Combine(dir, "fsrs_live.csv")))
            CardList(int.Parse(f[0]), long.Parse(f[1]))
                .Add(new Review(DateTime.UnixEpoch.AddMilliseconds(long.Parse(f[3])), int.Parse(f[2])));

        var archivePath = Path.Combine(dir, "fsrs_archive.csv");
        var skippedTruncated = 0;
        foreach (var f in ReadCsv(archivePath))
        {
            // A truncated history is missing its first reviews, so its initial state would be wrong.
            if (f[2] == "t")
            {
                skippedTruncated++;
                continue;
            }

            var firstReview = DateTime.UnixEpoch.AddMilliseconds(long.Parse(f[4]));
            var list = CardList(int.Parse(f[0]), -long.Parse(f[1]));
            foreach (var r in ReviewLogPacker.Unpack(Convert.FromHexString(f[6]), firstReview))
                list.Add(new Review(r.ReviewDateTime, (int)r.Rating));
        }

        if (skippedTruncated > 0)
            Console.WriteLine($"Skipped {skippedTruncated} truncated archive histories");

        return cards.OrderBy(kv => kv.Key)
                    .Select(kv => new UserData(
                        kv.Key,
                        paramCounts.GetValueOrDefault(kv.Key),
                        kv.Value.Values.Select(l => l.OrderBy(r => r.Utc).ToArray()).ToList()))
                    .ToList();
    }

    private static IEnumerable<string[]> ReadCsv(string path) =>
        File.ReadLines(path).Skip(1).Where(l => l.Length > 0).Select(l => l.Split(','));

    private record Metrics(int Count, double LogLoss, double Rmse, double Auc)
    {
        public static Metrics From(IEnumerable<Prediction> source)
        {
            var list = source.ToList();
            if (list.Count == 0)
                return new Metrics(0, double.NaN, double.NaN, double.NaN);

            var logLoss = list.Average(p => FsrsOptimizer.BinaryCrossEntropy(p.P, p.Y));

            var sq = list.GroupBy(p => p.Bin)
                         .Sum(g => g.Count() * Math.Pow(g.Average(p => p.P) - g.Average(p => p.Y), 2));
            var rmse = Math.Sqrt(sq / list.Count);

            return new Metrics(list.Count, logLoss, rmse, ComputeAuc(list));
        }

        public static Metrics Mean(IEnumerable<Metrics> source)
        {
            var list = source.Where(m => m.Count > 0).ToList();
            if (list.Count == 0)
                return new Metrics(0, double.NaN, double.NaN, double.NaN);
            return new Metrics(list.Count,
                               list.Average(m => m.LogLoss),
                               list.Average(m => m.Rmse),
                               list.Where(m => !double.IsNaN(m.Auc)).Select(m => m.Auc).DefaultIfEmpty(double.NaN).Average());
        }

        /// <summary>Mann-Whitney AUC with tied predictions sharing their average rank.</summary>
        private static double ComputeAuc(List<Prediction> list)
        {
            var positives = list.Count(p => p.Y > 0.5);
            var negatives = list.Count - positives;
            if (positives == 0 || negatives == 0)
                return double.NaN;

            var sorted = list.OrderBy(p => p.P).ToArray();
            var rankSumPositives = 0.0;
            var i = 0;
            while (i < sorted.Length)
            {
                var j = i;
                while (j + 1 < sorted.Length && sorted[j + 1].P == sorted[i].P) j++;
                var averageRank = (i + j) / 2.0 + 1;
                for (var k = i; k <= j; k++)
                    if (sorted[k].Y > 0.5)
                        rankSumPositives += averageRank;
                i = j + 1;
            }

            return (rankSumPositives - (double)positives * (positives + 1) / 2.0) / ((double)positives * negatives);
        }
    }
}
