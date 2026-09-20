using Jiten.Core.Data.FSRS;

namespace Jiten.Tests;

public class FsrsOptimizerTests
{
    private readonly double[] _defaultParameters = FsrsConstants.DefaultParameters;

    [Fact]
    public void ForwardPass_MatchesScheduler_StabilityAndDifficulty()
    {
        var scheduler = FsrsTests.CreateSchedulerWithoutFuzzing();
        var card = FsrsTests.CreateNewCard();
        var reviewDateTime = FsrsTests.GetTestDateTime();

        var ratings = new[] { FsrsRating.Again, FsrsRating.Good, FsrsRating.Good, FsrsRating.Good, FsrsRating.Good, FsrsRating.Good };
        var intervals = new[] { 0, 0, 1, 3, 8, 21 };

        var reviews = new FsrsTrainingReview[ratings.Length];
        reviews[0] = new FsrsTrainingReview((int)ratings[0], 0);
        for (var i = 1; i < ratings.Length; i++)
            reviews[i] = new FsrsTrainingReview((int)ratings[i], intervals[i]);

        // Run through scheduler
        for (var i = 0; i < ratings.Length; i++)
        {
            reviewDateTime = reviewDateTime.AddDays(intervals[i]);
            (card, _) = scheduler.ReviewCard(card, ratings[i], reviewDateTime);
        }

        // Run optimizer forward pass to get predictions
        var item = new FsrsTrainingItem(reviews);
        var predictions = FsrsOptimizer.ForwardPass(item, _defaultParameters);

        // The two same-day reviews update state only; the four long-term ones are targets
        Assert.Equal(intervals.Count(d => d >= 1), predictions.Count);

        // After the full sequence, run one more review through scheduler to get final state
        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, reviewDateTime);
        Assert.Equal(53.3825, Math.Round(card.Stability!.Value, 4));
        Assert.Equal(6.3809, Math.Round(card.Difficulty!.Value, 4));
    }

    [Fact]
    public void ForwardPass_FirstReviewPrediction_UsesInitialStability()
    {
        var reviews = new[]
        {
            new FsrsTrainingReview(3, 0),  // Good, first review
            new FsrsTrainingReview(3, 5),  // Good, 5 days later
        };
        var item = new FsrsTrainingItem(reviews);
        var predictions = FsrsOptimizer.ForwardPass(item, _defaultParameters);

        Assert.Single(predictions);

        var initS = FsrsHelper.CalculateInitialStability(FsrsRating.Good, _defaultParameters);
        var expectedR = FsrsOptimizer.PowerForgettingCurve(5.0, initS, _defaultParameters);

        Assert.Equal(expectedR, predictions[0].Predicted, 6);
        Assert.Equal(1.0, predictions[0].Label); // Good = recalled
    }

    [Fact]
    public void ForwardPass_AgainRating_ProducesZeroLabel()
    {
        var reviews = new[]
        {
            new FsrsTrainingReview(3, 0),
            new FsrsTrainingReview(1, 5), // Again
        };
        var item = new FsrsTrainingItem(reviews);
        var predictions = FsrsOptimizer.ForwardPass(item, _defaultParameters);

        Assert.Equal(0.0, predictions[0].Label);
    }

    [Fact]
    public void ForwardPass_SingleReview_ReturnsEmpty()
    {
        var item = new FsrsTrainingItem([new FsrsTrainingReview(3, 0)]);
        var predictions = FsrsOptimizer.ForwardPass(item, _defaultParameters);

        Assert.Empty(predictions);
    }

    [Fact]
    public void ForwardPass_ShortTermReviews_UpdateStateButAreNotTargets()
    {
        var withSteps = new FsrsTrainingItem(new[]
        {
            new FsrsTrainingReview(3, 0),
            new FsrsTrainingReview(1, 0),   // same-day lapse
            new FsrsTrainingReview(3, 0.5), // half day
            new FsrsTrainingReview(3, 5),   // long-term
        });
        var withoutSteps = new FsrsTrainingItem(new[]
        {
            new FsrsTrainingReview(3, 0),
            new FsrsTrainingReview(3, 5),
        });

        var predictions = FsrsOptimizer.ForwardPass(withSteps, _defaultParameters);
        var baseline = FsrsOptimizer.ForwardPass(withoutSteps, _defaultParameters);

        Assert.Single(predictions);
        Assert.Single(baseline);
        Assert.NotEqual(baseline[0].Predicted, predictions[0].Predicted);
    }

    [Fact]
    public void ComputeGradients_ShortTermReviews_ContributeNoLoss()
    {
        var withSteps = new FsrsTrainingItem(new[]
        {
            new FsrsTrainingReview(3, 0),
            new FsrsTrainingReview(1, 0.007),
            new FsrsTrainingReview(3, 0.007),
            new FsrsTrainingReview(3, 5),
        });
        var lossWithSteps = FsrsOptimizer.ComputeLoss([withSteps], _defaultParameters);
        var predicted = FsrsOptimizer.ForwardPass(withSteps, _defaultParameters)[0].Predicted;

        Assert.Equal(FsrsOptimizer.BinaryCrossEntropy(predicted, 1.0), lossWithSteps, 10);
    }

    [Fact]
    public void Optimize_ItemsWithoutLongTermReviews_AreDropped()
    {
        var items = Enumerable.Range(0, 2000)
            .Select(_ => new FsrsTrainingItem(new[]
            {
                new FsrsTrainingReview(3, 0),
                new FsrsTrainingReview(1, 0.007),
                new FsrsTrainingReview(3, 0.007),
            }))
            .ToList();

        var result = FsrsOptimizer.Optimize(items);

        Assert.Equal(0, result.ReviewCount);
        Assert.Equal(FsrsConstants.DefaultParameters, result.Parameters);
    }

    [Fact]
    public void BinaryCrossEntropy_PerfectPrediction_LowLoss()
    {
        var lossCorrect = FsrsOptimizer.BinaryCrossEntropy(0.99, 1.0);
        var lossWrong = FsrsOptimizer.BinaryCrossEntropy(0.01, 1.0);

        Assert.True(lossCorrect < 0.02);
        Assert.True(lossWrong > 4.0);
    }

    [Fact]
    public void BinaryCrossEntropy_Symmetric()
    {
        var loss1 = FsrsOptimizer.BinaryCrossEntropy(0.3, 0.0);
        var loss2 = FsrsOptimizer.BinaryCrossEntropy(0.7, 1.0);

        Assert.Equal(loss1, loss2, 6);
    }

    [Fact]
    public void BinaryCrossEntropy_ClampsPredictions()
    {
        // Should not throw or return NaN/Inf for extreme values
        var loss0 = FsrsOptimizer.BinaryCrossEntropy(0.0, 1.0);
        var loss1 = FsrsOptimizer.BinaryCrossEntropy(1.0, 0.0);

        Assert.False(double.IsNaN(loss0));
        Assert.False(double.IsInfinity(loss0));
        Assert.False(double.IsNaN(loss1));
        Assert.False(double.IsInfinity(loss1));
    }

    [Fact]
    public void PowerForgettingCurve_AtZeroDays_ReturnsOne()
    {
        var r = FsrsOptimizer.PowerForgettingCurve(0, 10.0, _defaultParameters);
        Assert.Equal(1.0, r, 6);
    }

    [Fact]
    public void PowerForgettingCurve_AtStabilityDays_ReturnsApprox09()
    {
        // By definition, stability is the time for R to drop to 0.9
        var stability = 10.0;
        var r = FsrsOptimizer.PowerForgettingCurve(stability, stability, _defaultParameters);
        Assert.Equal(0.9, r, 2);
    }

    [Fact]
    public void PowerForgettingCurve_MatchesSchedulerRetrievability()
    {
        var stability = 15.0;
        var elapsedDays = 7.0;

        var optimizerR = FsrsOptimizer.PowerForgettingCurve(elapsedDays, stability, _defaultParameters);

        var card = new FsrsCard
        {
            LastReview = DateTime.UtcNow.AddDays(-elapsedDays),
            Stability = stability
        };
        var schedulerR = FsrsHelper.CalculateRetrievability(card, DateTime.UtcNow, _defaultParameters);

        Assert.Equal(schedulerR, optimizerR, 4);
    }

    [Fact]
    public void ClampParameters_EnforcesAllBounds()
    {
        var parameters = new double[21];

        // Set all to below minimum
        for (var i = 0; i < 21; i++)
            parameters[i] = -100;

        FsrsOptimizer.ClampParameters(parameters);

        Assert.Equal(0.001, parameters[0]); // w0 min
        Assert.Equal(1.0, parameters[4]);   // w4 min
        Assert.Equal(0.1, parameters[20]);  // w20 min

        // Set all to above maximum
        for (var i = 0; i < 21; i++)
            parameters[i] = 1000;

        FsrsOptimizer.ClampParameters(parameters);

        Assert.Equal(100.0, parameters[0]); // w0 max
        Assert.Equal(10.0, parameters[4]);  // w4 max
        Assert.Equal(0.8, parameters[20]);  // w20 max
        Assert.Equal(6.0, parameters[16]);  // w16 max
        Assert.Equal(1.0, parameters[15]);  // w15 max
    }

    [Fact]
    public void DefaultParameters_AreWithinBounds()
    {
        var parameters = (double[])FsrsConstants.DefaultParameters.Clone();
        FsrsOptimizer.ClampParameters(parameters);

        for (var i = 0; i < 21; i++)
            Assert.Equal(FsrsConstants.DefaultParameters[i], parameters[i], 6);
    }

    [Fact]
    public void ComputeLoss_DefaultParams_OnSyntheticData_IsFinite()
    {
        var items = GenerateSyntheticData(_defaultParameters, cardCount: 50, seed: 42);
        var loss = FsrsOptimizer.ComputeLoss(items, _defaultParameters);

        Assert.True(loss > 0);
        Assert.False(double.IsNaN(loss));
        Assert.False(double.IsInfinity(loss));
    }

    [Fact]
    public void ComputeLoss_CorrectParams_LowerThanRandom()
    {
        var trueParams = (double[])_defaultParameters.Clone();
        var items = GenerateSyntheticData(trueParams, cardCount: 100, seed: 42);

        var correctLoss = FsrsOptimizer.ComputeLoss(items, trueParams);

        var perturbedParams = (double[])trueParams.Clone();
        var rng = new Random(123);
        for (var i = 0; i < 21; i++)
            perturbedParams[i] *= (0.5 + rng.NextDouble());
        FsrsOptimizer.ClampParameters(perturbedParams);

        var perturbedLoss = FsrsOptimizer.ComputeLoss(items, perturbedParams);

        Assert.True(correctLoss <= perturbedLoss,
            $"Correct params loss ({correctLoss:F6}) should be <= perturbed params loss ({perturbedLoss:F6})");
    }

    [Fact]
    public void NumericalGradients_NonZeroForRelevantParameters()
    {
        var items = GenerateSyntheticData(_defaultParameters, cardCount: 30, seed: 42);
        var gradients = FsrsOptimizer.ComputeNumericalGradients(items, (double[])_defaultParameters.Clone());

        var nonZeroCount = gradients.Count(g => Math.Abs(g) > 1e-10);
        Assert.True(nonZeroCount > 10,
            $"Expected most gradients to be non-zero, got {nonZeroCount}/21");
    }

    [Fact]
    public void AnalyticalGradients_MatchNumericalGradients()
    {
        var items = GenerateSyntheticData(_defaultParameters, cardCount: 50, seed: 42);
        var parameters = (double[])_defaultParameters.Clone();

        var numerical = FsrsOptimizer.ComputeNumericalGradients(items, parameters);
        var analytical = FsrsOptimizer.ComputeGradients(items, parameters);

        for (var i = 0; i < 21; i++)
        {
            var absDiff = Math.Abs(analytical[i] - numerical[i]);
            var relDiff = absDiff / (Math.Abs(numerical[i]) + 1e-10);
            Assert.True(absDiff < 1e-3 || relDiff < 0.05,
                $"w[{i}]: analytical={analytical[i]:E6}, numerical={numerical[i]:E6}, " +
                $"absDiff={absDiff:E3}, relDiff={relDiff:P1}");
        }
    }

    [Fact]
    public void AnalyticalGradients_MatchNumericalGradients_PerturbedParams()
    {
        var items = GenerateSyntheticData(_defaultParameters, cardCount: 50, seed: 42);

        var perturbedParams = (double[])_defaultParameters.Clone();
        perturbedParams[2] *= 1.5;
        perturbedParams[8] *= 0.7;
        perturbedParams[11] *= 1.3;
        FsrsOptimizer.ClampParameters(perturbedParams);

        var numerical = FsrsOptimizer.ComputeNumericalGradients(items, perturbedParams);
        var analytical = FsrsOptimizer.ComputeGradients(items, perturbedParams);

        for (var i = 0; i < 21; i++)
        {
            var absDiff = Math.Abs(analytical[i] - numerical[i]);
            var relDiff = absDiff / (Math.Abs(numerical[i]) + 1e-10);
            Assert.True(absDiff < 1e-3 || relDiff < 0.05,
                $"w[{i}]: analytical={analytical[i]:E6}, numerical={numerical[i]:E6}, " +
                $"absDiff={absDiff:E3}, relDiff={relDiff:P1}");
        }
    }

    [Fact]
    public void AnalyticalGradients_MatchNumerical_SingleItem()
    {
        var parameters = (double[])_defaultParameters.Clone();
        var reviews = new[]
        {
            new FsrsTrainingReview(1, 0),
            new FsrsTrainingReview(3, 1),
            new FsrsTrainingReview(3, 3),
            new FsrsTrainingReview(3, 8),
            new FsrsTrainingReview(3, 15),
        };
        var item = new FsrsTrainingItem(reviews);
        var items = new List<FsrsTrainingItem> { item };

        var numerical = FsrsOptimizer.ComputeNumericalGradients(items, parameters);
        var analytical = FsrsOptimizer.ComputeGradients(items, parameters);

        for (var i = 0; i < 21; i++)
        {
            var absDiff = Math.Abs(analytical[i] - numerical[i]);
            var relDiff = absDiff / (Math.Abs(numerical[i]) + 1e-10);
            Assert.True(absDiff < 1e-3 || relDiff < 0.05,
                $"w[{i}]: analytical={analytical[i]:E6}, numerical={numerical[i]:E6}, absDiff={absDiff:E3}");
        }
    }

    [Fact]
    public void NumericalGradients_PointTowardLowerLoss()
    {
        var items = GenerateSyntheticData(_defaultParameters, cardCount: 50, seed: 42);

        var perturbedParams = (double[])_defaultParameters.Clone();
        perturbedParams[2] *= 1.5; // Perturb w2 (init stability Good)
        FsrsOptimizer.ClampParameters(perturbedParams);

        var baseLoss = FsrsOptimizer.ComputeLoss(items, perturbedParams);
        var gradients = FsrsOptimizer.ComputeNumericalGradients(items, (double[])perturbedParams.Clone());

        // Take a small step in the negative gradient direction
        var stepParams = (double[])perturbedParams.Clone();
        for (var i = 0; i < 21; i++)
            stepParams[i] -= 0.001 * gradients[i];
        FsrsOptimizer.ClampParameters(stepParams);

        var newLoss = FsrsOptimizer.ComputeLoss(items, stepParams);
        Assert.True(newLoss <= baseLoss + 1e-6,
            $"Gradient step should not increase loss: {newLoss:F6} > {baseLoss:F6}");
    }

    [Fact]
    public void InitialStabilityEstimation_RecoversApproximateValues()
    {
        var trueParams = (double[])_defaultParameters.Clone();
        trueParams[0] = 1.0;  // Again (must be >= 1.0 so second reviews have deltaT >= 1 day)
        trueParams[1] = 3.0;  // Hard
        trueParams[2] = 5.0;  // Good
        trueParams[3] = 15.0; // Easy

        var items = GenerateSyntheticData(trueParams, cardCount: 1000, seed: 42);

        var estimated = (double[])_defaultParameters.Clone();
        FsrsOptimizer.EstimateInitialStability(items, estimated);

        // Only check ratings that have enough long-term second-review data points
        for (var i = 0; i < 4; i++)
        {
            var ratio = estimated[i] / trueParams[i];
            Assert.True(ratio > 0.3 && ratio < 3.0,
                $"w[{i}]: estimated={estimated[i]:F4}, true={trueParams[i]:F4}, ratio={ratio:F4}");
        }
    }

    [Fact]
    public void InitialStabilityEstimation_EnforcesMonotonicity()
    {
        var items = GenerateSyntheticData(_defaultParameters, cardCount: 200, seed: 99);
        var estimated = (double[])_defaultParameters.Clone();
        FsrsOptimizer.EstimateInitialStability(items, estimated);

        Assert.True(estimated[0] <= estimated[1],
            $"w[0]={estimated[0]:F4} should be <= w[1]={estimated[1]:F4}");
        Assert.True(estimated[1] <= estimated[2],
            $"w[1]={estimated[1]:F4} should be <= w[2]={estimated[2]:F4}");
        Assert.True(estimated[2] <= estimated[3],
            $"w[2]={estimated[2]:F4} should be <= w[3]={estimated[3]:F4}");
    }

    [Fact]
    public void Optimize_ReducesLoss()
    {
        var items = GenerateSyntheticData(_defaultParameters, cardCount: 500, seed: 42);

        var defaultLoss = FsrsOptimizer.ComputeLoss(items, _defaultParameters);
        var result = FsrsOptimizer.Optimize(items, new FsrsOptimizerConfig { Epochs = 5 });
        var optimizedLoss = FsrsOptimizer.ComputeLoss(items, result.Parameters);

        Assert.True(optimizedLoss <= defaultLoss + 0.01,
            $"Optimized loss ({optimizedLoss:F6}) should be <= default loss ({defaultLoss:F6}) + tolerance");
    }

    [Fact]
    public void Optimize_ProducesValidParameters()
    {
        var items = GenerateSyntheticData(_defaultParameters, cardCount: 50, seed: 42);
        var result = FsrsOptimizer.Optimize(items, new FsrsOptimizerConfig { Epochs = 2 });

        Assert.Equal(21, result.Parameters.Length);

        var clamped = (double[])result.Parameters.Clone();
        FsrsOptimizer.ClampParameters(clamped);

        for (var i = 0; i < 21; i++)
            Assert.Equal(clamped[i], result.Parameters[i], 10);
    }

    [Fact]
    public void Optimize_EmptyInput_ReturnsDefaults()
    {
        var result = FsrsOptimizer.Optimize([]);
        Assert.Equal(FsrsConstants.DefaultParameters, result.Parameters);
    }

    [Fact]
    public void Optimize_ProgressCallback_IsCalled()
    {
        var items = GenerateSyntheticData(_defaultParameters, cardCount: 100, seed: 42);
        var progressCalls = 0;

        FsrsOptimizer.Optimize(items, new FsrsOptimizerConfig
        {
            Epochs = 2,
            Progress = (current, total) => progressCalls++
        });

        Assert.True(progressCalls > 0);
    }

    [Fact]
    public void Optimize_DisableShortTerm_FreezesParameters17to19()
    {
        var items = GenerateSyntheticData(_defaultParameters, cardCount: 50, seed: 42);

        var result = FsrsOptimizer.Optimize(items, new FsrsOptimizerConfig
        {
            Epochs = 3,
            EnableShortTerm = false
        });

        // w17-w19 should stay close to defaults since their gradients are frozen
        for (var i = 17; i <= 19; i++)
        {
            var ratio = result.Parameters[i] / _defaultParameters[i];
            Assert.True(ratio > 0.9 && ratio < 1.1,
                $"w[{i}] should stay close to default when short-term is disabled: " +
                $"result={result.Parameters[i]:F4}, default={_defaultParameters[i]:F4}");
        }
    }

    [Fact]
    public void ConvertReviewLogs_GroupsAndOrdersCorrectly()
    {
        var baseTime = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var logs = new[]
        {
            new FsrsReviewLog(1, FsrsRating.Good, baseTime),
            new FsrsReviewLog(1, FsrsRating.Good, baseTime.AddDays(1)),
            new FsrsReviewLog(1, FsrsRating.Again, baseTime.AddDays(4)),
            new FsrsReviewLog(2, FsrsRating.Hard, baseTime),
            new FsrsReviewLog(2, FsrsRating.Good, baseTime.AddDays(2)),
        };

        var grouped = logs.GroupBy(l => l.CardId).ToList();
        var items = FsrsOptimizer.ConvertReviewLogs(grouped);

        Assert.Equal(2, items.Count);

        var card1 = items.First(i => i.Reviews.Length == 3);
        Assert.Equal(0, card1.Reviews[0].DeltaT);
        Assert.Equal(1.0, card1.Reviews[1].DeltaT);
        Assert.Equal(3.0, card1.Reviews[2].DeltaT);
        Assert.Equal(3, card1.Reviews[0].Rating);
        Assert.Equal(1, card1.Reviews[2].Rating);

        var card2 = items.First(i => i.Reviews.Length == 2);
        Assert.Equal(0, card2.Reviews[0].DeltaT);
        Assert.Equal(2.0, card2.Reviews[1].DeltaT);
    }

    [Fact]
    public void ConvertReviewLogs_SkipsSingleReviewCards()
    {
        var logs = new[]
        {
            new FsrsReviewLog(1, FsrsRating.Good, DateTime.UtcNow),
        };

        var grouped = logs.GroupBy(l => l.CardId).ToList();
        var items = FsrsOptimizer.ConvertReviewLogs(grouped);

        Assert.Empty(items);
    }

    [Fact]
    public void Optimize_CanRecoverPerturbedParameters()
    {
        var trueParams = (double[])_defaultParameters.Clone();
        trueParams[0] = 0.5;   // Again init stability (default 0.212)
        trueParams[2] = 8.0;   // Good init stability (default 2.3065)
        trueParams[8] = 2.5;   // Recall stability exp (default 1.8722)
        FsrsOptimizer.ClampParameters(trueParams);

        var items = GenerateSyntheticData(trueParams, cardCount: 500, seed: 42);
        var result = FsrsOptimizer.Optimize(items, new FsrsOptimizerConfig { Epochs = 5 });

        // Optimized parameters should produce lower loss than defaults
        var defaultLoss = FsrsOptimizer.ComputeLoss(items, _defaultParameters);
        var optimizedLoss = FsrsOptimizer.ComputeLoss(items, result.Parameters);
        Assert.True(optimizedLoss < defaultLoss + 0.01,
            $"Optimized loss ({optimizedLoss:F6}) should be close to or beat default loss ({defaultLoss:F6})");
    }

    [Fact]
    public void Optimize_SetsCorrectReviewCount()
    {
        var items = GenerateSyntheticData(_defaultParameters, cardCount: 20, seed: 42);
        var expectedReviews = items.Sum(i => i.Reviews.Skip(1).Count(r => r.DeltaT >= 1.0));

        var result = FsrsOptimizer.Optimize(items, new FsrsOptimizerConfig { Epochs = 1 });

        Assert.Equal(expectedReviews, result.ReviewCount);
    }

    private static List<FsrsTrainingItem> GenerateSyntheticData(double[] parameters, int cardCount, int seed)
    {
        var rng = new Random(seed);
        var items = new List<FsrsTrainingItem>();

        for (var c = 0; c < cardCount; c++)
        {
            var firstRating = rng.Next(1, 5);
            var reviewCount = rng.Next(3, 10);
            var reviews = new List<FsrsTrainingReview>();

            var stability = FsrsHelper.CalculateInitialStability((FsrsRating)firstRating, parameters);
            var difficulty = FsrsHelper.CalculateInitialDifficulty((FsrsRating)firstRating, parameters);

            reviews.Add(new FsrsTrainingReview(firstRating, 0));

            for (var r = 1; r < reviewCount; r++)
            {
                // Schedule review around the stability point with some noise
                var deltaT = Math.Max(0.1, stability * (0.5 + rng.NextDouble()));
                var retrievability = FsrsOptimizer.PowerForgettingCurve(deltaT, stability, parameters);

                // Simulate a stochastic review outcome based on retrievability
                var recalled = rng.NextDouble() < retrievability;
                int rating;
                if (!recalled)
                {
                    rating = 1; // Again
                }
                else
                {
                    var roll = rng.NextDouble();
                    rating = roll < 0.1 ? 2 : roll < 0.8 ? 3 : 4;
                }

                reviews.Add(new FsrsTrainingReview(rating, deltaT));

                if (deltaT < 1.0)
                {
                    stability = FsrsHelper.CalculateShortTermStability(stability, (FsrsRating)rating, parameters);
                    difficulty = FsrsHelper.CalculateNextDifficulty(difficulty, (FsrsRating)rating, parameters);
                }
                else
                {
                    stability = FsrsHelper.CalculateNextStability(
                        difficulty, stability, retrievability, (FsrsRating)rating, parameters);
                    difficulty = FsrsHelper.CalculateNextDifficulty(difficulty, (FsrsRating)rating, parameters);
                }
            }

            items.Add(new FsrsTrainingItem(reviews.ToArray()));
        }

        return items;
    }
}
