using FluentAssertions;
using Jiten.Core.Data.Billing;
using Jiten.Core.Services;

namespace Jiten.Tests;

public class RoadmapEngineTests
{
    private static long K(int wordId, int readingIndex = 0) => RoadmapEngine.PackKey(wordId, readingIndex);

    /// <summary>Builds a deck whose words are (wordId, occurrences), sorted descending as the engine requires.</summary>
    private static RoadmapCandidate Deck(int deckId, params (int WordId, int Occurrences)[] words)
    {
        var ordered = words.OrderByDescending(w => w.Occurrences)
                           .Select(w => new RoadmapWord(K(w.WordId), w.Occurrences))
                           .ToArray();

        return new RoadmapCandidate
        {
            DeckId = deckId,
            WordCount = ordered.Sum(w => (long)w.Occurrences),
            Words = ordered
        };
    }

    /// <summary>Re-stamps a deck with a reading length; <see cref="Deck"/> leaves it unset so cost falls back to tokens.</summary>
    private static RoadmapCandidate Timed(RoadmapCandidate deck, double hours) => new()
    {
        DeckId = deck.DeckId,
        WordCount = deck.WordCount,
        LengthHours = hours,
        Words = deck.Words,
        Vector = deck.Vector
    };

    private static RoadmapDefinition Settings(Action<RoadmapDefinition>? tweak = null)
    {
        var definition = new RoadmapDefinition
        {
            ComprehensionFloor = 0.90,
            // Both pinned to the floor so their weighting is inert; the comfort and goal-target tests set them explicitly.
            ComfortTarget = 0.90,
            GoalComprehensionTarget = 0.90,
            AcquisitionThreshold = 5,
            Steps = 5,
            Preference = RoadmapPreference.Volume
        };
        tweak?.Invoke(definition);
        return definition;
    }

    private static RoadmapEngine.RoadmapInput Input(
        RoadmapDefinition settings,
        IReadOnlyList<RoadmapCandidate> candidates,
        HashSet<long> known,
        IReadOnlyDictionary<long, int>? ranks = null,
        RoadmapCandidate? goal = null,
        IReadOnlyDictionary<int, int[]>? prerequisites = null,
        IReadOnlySet<int>? completed = null) => new()
    {
        Settings = settings,
        Candidates = candidates,
        KnownWords = known,
        FrequencyRanks = ranks ?? new Dictionary<long, int>(),
        Goal = goal,
        Prerequisites = prerequisites ?? new Dictionary<int, int[]>(),
        CompletedDeckIds = completed ?? new HashSet<int>()
    };

    // ---- Primitives ---------------------------------------------------------

    [Fact]
    public void PackKey_RoundTrips()
    {
        var key = RoadmapEngine.PackKey(123456, 7);
        RoadmapEngine.UnpackWordId(key).Should().Be(123456);
        RoadmapEngine.UnpackReadingIndex(key).Should().Be(7);
    }

    [Fact]
    public void PackKey_DistinguishesReadingIndexes()
    {
        RoadmapEngine.PackKey(100, 0).Should().NotBe(RoadmapEngine.PackKey(100, 1));
    }

    [Fact]
    public void Coverage_IsOccurrenceWeighted_NotUniqueWeighted()
    {
        // One very common known word outweighs many rare unknown ones.
        var deck = Deck(1, (10, 90), (11, 5), (12, 5));
        var known = new HashSet<long> { K(10) };

        RoadmapEngine.Coverage(deck, known).Should().BeApproximately(0.90, 1e-9);
    }

    [Fact]
    public void Coverage_EmptyKnownSet_IsZero()
    {
        RoadmapEngine.Coverage(Deck(1, (10, 5)), new HashSet<long>()).Should().Be(0);
    }

    [Fact]
    public void AcquisitionSet_ExcludesKnownAndBelowThresholdWords()
    {
        var deck = Deck(1, (10, 20), (11, 8), (12, 4));
        var known = new HashSet<long> { K(10) };

        var acquired = RoadmapEngine.AcquisitionSet(deck, known, acquisitionThreshold: 5);

        // 10 is known, 12 falls under the threshold.
        acquired.Select(w => RoadmapEngine.UnpackWordId(w.Key)).Should().Equal(11);
    }

    [Fact]
    public void AcquisitionSet_HigherThreshold_AcquiresFewerWords()
    {
        var deck = Deck(1, (10, 9), (11, 6), (12, 3));
        var known = new HashSet<long>();

        RoadmapEngine.AcquisitionSet(deck, known, 3).Should().HaveCount(3);
        RoadmapEngine.AcquisitionSet(deck, known, 6).Should().HaveCount(2);
        RoadmapEngine.AcquisitionSet(deck, known, 10).Should().BeEmpty();
    }

