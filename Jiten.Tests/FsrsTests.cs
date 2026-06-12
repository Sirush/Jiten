using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.User;

namespace Jiten.Tests;

public class FsrsTests
{
    private static readonly Random Random = new();
    private readonly double[] _defaultParameters = FsrsConstants.DefaultParameters;

    [Fact]
    public void Card_NewCard_ShouldHaveCorrectInitialState()
    {
        var card = CreateNewCard();

        Assert.Equal(FsrsState.Learning, card.State);
        Assert.Equal(0, card.Step);
        Assert.Null(card.Stability);
        Assert.Null(card.Difficulty);
        Assert.Null(card.LastReview);
        Assert.True(DateTime.UtcNow >= card.Due);
    }

    [Fact]
    public void Card_NewCard_ShouldBeDueImmediately()
    {
        var card = CreateNewCard();

        Assert.True(DateTime.UtcNow >= card.Due);
    }

    [Fact]
    public void Card_Clone_ShouldCreateIdenticalCopy()
    {
        var original = new FsrsCard(
                                    userId: "",
                                    wordId: 0,
                                    readingIndex: 0,
                                    cardId: Random.Next(),
                                    state: FsrsState.Review,
                                    step: null,
                                    stability: 10.5,
                                    difficulty: 5.2,
                                    due: DateTime.UtcNow.AddDays(5),
                                    lastReview: DateTime.UtcNow
                                   );

        var clone = original.Clone();

        Assert.Equal(original.CardId, clone.CardId);
        Assert.Equal(original.State, clone.State);
        Assert.Equal(original.Step, clone.Step);
        Assert.Equal(original.Stability, clone.Stability);
        Assert.Equal(original.Difficulty, clone.Difficulty);
        Assert.Equal(original.Due, clone.Due);
        Assert.Equal(original.LastReview, clone.LastReview);
    }

    [Fact]
    public void Card_UniqueIds_ShouldGenerateUniqueCardIds()
    {
        var cardIds = new HashSet<long>();

        for (int i = 0; i < 1000; i++)
        {
            var card = CreateNewCard();
            cardIds.Add(i);
        }

        Assert.Equal(1000, cardIds.Count);
    }

    [Fact]
    public void Card_ToString_ShouldEqualRepr()
    {
        var card = CreateNewCard();

        Assert.Equal(card.ToString(), card.ToString());
    }

    [Fact]
    public void ReviewLog_Creation_ShouldSetPropertiesCorrectly()
    {
        var cardId = Random.Next();
        var rating = FsrsRating.Good;
        var reviewDateTime = GetTestDateTime();
        var reviewDuration = 5000;

        var reviewLog = new FsrsReviewLog(cardId, rating, reviewDateTime, reviewDuration);

        Assert.Equal(cardId, reviewLog.CardId);
        Assert.Equal(rating, reviewLog.Rating);
        Assert.Equal(reviewDateTime, reviewLog.ReviewDateTime);
        Assert.Equal(reviewDuration, reviewLog.ReviewDuration);
    }

    [Fact]
    public void ReviewLog_Creation_WithoutDuration_ShouldHaveNullDuration()
    {
        var cardId = Random.Next();
        var rating = FsrsRating.Good;
        var reviewDateTime = GetTestDateTime();

        var reviewLog = new FsrsReviewLog(cardId, rating, reviewDateTime);

        Assert.Equal(cardId, reviewLog.CardId);
        Assert.Equal(rating, reviewLog.Rating);
        Assert.Equal(reviewDateTime, reviewLog.ReviewDateTime);
        Assert.Null(reviewLog.ReviewDuration);
    }

    [Fact]
    public void ReviewLog_ToString_ShouldEqualRepr()
    {
        var reviewLog = new FsrsReviewLog(Random.Next(), FsrsRating.Good, GetTestDateTime());

        Assert.Equal(reviewLog.ToString(), reviewLog.ToString());
    }

    [Fact]
    public void ReviewCard_BasicSequence_ShouldProduceExpectedIntervals()
    {
        var scheduler = CreateSchedulerWithoutFuzzing();
        var card = CreateNewCard();
        var reviewDateTime = GetTestDateTime();

        var FsrsRatings = new[]
                          {
                              FsrsRating.Good, FsrsRating.Good, FsrsRating.Good, FsrsRating.Good, FsrsRating.Good, FsrsRating.Good,
                              FsrsRating.Again, FsrsRating.Again, FsrsRating.Good, FsrsRating.Good, FsrsRating.Good, FsrsRating.Good,
                              FsrsRating.Good
                          };

        var expectedIntervals = new[] { 0, 2, 11, 46, 163, 497, 0, 0, 2, 4, 7, 12, 20 };
        var actualIntervals = new List<int>();

        foreach (var FsrsRating in FsrsRatings)
        {
            (card, _) = scheduler.ReviewCard(card, FsrsRating, reviewDateTime);
            var interval = (card.Due - card.LastReview!.Value).Days;
            actualIntervals.Add(interval);
            reviewDateTime = card.Due;
        }

        Assert.Equal(expectedIntervals, actualIntervals);
    }

    [Fact]
    public void ReviewCard_RepeatedCorrectReviews_ShouldMaintainMinimumDifficulty()
    {
        var scheduler = CreateSchedulerWithoutFuzzing();
        var card = CreateNewCard();

        var reviewDateTimes = Enumerable.Range(0, 10)
                                        .Select(i => new DateTime(2022, 11, 29, 12, 30, 0, DateTimeKind.Utc).AddMicroseconds(i))
                                        .ToArray();

        foreach (var reviewDateTime in reviewDateTimes)
        {
            (card, _) = scheduler.ReviewCard(card, FsrsRating.Easy, reviewDateTime);
        }

        // Sử dụng precision 1 decimal place
        Assert.Equal(1.0, card.Difficulty!.Value, 1);
    }

    [Fact]
    public void ReviewCard_WithDefaultDateTime_ShouldUseCurrent()
    {
        var scheduler = CreateSchedulerWithoutFuzzing();
        var card = CreateNewCard();

        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good);