    [Fact]
    public void GapToReadable_ReturnsEmpty_WhenDeckAlreadyClearsFloor()
    {
        var deck = Deck(1, (10, 95), (11, 5));
        var known = new HashSet<long> { K(10) };

        RoadmapEngine.GapToReadable(deck, known, 0.90).Should().BeEmpty();
    }

    [Fact]
    public void GapToReadable_CountsOnlyUnknownWords_HighestOccurrenceFirst()
    {
        var deck = Deck(1, (10, 50), (11, 30), (12, 20));
        var known = new HashSet<long>();

        var gap = RoadmapEngine.GapToReadable(deck, known, 0.90);

        // 50 + 30 = 80% is short of 90%, so the third word is needed too.
        gap.Select(w => RoadmapEngine.UnpackWordId(w.Key)).Should().Equal(10, 11, 12);
    }

    [Fact]
    public void GapToReadable_SkipsWordsTheUserAlreadyKnows()
    {
        var deck = Deck(1, (10, 50), (11, 30), (12, 20));
        var known = new HashSet<long> { K(10) };

        var gap = RoadmapEngine.GapToReadable(deck, known, 0.90);

        gap.Select(w => RoadmapEngine.UnpackWordId(w.Key)).Should().Equal(11, 12);
    }

    [Fact]
    public void WordValue_DiscountsTheLongTail()
    {
        // Without this the search rewards decks stuffed with rare vocabulary.
        var common = RoadmapEngine.WordValue(500);
        var rare = RoadmapEngine.WordValue(80000);

        common.Should().BeGreaterThan(rare);
        RoadmapEngine.WordValue(0).Should().BeLessThan(common);
    }

    // ---- The objective ------------------------------------------------------

    [Fact]
    public void Build_SkipsDecksBelowTheComprehensionFloor()
    {
        var readable = Deck(1, (10, 90), (20, 10));
        var unreadable = Deck(2, (30, 50), (40, 50));
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(Settings(), [readable, unreadable], known));

        result.Steps.Should().ContainSingle();
        result.Steps[0].DeckId.Should().Be(1);
    }

    [Fact]
    public void Build_AmongReadableDecks_PrefersTheOneTeachingMore()
    {
        // Both clear the floor on the same known word; deck 2 teaches two new words, deck 1 teaches one.
        var teachesOne = Deck(1, (10, 90), (20, 10));
        var teachesTwo = Deck(2, (10, 90), (30, 5), (31, 5));
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(Settings(s => s.Steps = 1), [teachesOne, teachesTwo], known));

        result.Steps[0].DeckId.Should().Be(2);
    }

    [Fact]
    public void Build_RaisingTheFloor_ProducesGentlerSteps()
    {
        // The claim the design rests on: "fewest new words" is not a mode, it is the floor moving up.
        var gentle = Deck(1, (10, 96), (20, 4));               // 96% known, teaches one word
        var aggressive = Deck(2, (10, 90), (30, 5), (31, 5));  // 90% known, teaches two
        var known = new HashSet<long> { K(10) };
        var candidates = new[] { gentle, aggressive };

        var lowFloor = RoadmapEngine.Build(Input(Settings(s => { s.Steps = 1; s.AcquisitionThreshold = 1; }), candidates, known));
        var highFloor = RoadmapEngine.Build(Input(
                                               Settings(s => { s.Steps = 1; s.AcquisitionThreshold = 1; s.ComprehensionFloor = 0.95; }),
                                               candidates, known));

        lowFloor.Steps[0].DeckId.Should().Be(2, "a low floor lets the higher-yield deck qualify");
        highFloor.Steps[0].DeckId.Should().Be(1, "raising the floor filters out the bigger leap");
    }

    [Fact]
    public void Build_EfficiencyPreference_PenalisesLongDecksForTheSameYield()
    {
        // Same acquisition set, but deck 2 is ten times longer.
        var shortDeck = Deck(1, (10, 900), (20, 100));
        var longDeck = Deck(2, (10, 9000), (20, 1000));
        var known = new HashSet<long> { K(10) };

        var volume = RoadmapEngine.Build(Input(
                                            Settings(s => { s.Steps = 1; s.Preference = RoadmapPreference.Volume; }),
                                            [shortDeck, longDeck], known));

        var efficiency = RoadmapEngine.Build(Input(
                                                 Settings(s => { s.Steps = 1; s.Preference = RoadmapPreference.Efficiency; }),
                                                 [shortDeck, longDeck], known));

        volume.Steps[0].NewWordsCount().Should().Be(1);
        efficiency.Steps[0].DeckId.Should().Be(1, "efficiency divides yield by length");
    }

    [Fact]
    public void Build_ValueWeighting_PrefersCommonVocabularyOverRareJunk()
    {
        var commonWords = Deck(1, (10, 90), (20, 5), (21, 5));
        var rareWords = Deck(2, (10, 90), (30, 4), (31, 3), (32, 3));
        var known = new HashSet<long> { K(10) };

        var ranks = new Dictionary<long, int>
        {
            [K(20)] = 300, [K(21)] = 500,
            [K(30)] = 90000, [K(31)] = 95000, [K(32)] = 99000
        };

        var result = RoadmapEngine.Build(Input(
                                            Settings(s => { s.Steps = 1; s.AcquisitionThreshold = 3; }),
                                            [commonWords, rareWords], known, ranks));

        result.Steps[0].DeckId.Should().Be(1, "three rare words are worth less than two common ones");
    }

    [Fact]
    public void Build_DoesNotWalkAFranchise_BecauseOverlapShrinksTheAcquisitionSet()
    {
        // The design claim: sequels are anti-selected by the objective, so no diversity cap is needed.
        var season1 = Deck(1, (10, 500), (20, 10), (21, 10), (22, 10));
        var season2 = Deck(2, (10, 500), (20, 10), (21, 10), (22, 10), (23, 6));
        var unrelated = Deck(3, (10, 500), (40, 9), (41, 9), (42, 9));
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(
                                            Settings(s => { s.Steps = 2; s.PinnedDeckIds = [1]; }),
                                            [season1, season2, unrelated], known));

        result.Steps.Should().HaveCount(2);
        result.Steps[0].DeckId.Should().Be(1);
        result.Steps[1].DeckId.Should().Be(3, "the sequel's overlap with step 1 leaves it little left to teach");
    }

    [Fact]
    public void Build_FoldsForwardOnlyWordsMeetingTheAcquisitionThreshold()
    {
        // Reading a deck must not be treated as learning all of its vocabulary.
        var step1 = Deck(1, (10, 200), (20, 9), (21, 2));
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(Settings(s => s.Steps = 1), [step1], known));

        var acquired = result.Steps[0].AcquiredWords.Select(w => RoadmapEngine.UnpackWordId(w.Key)).ToList();
        acquired.Should().Equal(20);
        acquired.Should().NotContain(21, "a word met twice is not acquired at threshold 5");
    }

    [Fact]
    public void Build_EmitsDrillStep_WhenNothingClearsTheFloor()
    {
        // A beginner must get actionable output, not an empty roadmap.
        var tooHard = Deck(1, (10, 50), (11, 30), (12, 20));
        var known = new HashSet<long>();

        var result = RoadmapEngine.Build(Input(Settings(), [tooHard], known));

        result.Steps.Should().BeEmpty();
        result.Drill.Should().NotBeNull();
        result.Drill!.DeckId.Should().Be(1);
        result.Drill.Words.Should().NotBeEmpty();
    }

    [Fact]
    public void Build_StopsAtTheRequestedStepCount()
    {
        var known = new HashSet<long> { K(10) };
        var candidates = Enumerable.Range(1, 10)
                                   .Select(i => Deck(i, (10, 90), (100 + i, 10)))
                                   .ToList();

        var result = RoadmapEngine.Build(Input(Settings(s => s.Steps = 3), candidates, known));

        result.Steps.Should().HaveCount(3);
    }

    [Fact]
    public void Build_NeverRepeatsADeck()
    {
        var known = new HashSet<long> { K(10) };
        var candidates = Enumerable.Range(1, 4)
                                   .Select(i => Deck(i, (10, 90), (100 + i, 10)))
                                   .ToList();

        var result = RoadmapEngine.Build(Input(Settings(s => s.Steps = 4), candidates, known));

        result.Steps.Select(s => s.DeckId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Build_SkipsDecksTheUserAlreadyCompleted()
    {
        var known = new HashSet<long> { K(10) };
        var completed = Deck(1, (10, 90), (20, 10));
        var fresh = Deck(2, (10, 90), (30, 10));

        var result = RoadmapEngine.Build(Input(Settings(s => s.Steps = 5), [completed, fresh], known,
                                               completed: new HashSet<int> { 1 }));

        result.Steps.Should().ContainSingle();
        result.Steps[0].DeckId.Should().Be(2);
    }

    // ---- Comfort target -----------------------------------------------------

    [Fact]
    public void ComfortWeight_IsFullAtOrAboveTheTarget_AndLowestAtTheFloor()
    {
        var settings = Settings(s => { s.ComprehensionFloor = 0.80; s.ComfortTarget = 0.90; });

        RoadmapEngine.ComfortWeight(0.95, settings).Should().Be(1.0);
        RoadmapEngine.ComfortWeight(0.90, settings).Should().Be(1.0);
        RoadmapEngine.ComfortWeight(0.85, settings).Should().BeApproximately(0.65, 1e-6);
        RoadmapEngine.ComfortWeight(0.80, settings).Should().BeApproximately(0.3, 1e-6);
    }

    [Fact]
    public void ComfortWeight_IsInert_WhenTargetEqualsFloor()
    {
        var settings = Settings(s => { s.ComprehensionFloor = 0.90; s.ComfortTarget = 0.90; });

        RoadmapEngine.ComfortWeight(0.90, settings).Should().Be(1.0);
    }

    [Fact]
    public void Build_PrefersAComfortableTitle_OverAMarginallyRicherHardOne()
    {
        // Without the comfort weight every pick lands exactly on the floor.
        var hard = Deck(1, (10, 80), (30, 10), (31, 10));       // 80% known, teaches two
        var comfortable = Deck(2, (10, 92), (40, 8));           // 92% known, teaches one
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(
                                            Settings(s =>
                                            {
                                                s.Steps = 1;
                                                s.ComprehensionFloor = 0.80;
                                                s.ComfortTarget = 0.90;
                                                s.AcquisitionThreshold = 5;
                                            }),
                                            [hard, comfortable], known));

        result.Steps[0].DeckId.Should().Be(2);
    }

    [Fact]
    public void Build_StillTakesAHardTitle_WhenItTeachesFarMore()
    {
        // The comfort target is a tilt, not a veto — a title that teaches many times more still wins.
        var hard = Deck(1, (10, 80), (30, 5), (31, 5), (32, 5), (33, 5));  // 80% known, teaches four
        var comfortable = Deck(2, (10, 95), (40, 5));                       // 95% known, teaches one
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(
                                            Settings(s =>
                                            {
                                                s.Steps = 1;
                                                s.ComprehensionFloor = 0.80;
                                                s.ComfortTarget = 0.95;
                                            }),
                                            [hard, comfortable], known));

        result.Steps[0].DeckId.Should().Be(1);
    }

    [Fact]
    public void Build_ComfortTarget_NeverAdmitsTitlesBelowTheHardFloor()
    {
        var belowFloor = Deck(1, (10, 70), (30, 10), (31, 10), (32, 10));
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(
                                            Settings(s => { s.ComprehensionFloor = 0.80; s.ComfortTarget = 0.90; }),
                                            [belowFloor], known));

        result.Steps.Should().BeEmpty();
    }

    // ---- Prerequisite ordering ---------------------------------------------

    [Fact]
    public void Build_NeverSchedulesASequelBeforeItsPrequel()
    {
        // Story continuity, not vocabulary: sequel is deliberately the higher-scoring pick.
        var prequel = Deck(1, (10, 200), (20, 10));
        var sequel = Deck(2, (10, 200), (30, 6), (31, 6));
        var known = new HashSet<long> { K(10) };
        var prerequisites = new Dictionary<int, int[]> { [2] = [1] };

        var result = RoadmapEngine.Build(Input(Settings(s => s.Steps = 2), [prequel, sequel], known,
                                              prerequisites: prerequisites));

        result.Steps.Select(s => s.DeckId).Should().Equal(1, 2);
    }

    [Fact]
    public void Build_PrequelOutsideTheCandidateSet_DoesNotDeadlockItsSequel()
    {
        var sequel = Deck(2, (10, 90), (30, 10));
        var known = new HashSet<long> { K(10) };
        var prerequisites = new Dictionary<int, int[]> { [2] = [999] };

        var result = RoadmapEngine.Build(Input(Settings(s => s.Steps = 1), [sequel], known,
                                              prerequisites: prerequisites));

        result.Steps.Should().ContainSingle();
        result.Steps[0].DeckId.Should().Be(2);
    }

    [Fact]
    public void Build_PrequelAlreadyCompleted_UnblocksTheSequel()
    {
        var prequel = Deck(1, (10, 90), (20, 10));
        var sequel = Deck(2, (10, 90), (30, 10));
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(Settings(s => s.Steps = 1), [prequel, sequel], known,
                                              prerequisites: new Dictionary<int, int[]> { [2] = [1] },
                                              completed: new HashSet<int> { 1 }));

        result.Steps[0].DeckId.Should().Be(2);
    }

    // ---- Goal mode ----------------------------------------------------------

    [Fact]
    public void Build_GoalAlreadyReadable_ReturnsImmediatelyWithoutSteps()
    {
        var goal = Deck(99, (10, 95), (20, 5));
        var filler = Deck(1, (10, 90), (30, 10));
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(Settings(), [filler], known, goal: goal));

        result.GoalReached.Should().BeTrue();
        result.Steps.Should().BeEmpty();
        result.GoalWordsRemaining.Should().Be(0);
    }

    [Fact]
    public void Build_GoalMode_PrefersDecksTeachingTheGoalsVocabulary()
    {
        // Both readable and equally sized; only deck 1 teaches words the goal actually uses.
        var goal = Deck(99, (10, 80), (50, 10), (51, 10));
        var towardGoal = Deck(1, (10, 300), (50, 6), (51, 6));
        var awayFromGoal = Deck(2, (10, 300), (60, 6), (61, 6), (62, 6));
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(Settings(s => s.Steps = 1), [towardGoal, awayFromGoal], known, goal: goal));

        result.Steps[0].DeckId.Should().Be(1);
    }

    [Fact]
    public void Build_GoalMode_StopsOnceTheGoalClearsTheFloor()
    {
        var goal = Deck(99, (10, 85), (50, 15));
        var unlocks = Deck(1, (10, 90), (50, 10));
        var other = Deck(2, (10, 90), (70, 10));
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(Settings(s => s.Steps = 5), [unlocks, other], known, goal: goal));

        result.GoalReached.Should().BeTrue();
        result.Steps.Should().ContainSingle();
        result.Steps[0].DeckId.Should().Be(1);
    }

    [Fact]
    public void Build_GoalMode_UnreachableGoal_ReportsClosestApproachRatherThanAFakePath()
    {
        var goal = Deck(99, (10, 10), (80, 45), (81, 45));
        var useless = Deck(1, (10, 90), (20, 10));
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(Settings(s => s.Steps = 3), [useless], known, goal: goal));

        result.GoalReached.Should().BeFalse();
        result.GoalCoverageFinal.Should().BeLessThan(0.90);
        result.GoalWordsRemaining.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Build_GoalMode_GoalTargetIsIndependentOfTheSteppingStoneFloor()
    {
        // Clearing the stepping-stone floor is not "you already know enough" — only the goal target is.
        var goal = Deck(99, (10, 84), (50, 16));
        var teachesNothingUseful = Deck(1, (10, 90), (20, 10));
        var known = new HashSet<long> { K(10) };
        var settings = Settings(s =>
        {
            s.Steps = 3;
            s.ComprehensionFloor = 0.80;
            s.GoalComprehensionTarget = 0.95;
        });

        var result = RoadmapEngine.Build(Input(settings, [teachesNothingUseful], known, goal: goal));

        result.GoalReached.Should().BeFalse("84% clears the floor but not the 95% goal target");
        result.GoalCoverageFinal.Should().BeApproximately(0.84, 1e-9);
        result.GoalWordsRemaining.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Build_GoalMode_RunsPastTheStepCountUntilTheTargetIsReached()
    {
        // The step count is a discovery-mode control; it must not cap a goal plan short of its target.
        var goal = Deck(99, (1, 20), (50, 20), (51, 20), (52, 20), (53, 20));
        var known = new HashSet<long> { K(1) };
        var stones = new[]
        {
            Deck(10, (1, 90), (50, 10)),
            Deck(11, (1, 90), (51, 10)),
            Deck(12, (1, 90), (52, 10)),
            Deck(13, (1, 90), (53, 10))
        };
        var settings = Settings(s =>
        {
            s.Steps = 1;
            s.ComprehensionFloor = 0.50;
            s.ComfortTarget = 0.50;
            s.GoalComprehensionTarget = 0.90;
        });

        var result = RoadmapEngine.Build(Input(settings, stones, known, goal: goal));

        result.Steps.Should().HaveCount(4, "the plan runs as many steps as the target needs, not the step count");
        result.GoalReached.Should().BeTrue();
        result.GoalCoverageFinal.Should().Be(1.0);
    }

    [Fact]
    public void Build_GoalMode_ReportsCeilingWhenRemainingWordsAreTargetExclusive()
    {
        // Word 60 exists only in the goal, so the plan tops out below target and reports a ceiling, not a shortfall.
        var goal = Deck(99, (1, 80), (50, 10), (60, 10));
        var teachesGoalWord = Deck(10, (1, 90), (50, 10));
        var unrelated = Deck(11, (1, 90), (70, 10));
        var known = new HashSet<long> { K(1) };
        var settings = Settings(s =>
        {
            s.ComprehensionFloor = 0.50;
            s.ComfortTarget = 0.50;
            s.GoalComprehensionTarget = 0.95;
        });

        var result = RoadmapEngine.Build(Input(settings, [teachesGoalWord, unrelated], known, goal: goal));

        result.GoalReached.Should().BeFalse();
        result.GoalCeilingReached.Should().BeTrue();
        result.GoalUnreachableWords.Should().Be(1);
        result.GoalCoverageFinal.Should().BeApproximately(0.90, 1e-9);
        result.Drill.Should().BeNull("a stepping-stone drill is noise once the goal has topped out");
        result.Steps.Should().ContainSingle().Which.DeckId.Should().Be(10);
    }

    [Fact]
    public void Build_GoalMode_NeverSchedulesTheGoalAsAStepTowardItself()
    {
        var goal = Deck(99, (10, 85), (50, 15));
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(Settings(), [goal], known, goal: goal));

        result.Steps.Should().NotContain(s => s.DeckId == 99);
    }

    [Fact]
    public void Build_GoalMode_NeverSchedulesTheGoalsOwnSequel()
    {
        // Goal weighting makes the goal's own sequel the top pick, but consuming it first inverts story order.
        var goal = Deck(100, (2, 50), (3, 50));
        var sequelOfGoal = Deck(200, (1, 90), (2, 5), (3, 5));
        var known = new HashSet<long> { K(1) };
        var prerequisites = new Dictionary<int, int[]> { [200] = [100] };

        var result = RoadmapEngine.Build(Input(Settings(), [sequelOfGoal], known, goal: goal,
                                               prerequisites: prerequisites));

        result.Steps.Should().BeEmpty();
        result.GoalReached.Should().BeFalse();
    }

    [Fact]
    public void Build_GoalMode_SkipsDecksTeachingNothingTheGoalUses()
    {
        // With zero goal overlap everywhere, iteration order must not pick an arbitrary filler step.
        var goal = Deck(99, (50, 100));
        var unrelated = Deck(1, (10, 90), (20, 5), (21, 5));
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(Settings(s => s.Steps = 3), [unrelated], known, goal: goal));

        result.Steps.Should().BeEmpty();
        result.GoalReached.Should().BeFalse();
        result.GoalWordsRemaining.Should().BeGreaterThan(0);
    }

    // ---- Swap ---------------------------------------------------------------

    [Fact]
    public void Build_ExcludedDeck_IsNeverScheduled()
    {
        var rejected = Deck(1, (10, 90), (20, 6), (21, 6));
        var alternative = Deck(2, (10, 90), (30, 10));
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(
                                            Settings(s => { s.Steps = 1; s.ExcludedDeckIds = [1]; }),
                                            [rejected, alternative], known));

        result.Steps[0].DeckId.Should().Be(2);
    }

    [Fact]
    public void Build_SwappedAwayPrequel_StillBlocksItsSequel()
    {
        // Promoting the rejected prequel's sequel in its place would still break story order.
        var prequel = Deck(1, (9, 100));
        var sequel = Deck(2, (10, 90), (30, 10));
        var known = new HashSet<long> { K(10) };
        var prerequisites = new Dictionary<int, int[]> { [2] = [1] };

        var result = RoadmapEngine.Build(Input(
                                            Settings(s => { s.Steps = 1; s.ExcludedDeckIds = [1]; }),
                                            [prequel, sequel], known, prerequisites: prerequisites));

        result.Steps.Should().BeEmpty();
    }

    [Fact]
    public void Build_PinnedPrefix_IsReplayedInOrderBeforeTheSearchResumes()
    {
        // Swapping step 3 must not reshuffle steps 1-2, even when they were not the top-scoring picks.
        var a = Deck(1, (10, 300), (20, 10));
        var b = Deck(2, (10, 300), (30, 10));
        var c = Deck(3, (10, 300), (40, 6), (41, 6), (42, 6));
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(
                                            Settings(s => { s.Steps = 3; s.PinnedDeckIds = [2, 1]; }),
                                            [a, b, c], known));

        result.Steps.Select(s => s.DeckId).Take(2).Should().Equal(2, 1);
        result.Steps[2].DeckId.Should().Be(3);
    }

    [Fact]
    public void Build_PinnedPrefixPlusExclusion_ReplacesOnlyTheSwappedStep()
    {
        var pinned = Deck(1, (10, 90), (20, 10));
        var rejected = Deck(2, (10, 90), (30, 8), (31, 8));
        var replacement = Deck(3, (10, 90), (40, 8));
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(
                                            Settings(s =>
                                            {
                                                s.Steps = 2;
                                                s.PinnedDeckIds = [1];
                                                s.ExcludedDeckIds = [2];
                                            }),
                                            [pinned, rejected, replacement], known));

        result.Steps.Select(s => s.DeckId).Should().Equal(1, 3);
    }

    [Fact]
    public void Build_PinnedDeckThatNoLongerQualifies_IsSkippedRatherThanCrashing()
    {
        var missing = 404;
        var available = Deck(1, (10, 90), (20, 10));
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(
                                            Settings(s => { s.Steps = 2; s.PinnedDeckIds = [missing]; }),
                                            [available], known));

        result.Steps.Should().ContainSingle();
        result.Steps[0].DeckId.Should().Be(1);
    }

    // ---- Content-similarity slider -----------------------------------------

    [Fact]
    public void Build_PositiveSimilarity_FavoursContentCloseToWhatWasAlreadyRead()
    {
        var known = new HashSet<long> { K(10) };

        var similar = new RoadmapCandidate
        {
            DeckId = 1, WordCount = 100, Vector = [1f, 0f],
            Words = [new RoadmapWord(K(10), 90), new RoadmapWord(K(20), 10)]
        };
        var different = new RoadmapCandidate
        {
            DeckId = 2, WordCount = 100, Vector = [0f, 1f],
            Words = [new RoadmapWord(K(10), 90), new RoadmapWord(K(30), 10)]
        };

        var seed = new List<float[]> { new[] { 1f, 0f } };

        var towardSimilar = RoadmapEngine.Build(new RoadmapEngine.RoadmapInput
        {
            Settings = Settings(s => { s.Steps = 1; s.ContentSimilarity = 2.0; }),
            Candidates = [similar, different],
            KnownWords = known,
            FrequencyRanks = new Dictionary<long, int>(),
            SeedVectors = seed
        });

        var towardDifferent = RoadmapEngine.Build(new RoadmapEngine.RoadmapInput
        {
            Settings = Settings(s => { s.Steps = 1; s.ContentSimilarity = -2.0; }),
            Candidates = [similar, different],
            KnownWords = known,
            FrequencyRanks = new Dictionary<long, int>(),
            SeedVectors = seed
        });

        towardSimilar.Steps[0].DeckId.Should().Be(1);
        towardDifferent.Steps[0].DeckId.Should().Be(2);
    }

    [Fact]
    public void Build_SimilarityMultiplierIsClamped_SoVocabularyStillDrivesSelection()
    {
        // An unclamped exp(λ·cos) would let taste overwhelm the acquisition score entirely.
        var known = new HashSet<long> { K(10) };

        var similarButEmpty = new RoadmapCandidate
        {
            DeckId = 1, WordCount = 100, Vector = [1f, 0f],
            Words = [new RoadmapWord(K(10), 95), new RoadmapWord(K(20), 5)]
        };
        var differentButRich = new RoadmapCandidate
        {
            DeckId = 2, WordCount = 100, Vector = [0f, 1f],
            Words =
            [
                new RoadmapWord(K(10), 90), new RoadmapWord(K(30), 3), new RoadmapWord(K(31), 3),
                new RoadmapWord(K(32), 2), new RoadmapWord(K(33), 2)
            ]
        };

        var result = RoadmapEngine.Build(new RoadmapEngine.RoadmapInput
        {
            Settings = Settings(s =>
            {
                s.Steps = 1;
                s.AcquisitionThreshold = 2;
                s.ContentSimilarity = 3.0;
            }),
            Candidates = [similarButEmpty, differentButRich],
            KnownWords = known,
            FrequencyRanks = new Dictionary<long, int>(),
            SeedVectors = [[1f, 0f]]
        });

        result.Steps[0].DeckId.Should().Be(2, "four new words at max clamp still beat one");
    }

    [Fact]
    public void Build_GoalMode_WeightsGoalWordsByOccurrenceNotByCount()
    {
        // The goal leans on word 50 and merely mentions 60-69. Buying the ten rare ones moves goal coverage by
        // 10 tokens; buying the one frequent word moves it by 100. A sublinear occurrence weight inverts this.
        var goalWords = new List<(int, int)> { (10, 800), (50, 100) };
        for (var w = 60; w < 70; w++) goalWords.Add((w, 1));
        var goal = Deck(99, goalWords.ToArray());

        var oneFrequentWord = Deck(1, (10, 900), (50, 10));
        var tenRareWords = Deck(2, (10, 900), (60, 10), (61, 10), (62, 10), (63, 10), (64, 10),
                                (65, 10), (66, 10), (67, 10), (68, 10), (69, 10));
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(Settings(s => s.Steps = 1), [tenRareWords, oneFrequentWord], known,
                                               goal: goal));

        result.Steps[0].DeckId.Should().Be(1, "one word worth 100 goal tokens beats ten worth 1 each");
    }

    [Fact]
    public void Build_GoalMode_ReportsTheMinimumWordsNeededBeforeAnyStepIsTaken()
    {
        // 90% known; reaching 95% needs 5 more tokens, which word 50 supplies on its own.
        var goal = Deck(99, (10, 90), (50, 6), (51, 4));
        var filler = Deck(1, (10, 90), (50, 10));
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(Settings(s => s.GoalComprehensionTarget = 0.95),
                                               [filler], known, goal: goal));

        result.GoalWordsAtStart.Should().Be(1);
    }

    [Fact]
    public void Build_GoalMode_SeparatesGoalRelevantWordsFromTheRestOfAStepsYield()
    {
        var goal = Deck(99, (10, 80), (50, 10), (51, 10));
        var mixed = Deck(1, (10, 300), (50, 6), (51, 6), (60, 6));
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(Settings(s => s.Steps = 1), [mixed], known, goal: goal));

        result.Steps[0].NewWordsCount().Should().Be(3);
        result.Steps[0].GoalNewWords.Should().Be(2, "word 60 never appears in the goal");
    }

    [Fact]
    public void Build_EfficiencyPreference_PricesByHoursRatherThanTokenCount()
    {
        // Identical token counts and identical yield, but one takes ten times as long to get through.
        var quick = Timed(Deck(1, (10, 900), (20, 100)), 1.0);
        var slow = Timed(Deck(2, (10, 900), (20, 100)), 10.0);
        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(
                                             Settings(s => { s.Steps = 1; s.Preference = RoadmapPreference.Efficiency; }),
                                             [slow, quick], known));

        result.Steps[0].DeckId.Should().Be(1, "equal token counts hide a tenfold difference in time spent");
    }

    [Fact]
    public void Build_EfficiencyPreference_DoesNotLetShortTitlesWinOnRateAlone()
    {
        // The short title teaches more per hour, but a route made of these cannot reach a goal inside the
        // step budget. Charging every title a fixed commitment on top of its runtime is what stops the plan
        // filling with them.
        var snack = Timed(Deck(1, (10, 4000), (20, 40), (21, 40)), 0.5);

        var longWords = new List<(int, int)> { (10, 100000) };
        for (var w = 30; w < 50; w++) longWords.Add((w, 400));
        var substantial = Timed(Deck(2, longWords.ToArray()), 20.0);

        var known = new HashSet<long> { K(10) };

        var result = RoadmapEngine.Build(Input(
                                             Settings(s => { s.Steps = 1; s.Preference = RoadmapPreference.Efficiency; }),
                                             [snack, substantial], known));

        // Per hour the snack wins outright (1.4 vs 0.35); the fixed commitment inverts it (0.13 vs 0.28).
        result.Steps[0].DeckId.Should().Be(2);
    }

    [Fact]
    public void Build_GoalMode_StopsAtTheGoalStepBudget()
    {
        var goal = Deck(99, (1, 20), (50, 20), (51, 20), (52, 20), (53, 20));
        var known = new HashSet<long> { K(1) };
        var stones = new[]
        {
            Deck(10, (1, 90), (50, 10)),
            Deck(11, (1, 90), (51, 10)),
            Deck(12, (1, 90), (52, 10)),
            Deck(13, (1, 90), (53, 10))
        };

        var settings = Settings(s =>
        {
            s.GoalSteps = 2;
            s.GoalComprehensionTarget = 0.95;
        });

        var result = RoadmapEngine.Build(Input(settings, stones, known, goal: goal));

        result.Steps.Should().HaveCount(2, "the budget binds before the target is reached");
        result.GoalReached.Should().BeFalse();
    }

    [Fact]
    public void Definition_StoredBeforeTheGoalBudgetExisted_KeepsTheOldCeiling()
    {
        // Plans serialised before GoalSteps existed must not silently inherit the discovery default.
        var roadmap = new UserRoadmap { DefinitionJson = """{"Steps":5,"ComprehensionFloor":0.8}""" };

        roadmap.Definition.GoalSteps.Should().Be(RoadmapDefinition.MaxGoalSteps);
        roadmap.Definition.Steps.Should().Be(5);
    }

    [Fact]
    public void CosineSimilarity_HandlesNullAndMismatchedVectors()
    {
        RoadmapEngine.CosineSimilarity(null, [1f, 0f]).Should().Be(0);
        RoadmapEngine.CosineSimilarity([1f, 0f], null).Should().Be(0);
        RoadmapEngine.CosineSimilarity([1f, 0f], [1f, 0f, 0f]).Should().Be(0);
        RoadmapEngine.CosineSimilarity([1f, 0f], [1f, 0f]).Should().BeApproximately(1.0, 1e-6);
    }
}

internal static class RoadmapStepAssertionExtensions
{
    public static int NewWordsCount(this RoadmapEngineStep step) => step.AcquiredWords.Count;
}