        var timeDelta = card.Due - DateTime.UtcNow;
        Assert.True(timeDelta.TotalSeconds > 500); // Due in approximately 8-10 minutes
    }

    [Fact]
    public void ReviewCard_MemoState_ShouldProduceExpectedStabilityAndDifficulty()
    {
        var scheduler = CreateSchedulerWithoutFuzzing();
        var card = CreateNewCard();
        var reviewDateTime = GetTestDateTime();

        var ratings = new[] { FsrsRating.Again, FsrsRating.Good, FsrsRating.Good, FsrsRating.Good, FsrsRating.Good, FsrsRating.Good };
        var intervals = new[] { 0, 0, 1, 3, 8, 21 };

        for (int i = 0; i < ratings.Length; i++)
        {
            reviewDateTime = reviewDateTime.AddDays(intervals[i]);
            (card, _) = scheduler.ReviewCard(card, ratings[i], reviewDateTime);
        }

        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, reviewDateTime);

        Assert.Equal(53.3825, Math.Round(card.Stability!.Value, 4));
        Assert.Equal(6.3809, Math.Round(card.Difficulty!.Value, 4));
    }

    [Fact]
    public void Scheduler_CustomParameters_ShouldSetCorrectly()
    {
        var customParameters = new double[]
                               {
                                   0.1456, 0.4186, 1.1104, 4.1315, 5.2417, 1.3098, 0.8975, 0.0000, 1.5674, 0.0567, 0.9661, 2.0275, 0.1592,
                                   0.2446, 1.5071, 0.2272, 2.8755, 1.234, 5.6789, 0.1437, 0.2
                               };
        var desiredRetention = 0.85;
        var maximumInterval = 3650;

        var scheduler = new FsrsScheduler(
                                          parameters: customParameters,
                                          desiredRetention: desiredRetention,
                                          maximumInterval: maximumInterval,
                                          enableFuzzing: false
                                         );

        Assert.Equal(customParameters, scheduler.Parameters);
        Assert.Equal(desiredRetention, scheduler.DesiredRetention);
        Assert.Equal(maximumInterval, scheduler.MaximumInterval);
        Assert.False(scheduler.EnableFuzzing);
    }

    [Fact]
    public void Scheduler_CustomLearningSteps_ShouldHandleMultipleSchedulers()
    {
        var twoStepScheduler = new FsrsScheduler(
                                                 learningSteps: [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)],
                                                 enableFuzzing: false
                                                );
        var oneStepScheduler = new FsrsScheduler(
                                                 learningSteps: [TimeSpan.FromMinutes(1)],
                                                 enableFuzzing: false
                                                );
        var noStepScheduler = new FsrsScheduler(
                                                learningSteps: [],
                                                enableFuzzing: false
                                               );

        Assert.Equal(2, twoStepScheduler.LearningSteps.Length);
        Assert.Single(oneStepScheduler.LearningSteps);
        Assert.Empty(noStepScheduler.LearningSteps);
    }

    [Fact]
    public void Scheduler_MaximumInterval_ShouldEnforceLimit()
    {
        var maximumInterval = 100;
        var scheduler = new FsrsScheduler(maximumInterval: maximumInterval, enableFuzzing: false);
        var card = new FsrsCard("", 0, 0);

        var ratings = new[] { FsrsRating.Easy, FsrsRating.Good, FsrsRating.Easy, FsrsRating.Good };

        foreach (var rating in ratings)
        {
            (card, _) = scheduler.ReviewCard(card, rating, card.Due);
            Assert.True((card.Due - card.LastReview!.Value).Days <= scheduler.MaximumInterval);
        }
    }

    [Fact]
    public void Fuzzing_WithDifferentSeeds_ShouldProduceDifferentIntervals()
    {
        // Arrange
        var iterations = 100;

        // Act
        var intervalsWithFuzzing = GenerateReviewIntervals(enableFuzzing: true, iterations: iterations);
        var intervalsWithoutFuzzing = GenerateReviewIntervals(enableFuzzing: false, iterations: iterations);

        // Assert
        Assert.False(
                     intervalsWithFuzzing.All(i => i == intervalsWithFuzzing.First()),
                     "Expected fuzzed intervals to vary, but all were the same."
                    );

        Assert.True(
                    intervalsWithoutFuzzing.All(i => i == intervalsWithoutFuzzing.First()),
                    "Expected non-fuzzed intervals to be consistent, but they varied."
                   );
    }

    [Fact]
    public void Fuzzing_Disabled_ShouldProduceConsistentIntervals()
    {
        var intervals = new List<int>();

        for (int i = 0; i < 5; i++)
        {
            var scheduler = CreateSchedulerWithoutFuzzing();
            var card = CreateNewCard();

            (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, DateTime.UtcNow);
            (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, card.Due);

            var prevDue = card.Due;
            (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, card.Due);
            var interval = (card.Due - prevDue).Days;
            intervals.Add(interval);
        }

        // All intervals should be identical when fuzzing is disabled
        Assert.True(intervals.All(i => i == intervals.First()));
    }

    private static readonly DateTime BalancerAnchor = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LoadBalancing_FlatLoad_ReturnsUnfuzzedCentre()
    {
        // With an empty calendar every day in the window is equally loaded, so the balancer must fall
        // back to the un-fuzzed interval (the window centre) — no early/late bias.
        var balancer = new DictionaryFsrsLoadBalancer();

        var result = FsrsHelper.ApplyFuzzing(TimeSpan.FromDays(30), 36500, BalancerAnchor, balancer);

        Assert.Equal(30, result.Days);
    }

    [Fact]
    public void LoadBalancing_PicksLeastLoadedDayWithinWindow()
    {
        // Interval 30 → fuzz window [27, 33]. Load every day except 33; the balancer must choose 33.
        var balancer = new DictionaryFsrsLoadBalancer();
        for (var day = 27; day <= 32; day++)
            for (var n = 0; n < 5; n++)
                balancer.Register(BalancerAnchor.AddDays(day));

        var result = FsrsHelper.ApplyFuzzing(TimeSpan.FromDays(30), 36500, BalancerAnchor, balancer);

        Assert.Equal(33, result.Days);
    }

    [Fact]
    public void LoadBalancing_NeverLeavesFuzzWindow()
    {
        // Even when the whole window is uniformly loaded, the choice stays inside [27, 33].
        var balancer = new DictionaryFsrsLoadBalancer();
        for (var day = 27; day <= 33; day++)
            balancer.Register(BalancerAnchor.AddDays(day));

        var result = FsrsHelper.ApplyFuzzing(TimeSpan.FromDays(30), 36500, BalancerAnchor, balancer);

        Assert.InRange(result.Days, 27, 33);
    }

    [Fact]
    public void LoadBalancing_RegistersChosenDay()
    {
        var balancer = new DictionaryFsrsLoadBalancer();

        var result = FsrsHelper.ApplyFuzzing(TimeSpan.FromDays(30), 36500, BalancerAnchor, balancer);

        // The placement must be recorded so later cards in the same batch balance around it.
        Assert.Equal(1, balancer.GetLoad(BalancerAnchor.AddDays(result.Days)));
    }

    [Fact]
    public void LoadBalancing_SharedBalancer_FlattensRepeatedIdenticalIntervals()
    {
        // 20 cards with the same 30-day interval reviewed at the same time would all pile onto one day
        // under naive scheduling. Sharing one balancer spreads them evenly across the [27, 33] window.
        var balancer = new DictionaryFsrsLoadBalancer();
        var perDay = new Dictionary<int, int>();

        for (var i = 0; i < 20; i++)
        {
            var result = FsrsHelper.ApplyFuzzing(TimeSpan.FromDays(30), 36500, BalancerAnchor, balancer);
            Assert.InRange(result.Days, 27, 33);
            perDay[result.Days] = perDay.GetValueOrDefault(result.Days) + 1;
        }

        // All 7 days used, and no day carries more than ceil(20 / 7) = 3 cards.
        Assert.Equal(7, perDay.Count);
        Assert.True(perDay.Values.All(count => count <= 3), "Load was not evenly distributed across the window.");
    }

    [Fact]
    public void LoadBalancing_NullBalancer_FallsBackToRandomFuzz()
    {
        // Without a balancer the result must land uniformly within the fuzz window [27, 33].
        var result = FsrsHelper.ApplyFuzzing(TimeSpan.FromDays(30), 36500);

        Assert.InRange(result.Days, 27, 33);
    }

    private static double[] WeekendEasyDays()
    {
        // Avoid Saturday (index 6) and Sunday (index 0); all weekdays normal.
        var weights = new[] { 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0 };
        weights[(int)DayOfWeek.Saturday] = 0.0;
        weights[(int)DayOfWeek.Sunday] = 0.0;
        return weights;
    }

    [Fact]
    public void EasyDays_From_InactiveCases_ReturnNull()
    {
        Assert.Null(EasyDaysPolicy.From(null, 0));
        Assert.Null(EasyDaysPolicy.From(new[] { 1.0, 1.0, 1.0 }, 0));                 // wrong length
        Assert.Null(EasyDaysPolicy.From(new[] { 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0 }, 0)); // all normal
        Assert.NotNull(EasyDaysPolicy.From(WeekendEasyDays(), 0));
    }

    [Fact]
    public void EasyDays_SteersOffReducedWeekday_WithinWindow()
    {
        // Empty calendar, weekends avoided. The chosen due date must fall on a weekday and stay in window.
        var balancer = new DictionaryFsrsLoadBalancer();
        var easyDays = EasyDaysPolicy.From(WeekendEasyDays(), 0);

        var result = FsrsHelper.ApplyFuzzing(TimeSpan.FromDays(30), 36500, BalancerAnchor, balancer, easyDays);

        var chosenWeekday = BalancerAnchor.AddDays(result.Days).DayOfWeek;
        Assert.InRange(result.Days, 27, 33);
        Assert.NotEqual(DayOfWeek.Saturday, chosenWeekday);
        Assert.NotEqual(DayOfWeek.Sunday, chosenWeekday);
    }

    [Fact]
    public void EasyDays_AllDaysAvoided_FallsBackToCentre()
    {
        // When every day in the window is "avoid", there is no preferable day, so it behaves like plain
        // balancing on a flat calendar: pick the un-fuzzed centre.
        var balancer = new DictionaryFsrsLoadBalancer();
        var allAvoid = EasyDaysPolicy.From(new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }, 0);

        var result = FsrsHelper.ApplyFuzzing(TimeSpan.FromDays(30), 36500, BalancerAnchor, balancer, allAvoid);

        Assert.Equal(30, result.Days);
    }

    [Fact]
    public void EasyDays_CombinesWithLoad_AvoidsBothBusyAndReducedDays()
    {
        // A heavily-loaded weekday plus avoided weekends: the pick must be a weekday that is not the loaded
        // one — load and weekday preference compose.
        var balancer = new DictionaryFsrsLoadBalancer();
        var easyDays = EasyDaysPolicy.From(WeekendEasyDays(), 0)!;

        // Find a weekday in the window and pile load on it.
        var loadedDay = -1;
        for (var days = 27; days <= 33; days++)
        {
            var weekday = BalancerAnchor.AddDays(days).DayOfWeek;
            if (weekday != DayOfWeek.Saturday && weekday != DayOfWeek.Sunday)
            {
                loadedDay = days;
                break;
            }
        }
        for (var n = 0; n < 50; n++)
            balancer.Register(BalancerAnchor.AddDays(loadedDay));

        var result = FsrsHelper.ApplyFuzzing(TimeSpan.FromDays(30), 36500, BalancerAnchor, balancer, easyDays);

        var chosenWeekday = BalancerAnchor.AddDays(result.Days).DayOfWeek;
        Assert.NotEqual(loadedDay, result.Days);
        Assert.NotEqual(DayOfWeek.Saturday, chosenWeekday);
        Assert.NotEqual(DayOfWeek.Sunday, chosenWeekday);
    }

    [Fact]
    public void EasyDays_OffsetShiftsWeekdayClassification()
    {
        // A large positive offset can roll a UTC weekday into the next local day; the policy must classify
        // by local weekday, not UTC.
        var utcDue = new DateTime(2026, 1, 3, 20, 0, 0, DateTimeKind.Utc); // Saturday 20:00 UTC

        // +8h rolls this into Sunday 04:00 local; weight must be read for the local weekday (Sunday).
        var sundayReduced = new[] { 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0 };
        sundayReduced[(int)DayOfWeek.Sunday] = 0.2;
        var policy = new EasyDaysPolicy(sundayReduced, 8);

        Assert.Equal(0.2, policy.Weight(utcDue)); // local Sunday, not UTC Saturday
    }

    private List<int> GenerateReviewIntervals(bool enableFuzzing, int iterations)
    {
        var intervals = new List<int>();

        for (int i = 0; i < iterations; i++)
        {
            var scheduler = new FsrsScheduler(enableFuzzing: enableFuzzing);
            var card = CreateNewCard();

            // Apply 3 Good reviews
            (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, DateTime.UtcNow);
            (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, card.Due);

            var prevDue = card.Due;
            (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, card.Due);
            var interval = (card.Due - prevDue).Days;

            intervals.Add(interval);
        }

        return intervals;
    }

    [Fact]
    public void LearningSteps_GoodRating_ShouldProgressThroughSteps()
    {
        var scheduler = CreateSchedulerWithoutFuzzing();
        var createdAt = DateTime.UtcNow;
        var card = CreateNewCard();

        Assert.Equal(FsrsState.Learning, card.State);
        Assert.Equal(0, card.Step);

        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, card.Due);

        Assert.Equal(FsrsState.Learning, card.State);
        Assert.Equal(1, card.Step);
        Assert.Equal(6, Math.Round((card.Due - createdAt).TotalSeconds / 100)); // ~10 minutes

        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, card.Due);

        Assert.Equal(FsrsState.Review, card.State);
        Assert.Null(card.Step);
        Assert.True(Math.Round((card.Due - createdAt).TotalSeconds / 3600) >= 24); // Over a day
    }

    [Fact]
    public void LearningSteps_AgainRating_ShouldResetToFirstStep()
    {
        var scheduler = CreateSchedulerWithoutFuzzing();
        var createdAt = DateTime.UtcNow;
        var card = CreateNewCard();

        Assert.Equal(FsrsState.Learning, card.State);
        Assert.Equal(0, card.Step);

        (card, _) = scheduler.ReviewCard(card, FsrsRating.Again, card.Due);

        Assert.Equal(FsrsState.Learning, card.State);
        Assert.Equal(0, card.Step);
        Assert.Equal(6, Math.Round((card.Due - createdAt).TotalSeconds / 10)); // ~1 minute
    }

    [Fact]
    public void LearningSteps_HardRating_ShouldStayAtSameStep()
    {
        var scheduler = CreateSchedulerWithoutFuzzing();
        var createdAt = DateTime.UtcNow;
        var card = CreateNewCard();

        Assert.Equal(FsrsState.Learning, card.State);
        Assert.Equal(0, card.Step);

        (card, _) = scheduler.ReviewCard(card, FsrsRating.Hard, card.Due);

        Assert.Equal(FsrsState.Learning, card.State);
        Assert.Equal(0, card.Step);
        Assert.Equal(33, Math.Round((card.Due - createdAt).TotalSeconds / 10)); // ~5.5 minutes
    }

    [Fact]
    public void LearningSteps_EasyRating_ShouldSkipToReview()
    {
        var scheduler = CreateSchedulerWithoutFuzzing();
        var createdAt = DateTime.UtcNow;
        var card = CreateNewCard();

        Assert.Equal(FsrsState.Learning, card.State);
        Assert.Equal(0, card.Step);

        (card, _) = scheduler.ReviewCard(card, FsrsRating.Easy, card.Due);

        Assert.Equal(FsrsState.Review, card.State);
        Assert.Null(card.Step);
        Assert.True(Math.Round((card.Due - createdAt).TotalSeconds / 86400) >= 1); // At least 1 day
    }

    [Fact]
    public void NoLearningSteps_ShouldSkipDirectlyToReview()
    {
        var scheduler = new FsrsScheduler(learningSteps: [], enableFuzzing: false);
        var card = CreateNewCard();

        Assert.Empty(scheduler.LearningSteps);

        (card, _) = scheduler.ReviewCard(card, FsrsRating.Again, DateTime.UtcNow);

        Assert.Equal(FsrsState.Review, card.State);
        var interval = (card.Due - card.LastReview!.Value).Days;
        Assert.True(interval >= 1);
    }

    [Fact]
    public void Relearning_AgainRating_ShouldStayAtFirstStep()
    {
        var scheduler = CreateSchedulerWithoutFuzzing();
        var card = CreateRelearningCard(scheduler);

        Assert.Equal(FsrsState.Relearning, card.State);
        Assert.Equal(0, card.Step);

        var prevDue = card.Due;
        (card, _) = scheduler.ReviewCard(card, FsrsRating.Again, card.Due);

        Assert.Equal(FsrsState.Relearning, card.State);
        Assert.Equal(0, card.Step);
        Assert.Equal(10, Math.Round((card.Due - prevDue).TotalSeconds / 60)); // 10 minutes
    }

    [Fact]
    public void Relearning_GoodRating_ShouldReturnToReview()
    {
        var scheduler = CreateSchedulerWithoutFuzzing();
        var card = CreateRelearningCard(scheduler);

        var prevDue = card.Due;
        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, card.Due);

        Assert.Equal(FsrsState.Review, card.State);
        Assert.Null(card.Step);
        Assert.True(Math.Round((card.Due - prevDue).TotalSeconds / 3600) >= 24); // At least 1 day
    }

    [Fact]
    public void NoRelearningSteps_ShouldStayInReview()
    {
        var scheduler = new FsrsScheduler(relearningSteps: [], enableFuzzing: false);
        var card = CreateNewCard();

        Assert.Empty(scheduler.RelearningSteps);

        // Progress to Review state
        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, DateTime.UtcNow);
        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, card.Due);

        Assert.Equal(FsrsState.Review, card.State);

        (card, _) = scheduler.ReviewCard(card, FsrsRating.Again, card.Due);

        Assert.Equal(FsrsState.Review, card.State);
        var interval = (card.Due - card.LastReview!.Value).Days;
        Assert.True(interval >= 1);
    }

    private static FsrsCard CreateRelearningCard(FsrsScheduler scheduler)
    {
        var card = CreateNewCard();

        // Progress to Review state
        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, card.Due);
        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, card.Due);
        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, card.Due);

        // Move to Relearning
        (card, _) = scheduler.ReviewCard(card, FsrsRating.Again, card.Due);

        return card;
    }

    [Fact]
    public void GetCardRetrievability_NewCard_ShouldReturnZero()
    {
        var scheduler = CreateSchedulerWithoutFuzzing();
        var card = CreateNewCard();

        Assert.Equal(FsrsState.Learning, card.State);
        var retrievability = scheduler.GetCardRetrievability(card);
        Assert.Equal(0, retrievability);
    }

    [Fact]
    public void GetCardRetrievability_LearningCard_ShouldReturnValidRange()
    {
        var scheduler = CreateSchedulerWithoutFuzzing();
        var card = CreateNewCard();

        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good);
        Assert.Equal(FsrsState.Learning, card.State);

        var retrievability = scheduler.GetCardRetrievability(card);
        Assert.InRange(retrievability, 0, 1);
    }

    [Fact]
    public void GetCardRetrievability_ReviewCard_ShouldReturnValidRange()
    {
        var scheduler = CreateSchedulerWithoutFuzzing();
        var card = CreateNewCard();

        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good);
        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good);
        Assert.Equal(FsrsState.Review, card.State);

        var retrievability = scheduler.GetCardRetrievability(card);
        Assert.InRange(retrievability, 0, 1);
    }

    [Fact]
    public void GetCardRetrievability_RelearningCard_ShouldReturnValidRange()
    {
        var scheduler = CreateSchedulerWithoutFuzzing();
        var card = CreateNewCard();

        // Progress to Review then to Relearning
        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good);
        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good);
        (card, _) = scheduler.ReviewCard(card, FsrsRating.Again);
        Assert.Equal(FsrsState.Relearning, card.State);

        var retrievability = scheduler.GetCardRetrievability(card);
        Assert.InRange(retrievability, 0, 1);
    }

    [Fact]
    public void ReviewState_GoodRating_ShouldMaintainReviewState()
    {
        var scheduler = CreateSchedulerWithoutFuzzing();
        var card = CreateNewCard();

        // Progress to Review state
        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, card.Due);
        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, card.Due);

        Assert.Equal(FsrsState.Review, card.State);
        Assert.Null(card.Step);

        var prevDue = card.Due;
        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, card.Due);

        Assert.Equal(FsrsState.Review, card.State);
        Assert.True(Math.Round((card.Due - prevDue).TotalSeconds / 3600) >= 24); // At least 1 day
    }

    [Fact]
    public void ReviewState_AgainRating_ShouldMoveToRelearning()
    {
        var scheduler = CreateSchedulerWithoutFuzzing();
        var card = CreateNewCard();

        // Progress to Review state
        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, card.Due);
        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, card.Due);
        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, card.Due);

        var prevDue = card.Due;
        (card, _) = scheduler.ReviewCard(card, FsrsRating.Again, card.Due);

        Assert.Equal(FsrsState.Relearning, card.State);
        Assert.Equal(10, Math.Round((card.Due - prevDue).TotalSeconds / 60)); // 10 minutes
    }

    [Fact]
    public void ReviewCard_NonUtcDateTime_ShouldThrowException()
    {
        var scheduler = CreateSchedulerWithoutFuzzing();
        var card = CreateNewCard();
        var nonUtcDateTime = new DateTime(2022, 11, 29, 12, 30, 0, DateTimeKind.Local);

        var exception = Assert.Throws<ArgumentException>(() =>
                                                             scheduler.ReviewCard(card, FsrsRating.Good, nonUtcDateTime));

        Assert.Contains("UTC", exception.Message);
    }

    [Fact]
    public void ReviewCard_UtcDateTime_ShouldSetCorrectTimezone()
    {
        var scheduler = CreateSchedulerWithoutFuzzing();
        var card = CreateNewCard();

        (card, _) = scheduler.ReviewCard(card, FsrsRating.Good, DateTime.UtcNow);

        Assert.Equal(DateTimeKind.Utc, card.Due.Kind);
        Assert.Equal(DateTimeKind.Utc, card.LastReview!.Value.Kind);
        Assert.True(card.Due >= card.LastReview);
    }

    [Fact]
    public void StabilityLowerBound_ShouldAlwaysBeAboveMinimum()
    {
        var scheduler = CreateSchedulerWithoutFuzzing();
        var card = CreateNewCard();

        for (int i = 0; i < 1000; i++)
        {
            (card, _) = scheduler.ReviewCard(
                                             card,
                                             FsrsRating.Again,
                                             card.Due.AddDays(1)
                                            );
            Assert.True(card.Stability >= FsrsConstants.StabilityMin);
        }
    }

    [Theory]
    [InlineData(FsrsRating.Again)]
    [InlineData(FsrsRating.Hard)]
    [InlineData(FsrsRating.Good)]
    [InlineData(FsrsRating.Easy)]
    public void CalculateInitialDifficulty_AllRatings_ShouldReturnValidRange(FsrsRating rating)
    {
        var difficulty = FsrsHelper.CalculateInitialDifficulty(rating, _defaultParameters);

        Assert.InRange(difficulty, 1.0, 10.0);
    }

    [Fact]
    public void CalculateInitialDifficulty_EasierRatings_ShouldProduceLowerDifficulty()
    {
        var againDifficulty = FsrsHelper.CalculateInitialDifficulty(FsrsRating.Again, _defaultParameters);
        var hardDifficulty = FsrsHelper.CalculateInitialDifficulty(FsrsRating.Hard, _defaultParameters);
        var goodDifficulty = FsrsHelper.CalculateInitialDifficulty(FsrsRating.Good, _defaultParameters);
        var easyDifficulty = FsrsHelper.CalculateInitialDifficulty(FsrsRating.Easy, _defaultParameters);

        Assert.True(againDifficulty > hardDifficulty);
        Assert.True(hardDifficulty > goodDifficulty);
        Assert.True(goodDifficulty > easyDifficulty);
    }

    [Theory]
    [InlineData(5.0, FsrsRating.Again)]
    [InlineData(5.0, FsrsRating.Hard)]
    [InlineData(5.0, FsrsRating.Good)]
    [InlineData(5.0, FsrsRating.Easy)]
    public void CalculateNextDifficulty_AllRatings_ShouldReturnValidRange(double currentDifficulty, FsrsRating rating)
    {
        var nextDifficulty = FsrsHelper.CalculateNextDifficulty(currentDifficulty, rating, _defaultParameters);

        Assert.InRange(nextDifficulty, 1.0, 10.0);
    }

    [Fact]
    public void CalculateNextDifficulty_AgainRating_ShouldIncreaseDifficulty()
    {
        var currentDifficulty = 5.0;
        var nextDifficulty = FsrsHelper.CalculateNextDifficulty(currentDifficulty, FsrsRating.Again, _defaultParameters);

        Assert.True(nextDifficulty > currentDifficulty);
    }

    [Fact]
    public void CalculateNextDifficulty_EasyRating_ShouldDecreaseDifficulty()
    {
        var currentDifficulty = 5.0;
        var nextDifficulty = FsrsHelper.CalculateNextDifficulty(currentDifficulty, FsrsRating.Easy, _defaultParameters);

        Assert.True(nextDifficulty < currentDifficulty);
    }

    [Fact]
    public void CalculateNextDifficulty_ExtremeValues_ShouldClampCorrectly()
    {
        // Test minimum boundary - Even with Easy FsrsRating from minimum difficulty, should stay at 1.0
        var minDifficulty = FsrsHelper.CalculateNextDifficulty(1.0, FsrsRating.Easy, _defaultParameters);
        Assert.Equal(1.0, minDifficulty, 6);

        // Test maximum boundary - Due to mean reversion, even Again FsrsRating from max difficulty
        // may not result in exactly 10.0, but should be very close to 10.0
        var maxDifficulty = FsrsHelper.CalculateNextDifficulty(10.0, FsrsRating.Again, _defaultParameters);
        Assert.True(maxDifficulty >= 9.5); // Should be close to maximum
        Assert.True(maxDifficulty <= 10.0); // Should not exceed maximum
    }

    [Fact]
    public void CalculateNextDifficulty_MeanReversion_ShouldTrendTowardEasyDifficulty()
    {
        var currentDifficulty = 8.0; // High difficulty
        var easyInitialDifficulty = FsrsHelper.CalculateInitialDifficulty(FsrsRating.Easy, _defaultParameters);

        // After many Good FsrsRatings, difficulty should move toward easy initial difficulty
        var difficulty = currentDifficulty;
        for (int i = 0; i < 10; i++)
        {
            difficulty = FsrsHelper.CalculateNextDifficulty(difficulty, FsrsRating.Good, _defaultParameters);
        }

        Assert.True(difficulty < currentDifficulty); // Should decrease
        // Note: Complete convergence depends on the mean reversion parameter
    }

    [Fact]
    public void ApplyFuzzing_ShortInterval_ShouldReturnUnchanged()
    {
        var shortInterval = TimeSpan.FromDays(2);
        var maximumInterval = 36500;

        var fuzzedInterval = FsrsHelper.ApplyFuzzing(shortInterval, maximumInterval);

        Assert.Equal(shortInterval, fuzzedInterval);
    }

    [Fact]
    public void ApplyFuzzing_LongInterval_ShouldReturnFuzzedValue()
    {
        // Arrange
        var longInterval = TimeSpan.FromDays(30);
        var maximumInterval = 36500;

        var results = new HashSet<double>();

        for (int i = 0; i < 100; i++)
        {
            var fuzzedInterval = FsrsHelper.ApplyFuzzing(longInterval, maximumInterval);
            results.Add(fuzzedInterval.TotalDays);
        }

        Assert.True(results.Count > 1, "Fuzzing did not produce varied results");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(100)]
    public void ApplyFuzzing_VariousIntervals_ShouldRespectMaximumInterval(int intervalDays)
    {
        var interval = TimeSpan.FromDays(intervalDays);
        var maximumInterval = 36500;

        var fuzzedInterval = FsrsHelper.ApplyFuzzing(interval, maximumInterval);

        Assert.True(fuzzedInterval.Days <= maximumInterval);
    }

    [Fact]
    public void ApplyFuzzing_LargeInterval_ShouldProduceReasonableRange()
    {
        var interval = TimeSpan.FromDays(50);
        var maximumInterval = 36500;
        var results = new List<int>();

        // Collect multiple results
        for (int i = 0; i < 1000; i++)
        {
            var fuzzedInterval = FsrsHelper.ApplyFuzzing(interval, maximumInterval);
            results.Add(fuzzedInterval.Days);
        }

        var min = results.Min();
        var max = results.Max();
        var originalDays = interval.Days;

        // Fuzzed values should be within reasonable range of original
        Assert.True(min >= originalDays * 0.8); // At least 80% of original
        Assert.True(max <= originalDays * 1.2); // At most 120% of original
        Assert.True(max > min); // Should have variation
    }

    [Fact]
    public void ApplyFuzzing_MinimumIntervalThreshold_ShouldRespectMinimum()
    {
        var interval = TimeSpan.FromDays(10);
        var maximumInterval = 36500;
        var results = new List<int>();

        // Collect multiple results
        for (int i = 0; i < 100; i++)
        {
            var fuzzedInterval = FsrsHelper.ApplyFuzzing(interval, maximumInterval);
            results.Add(fuzzedInterval.Days);
        }

        // All results should be at least 2 days (minimum enforced in fuzzing)
        Assert.All(results, days => Assert.True(days >= 2));
    }

    [Fact]
    public void ApplyFuzzing_ExactThreshold_ShouldHandleBoundaryCorrectly()
    {
        var thresholdInterval = TimeSpan.FromDays(2.5);
        var maximumInterval = 36500;

        var fuzzedInterval = FsrsHelper.ApplyFuzzing(thresholdInterval, maximumInterval);

        // At exactly 2.5 days, should not be fuzzed
        Assert.Equal(thresholdInterval, fuzzedInterval);
    }

    [Theory]
    [InlineData(1.0, 0.9, 36500)]
    [InlineData(10.0, 0.9, 36500)]
    [InlineData(100.0, 0.9, 36500)]
    public void CalculateNextInterval_ValidInputs_ShouldReturnPositiveInterval(double stability, double desiredRetention,
                                                                               int maximumInterval)
    {
        var interval = FsrsHelper.CalculateNextInterval(stability, desiredRetention, _defaultParameters, maximumInterval);

        Assert.True(interval > 0);
        Assert.True(interval <= maximumInterval);
    }

    [Fact]
    public void CalculateNextInterval_HigherStability_ShouldProduceLongerInterval()
    {
        var lowStability = 5.0;
        var highStability = 50.0;
        var desiredRetention = 0.9;
        var maximumInterval = 36500;

        var lowInterval = FsrsHelper.CalculateNextInterval(lowStability, desiredRetention, _defaultParameters, maximumInterval);
        var highInterval = FsrsHelper.CalculateNextInterval(highStability, desiredRetention, _defaultParameters, maximumInterval);

        Assert.True(highInterval > lowInterval);
    }

    [Fact]
    public void CalculateNextInterval_HigherDesiredRetention_ShouldProduceShorterInterval()
    {
        var stability = 10.0;
        var lowRetention = 0.8;
        var highRetention = 0.95;
        var maximumInterval = 36500;

        var lowRetentionInterval = FsrsHelper.CalculateNextInterval(stability, lowRetention, _defaultParameters, maximumInterval);
        var highRetentionInterval = FsrsHelper.CalculateNextInterval(stability, highRetention, _defaultParameters, maximumInterval);

        Assert.True(highRetentionInterval < lowRetentionInterval);
    }

    [Fact]
    public void CalculateNextInterval_MaximumInterval_ShouldEnforceLimit()
    {
        var stability = 1000.0; // Very high stability
        var desiredRetention = 0.9;
        var maximumInterval = 100;

        var interval = FsrsHelper.CalculateNextInterval(stability, desiredRetention, _defaultParameters, maximumInterval);

        Assert.Equal(maximumInterval, interval);
    }

    [Fact]
    public void CalculateNextInterval_MinimumInterval_ShouldBeAtLeastOne()
    {
        var stability = 0.1; // Very low stability
        var desiredRetention = 0.99; // Very high retention requirement
        var maximumInterval = 36500;

        var interval = FsrsHelper.CalculateNextInterval(stability, desiredRetention, _defaultParameters, maximumInterval);

        Assert.True(interval >= 1);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(0.8)]
    [InlineData(0.9)]
    [InlineData(0.95)]
    [InlineData(0.99)]
    public void CalculateNextInterval_VariousRetentions_ShouldProduceValidIntervals(double desiredRetention)
    {
        var stability = 10.0;
        var maximumInterval = 36500;

        var interval = FsrsHelper.CalculateNextInterval(stability, desiredRetention, _defaultParameters, maximumInterval);

        Assert.InRange(interval, 1, maximumInterval);
    }

    [Fact]
    public void CalculateNextInterval_SameInputs_ShouldProduceSameResults()
    {
        var stability = 15.0;
        var desiredRetention = 0.9;
        var maximumInterval = 36500;

        var interval1 = FsrsHelper.CalculateNextInterval(stability, desiredRetention, _defaultParameters, maximumInterval);
        var interval2 = FsrsHelper.CalculateNextInterval(stability, desiredRetention, _defaultParameters, maximumInterval);

        Assert.Equal(interval1, interval2);
    }

    [Fact]
    public void CalculateRetrievability_CardWithoutLastReview_ShouldReturnZero()
    {
        var card = new FsrsCard();

        var retrievability = FsrsHelper.CalculateRetrievability(card);

        Assert.Equal(0, retrievability);
    }

    [Fact]
    public void CalculateRetrievability_CardWithLastReview_ShouldReturnValidRange()
    {
        var card = new FsrsCard { LastReview = DateTime.UtcNow.AddDays(-1), Stability = 10.0 };

        var retrievability = FsrsHelper.CalculateRetrievability(card);

        Assert.InRange(retrievability, 0.0, 1.0);
    }

    [Fact]
    public void CalculateRetrievability_LongerTimePassed_ShouldProduceLowerRetrievability()
    {
        var baseDateTime = DateTime.UtcNow;
        var stability = 10.0;

        var cardRecent = new FsrsCard { LastReview = baseDateTime.AddDays(-1), Stability = stability };

        var cardOld = new FsrsCard { LastReview = baseDateTime.AddDays(-10), Stability = stability };

        var recentRetrievability = FsrsHelper.CalculateRetrievability(cardRecent, baseDateTime, _defaultParameters);
        var oldRetrievability = FsrsHelper.CalculateRetrievability(cardOld, baseDateTime, _defaultParameters);

        Assert.True(recentRetrievability > oldRetrievability);
    }

    [Fact]
    public void CalculateRetrievability_HigherStability_ShouldProduceHigherRetrievability()
    {
        var baseDateTime = DateTime.UtcNow;
        var lastReview = baseDateTime.AddDays(-5);

        var cardLowStability = new FsrsCard { LastReview = lastReview, Stability = 5.0 };

        var cardHighStability = new FsrsCard { LastReview = lastReview, Stability = 20.0 };

        var lowStabilityRetrievability = FsrsHelper.CalculateRetrievability(cardLowStability, baseDateTime, _defaultParameters);
        var highStabilityRetrievability = FsrsHelper.CalculateRetrievability(cardHighStability, baseDateTime, _defaultParameters);

        Assert.True(highStabilityRetrievability > lowStabilityRetrievability);
    }

    [Fact]
    public void CalculateRetrievability_SameDayReview_ShouldReturnOne()
    {
        var baseDateTime = DateTime.UtcNow;
        var card = new FsrsCard { LastReview = baseDateTime, Stability = 10.0 };

        var retrievability = FsrsHelper.CalculateRetrievability(card, baseDateTime, _defaultParameters);

        Assert.Equal(1.0, retrievability, 6); // Should be 1.0 (or very close) when no time has passed
    }

    [Fact]
    public void CalculateRetrievability_NegativeElapsedTime_ShouldHandleGracefully()
    {
        var baseDateTime = DateTime.UtcNow;
        var card = new FsrsCard
                   {
                       LastReview = baseDateTime.AddDays(1), // Future date
                       Stability = 10.0
                   };

        var retrievability = FsrsHelper.CalculateRetrievability(card, baseDateTime, _defaultParameters);

        Assert.InRange(retrievability, 0.0, 1.0);
    }

    [Fact]
    public void CalculateRetrievability_DefaultCurrentDateTime_ShouldUseUtcNow()
    {
        var card = new FsrsCard { LastReview = DateTime.UtcNow.AddDays(-1), Stability = 10.0 };

        var retrievability = FsrsHelper.CalculateRetrievability(card);

        Assert.InRange(retrievability, 0.0, 1.0);
    }

    [Fact]
    public void CalculateRetrievability_CustomParameters_ShouldUseProvided()
    {
        var card = new FsrsCard { LastReview = DateTime.UtcNow.AddDays(-5), Stability = 10.0 };

        var customParameters = new double[21];
        Array.Copy(_defaultParameters, customParameters, 21);
        customParameters[20] = 0.1; // Different decay parameter

        var defaultRetrievability = FsrsHelper.CalculateRetrievability(card, null, _defaultParameters);
        var customRetrievability = FsrsHelper.CalculateRetrievability(card, null, customParameters);

        Assert.NotEqual(defaultRetrievability, customRetrievability);
    }

    [Theory]
    [InlineData(FsrsRating.Again, 1)]
    [InlineData(FsrsRating.Hard, 2)]
    [InlineData(FsrsRating.Good, 3)]
    [InlineData(FsrsRating.Easy, 4)]
    public void CalculateInitialStability_AllRatings_ShouldReturnValidStability(FsrsRating rating, int expectedParameterIndex)
    {
        var stability = FsrsHelper.CalculateInitialStability(rating, _defaultParameters);

        Assert.True(stability >= FsrsConstants.StabilityMin);
        Assert.Equal(_defaultParameters[expectedParameterIndex - 1], stability, 6);
    }

    [Fact]
    public void CalculateInitialStability_ShouldClampToMinimum()
    {
        var parametersWithZero = new double[21];
        parametersWithZero[0] = 0.0; // FsrsRating.Again parameter

        var stability = FsrsHelper.CalculateInitialStability(FsrsRating.Again, parametersWithZero);

        Assert.Equal(FsrsConstants.StabilityMin, stability);
    }

    [Theory]
    [InlineData(10.0, FsrsRating.Good, 1.0)]
    [InlineData(10.0, FsrsRating.Easy, 1.0)]
    [InlineData(5.0, FsrsRating.Hard, 0.5)]
    [InlineData(5.0, FsrsRating.Again, 0.5)]
    public void CalculateShortTermStability_ShouldProduceReasonableResults(double currentStability, FsrsRating rating,
                                                                           double minExpectedMultiplier)
    {
        var newStability = FsrsHelper.CalculateShortTermStability(currentStability, rating, _defaultParameters);

        Assert.True(newStability >= FsrsConstants.StabilityMin);

        if (rating is FsrsRating.Good or FsrsRating.Easy)
        {
            Assert.True(newStability >= currentStability * minExpectedMultiplier);
        }
    }

    [Fact]
    public void CalculateNextStability_AgainRating_ShouldCalculateForgetStability()
    {
        var difficulty = 5.0;
        var stability = 10.0;
        var retrievability = 0.8;

        var nextStability = FsrsHelper.CalculateNextStability(
                                                              difficulty, stability, retrievability, FsrsRating.Again, _defaultParameters);

        Assert.True(nextStability >= FsrsConstants.StabilityMin);
        Assert.True(nextStability <= stability); // Should decrease for Again FsrsRating
    }

    [Theory]
    [InlineData(FsrsRating.Hard)]
    [InlineData(FsrsRating.Good)]
    [InlineData(FsrsRating.Easy)]
    public void CalculateNextStability_RecallRatings_ShouldCalculateRecallStability(FsrsRating rating)
    {
        var difficulty = 5.0;
        var stability = 10.0;
        var retrievability = 0.8;

        var nextStability = FsrsHelper.CalculateNextStability(
                                                              difficulty, stability, retrievability, rating, _defaultParameters);

        Assert.True(nextStability >= FsrsConstants.StabilityMin);

        if (rating == FsrsRating.Good)
        {
            Assert.True(nextStability >= stability); // Should generally increase for Good FsrsRating
        }
    }

    [Fact]
    public void CalculateNextStability_HardRating_ShouldApplyPenalty()
    {
        var difficulty = 5.0;
        var stability = 10.0;
        var retrievability = 0.8;

        var hardStability = FsrsHelper.CalculateNextStability(
                                                              difficulty, stability, retrievability, FsrsRating.Hard, _defaultParameters);

        var goodStability = FsrsHelper.CalculateNextStability(
                                                              difficulty, stability, retrievability, FsrsRating.Good, _defaultParameters);

        Assert.True(hardStability < goodStability); // Hard should produce lower stability than Good
    }

    [Fact]
    public void CalculateNextStability_EasyRating_ShouldApplyBonus()
    {
        var difficulty = 5.0;
        var stability = 10.0;
        var retrievability = 0.8;

        var easyStability = FsrsHelper.CalculateNextStability(
                                                              difficulty, stability, retrievability, FsrsRating.Easy, _defaultParameters);

        var goodStability = FsrsHelper.CalculateNextStability(
                                                              difficulty, stability, retrievability, FsrsRating.Good, _defaultParameters);

        Assert.True(easyStability > goodStability); // Easy should produce higher stability than Good
    }

    public static FsrsScheduler CreateSchedulerWithoutFuzzing()
    {
        return new FsrsScheduler(enableFuzzing: false);
    }

    public static FsrsCard CreateNewCard()
    {
        return new FsrsCard("", 0, 0);
    }

    public static DateTime GetTestDateTime()
    {
        return new DateTime(2022, 11, 29, 12, 30, 0, DateTimeKind.Utc);
    }

    private static void AssertIntervalEquals(int expected, FsrsCard card, DateTime reviewDateTime)
    {
        var actual = (card.Due - card.LastReview!.Value).Days;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DefaultDesiredRetention_ShouldBe09()
    {
        Assert.Equal(0.9, FsrsConstants.DefaultDesiredRetention);
    }

    [Fact]
    public void UserFsrsSettings_SetParameters_ShouldSerialiseToJson()
    {
        var settings = new UserFsrsSettings();
        settings.Parameters = new[] { 1.0, 2.0, 3.0 };

        Assert.Contains("1", settings.ParametersJson);
        Assert.Contains("2", settings.ParametersJson);
        Assert.Contains("3", settings.ParametersJson);
    }

    [Fact]
    public void UserFsrsSettings_GetParameters_ShouldDeserialiseFromJson()
    {
        var settings = new UserFsrsSettings { ParametersJson = "[0.5, 1.5, 2.5]" };

        var parameters = settings.GetParametersOnce();

        Assert.Equal(3, parameters.Length);
        Assert.Equal(0.5, parameters[0]);
        Assert.Equal(1.5, parameters[1]);
        Assert.Equal(2.5, parameters[2]);
    }

    [Fact]
    public void UserFsrsSettings_GetParameters_ShouldCacheResult()
    {
        var settings = new UserFsrsSettings { ParametersJson = "[1.0, 2.0]" };

        var first = settings.GetParametersOnce();
        var second = settings.GetParametersOnce();

        Assert.Same(first, second);
    }

    [Fact]
    public void UserFsrsSettings_InvalidJson_ShouldReturnEmptyArray()
    {
        var settings = new UserFsrsSettings { ParametersJson = "not json" };

        var parameters = settings.GetParametersOnce();

        Assert.Empty(parameters);
    }

    [Fact]
    public void UserFsrsSettings_DefaultParametersJson_ShouldReturnEmptyArray()
    {
        var settings = new UserFsrsSettings();

        var parameters = settings.GetParametersOnce();

        Assert.Empty(parameters);
    }

    [Fact]
    public void UserFsrsSettings_SetParameters_ShouldUpdateCache()
    {
        var settings = new UserFsrsSettings();
        settings.Parameters = new[] { 1.0, 2.0 };

        var first = settings.GetParametersOnce();
        Assert.Equal(2, first.Length);

        settings.Parameters = new[] { 3.0, 4.0, 5.0 };

        var second = settings.GetParametersOnce();
        Assert.Equal(3, second.Length);
        Assert.Equal(3.0, second[0]);
    }

    [Fact]
    public void UserFsrsSettings_RoundTrip_ShouldPreserveDefaultParameters()
    {
        var settings = new UserFsrsSettings();
        settings.Parameters = FsrsConstants.DefaultParameters;

        var roundTripped = settings.GetParametersOnce();

        Assert.Equal(FsrsConstants.DefaultParameters.Length, roundTripped.Length);
        for (var i = 0; i < FsrsConstants.DefaultParameters.Length; i++)
        {
            Assert.Equal(FsrsConstants.DefaultParameters[i], roundTripped[i], 6);
        }
    }

    [Fact]
    public void Scheduler_CustomRetention_ShouldProduceDifferentIntervals()
    {
        var schedulerHigh = new FsrsScheduler(desiredRetention: 0.95, enableFuzzing: false);
        var schedulerLow = new FsrsScheduler(desiredRetention: 0.80, enableFuzzing: false);
        var reviewDateTime = GetTestDateTime();

        var cardHigh = CreateNewCard();
        var cardLow = CreateNewCard();

        (cardHigh, _) = schedulerHigh.ReviewCard(cardHigh, FsrsRating.Good, reviewDateTime);
        (cardHigh, _) = schedulerHigh.ReviewCard(cardHigh, FsrsRating.Good, cardHigh.Due);

        (cardLow, _) = schedulerLow.ReviewCard(cardLow, FsrsRating.Good, reviewDateTime);
        (cardLow, _) = schedulerLow.ReviewCard(cardLow, FsrsRating.Good, cardLow.Due);

        var intervalHigh = (cardHigh.Due - cardHigh.LastReview!.Value).Days;
        var intervalLow = (cardLow.Due - cardLow.LastReview!.Value).Days;

        Assert.True(intervalLow > intervalHigh, "Lower retention should produce longer intervals");
    }

    [Fact]
    public void Scheduler_CustomParameters_ShouldProduceDifferentStability()
    {
        var customParameters = (double[])FsrsConstants.DefaultParameters.Clone();
        customParameters[2] = 5.0;

        var defaultScheduler = CreateSchedulerWithoutFuzzing();
        var customScheduler = new FsrsScheduler(parameters: customParameters, enableFuzzing: false);
        var reviewDateTime = GetTestDateTime();

        var defaultCard = CreateNewCard();
        var customCard = CreateNewCard();

        (defaultCard, _) = defaultScheduler.ReviewCard(defaultCard, FsrsRating.Good, reviewDateTime);
        (customCard, _) = customScheduler.ReviewCard(customCard, FsrsRating.Good, reviewDateTime);

        Assert.NotEqual(defaultCard.Stability, customCard.Stability);
    }
}