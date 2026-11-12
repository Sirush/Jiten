using FluentAssertions;
using Jiten.Api.Services.ReadingCheck;

namespace Jiten.Tests;

public class ReadingCheckEngineTests
{
    private static ReadingCandidate C(int occ, int rank, int id = 1) => new(id, 0, occ, rank, 1);

    private static ReadingFit FitOf(double a, double b = 0.8, double c = -0.5) => new(a, b, c, 0.5, new double[4, 4]);

    /// <summary>Candidates spread over exposure and global rank like a real selection.</summary>
    private static List<ReadingCandidate> Pool()
    {
        var random = new Random(4);
        return Enumerable.Range(1, 3000).Select(i => C(5 + (int)(400 * Math.Pow(random.NextDouble(), 3)), 5000 + random.Next(80000), i)).ToList();
    }

    private static List<(ReadingCandidate, bool)> Answers(Func<ReadingCandidate, bool> knows, int count = 60)
    {
        var random = new Random(9);
        return Enumerable.Range(10_000, count)
                         .Select(i => C(2 + (int)(300 * Math.Pow(random.NextDouble(), 2)), 3000 + random.Next(100000), i))
                         .Select(c => (c, knows(c)))
                         .ToList();
    }

    [Fact]
    public void Fit_ReaderWhoKnowsMore_GetsAHigherIntercept()
    {
        var strong = ReadingCheckEngine.Fit(Answers(c => c.Occurrences >= 10));
        var weak = ReadingCheckEngine.Fit(Answers(c => c.Occurrences >= 150));

        strong.A.Should().BeGreaterThan(weak.A);
    }

    [Fact]
    public void Fit_MoreAnswersNarrowTheIntercept()
    {
        var few = ReadingCheckEngine.Fit(Answers(c => c.Occurrences >= 40, 12));
        var many = ReadingCheckEngine.Fit(Answers(c => c.Occurrences >= 40, 90));

        many.Covariance[0, 0].Should().BeLessThan(few.Covariance[0, 0]);
    }

    [Fact]
    public void Fit_WithoutAnswers_ReturnsThePrior()
    {
        var fit = ReadingCheckEngine.Fit([]);

        (fit.A, fit.B, fit.C, fit.E).Should().Be((0, 0.8, -0.5, 0.5));
    }

    [Fact]
    public void Fit_ReaderIndifferentToExposure_GetsAFlatterExposureSlope()
    {
        var byExposure = ReadingCheckEngine.Fit(Answers(c => c.Occurrences >= 40, 120));
        var byRank = ReadingCheckEngine.Fit(Answers(c => c.Rank < 40000, 120));

        byRank.B.Should().BeLessThan(byExposure.B);
        byRank.C.Should().BeLessThan(byExposure.C);
    }

    [Fact]
    public void Fit_ReaderWhoKnowsEveryExpression_RaisesTheirOdds()
    {
        var answers = Answers(c => c.Occurrences >= 150, 80)
                      .Concat(Enumerable.Range(50_000, 10).Select(i => (C(8, 30000, i) with { Expression = true }, true)))
                      .ToList();

        var fit = ReadingCheckEngine.Fit(answers);

        fit.E.Should().BeGreaterThan(1);
        fit.Probability(C(8, 30000) with { Expression = true }).Should().BeGreaterThan(fit.Probability(C(8, 30000)) + 0.2);
    }

    [Fact]
    public void Fit_StaysFiniteOnAnswersThatThrowFullNewtonStepsOff()
    {
        (int Occ, int Rank, bool Known)[] raw =
        [
            (26, 3744, true), (30, 4908, true), (35, 6950, false), (41, 9298, false), (42, 9797, false), (50, 14037, false),
            (11, 10038, false), (7, 18450, false), (6, 22646, false), (3, 57119, false), (3, 57127, false), (2, 98331, false),
            (63, 2729, false), (74, 2853, false), (101, 2922, true), (131, 2986, false), (154, 3039, true), (145, 3109, true),
            (220, 2047, true), (293, 2097, false), (258, 2206, false), (264, 2242, true), (504, 2369, true), (409, 2450, true)
        ];
        var answers = raw.Select((a, i) => (C(a.Occ, a.Rank, i + 1), a.Known)).ToList();

        var fit = ReadingCheckEngine.Fit(answers);

        Math.Abs(fit.A).Should().BeLessThan(10);
    }

    [Fact]
    public void Select_ReaderWhoKnowsEverything_MarksNearlyEveryCandidate()
    {
        var pool = Pool();
        var fit = ReadingCheckEngine.Fit(Answers(_ => true));

        var selection = ReadingCheckEngine.Select(fit, pool).Single(t => t.Level == ReadingCheckEngine.DefaultLevel);

        selection.Known.Count.Should().BeGreaterThan(pool.Count * 9 / 10);
    }

    [Fact]
    public void Select_ReaderWhoKnowsOnlyFrequentWords_KeepsRareOnesOut()
    {
        var pool = Pool();
        var fit = ReadingCheckEngine.Fit(Answers(c => c.Occurrences >= 150));

        var selection = ReadingCheckEngine.Select(fit, pool).Single(t => t.Level == ReadingCheckEngine.DefaultLevel);

        selection.Known.Should().NotContain(c => c.Occurrences < 30);
        selection.Known.Count.Should().BeLessThan(pool.Count(c => c.Occurrences >= 100));
    }

    [Fact]
    public void Select_BolderLevelsNeverMarkFewerWords()
    {
        var fit = ReadingCheckEngine.Fit(Answers(c => c.Occurrences >= 40 || c.Rank < 20000));

        var selection = ReadingCheckEngine.Select(fit, Pool()).OrderBy(t => t.Level).ToList();

        selection.Select(t => t.Known.Count).Should().BeInAscendingOrder();
    }

    [Fact]
    public void NextRound_AsksOneWordPerExposureBand()
    {
        var pool = Pool().Concat(Enumerable.Range(20_000, 500).Select(i => C(2 + i % 3, 60000 + i, i))).ToList();

        var round = ReadingCheckEngine.NextRound(pool, ReadingCheckEngine.ExpressionProbes, new Random(1));

        round.Select(c => ReadingCheckEngine.Band(c.Occurrences)).Should().BeEquivalentTo(Enumerable.Range(0, ReadingCheckEngine.RoundSize));
    }

    [Fact]
    public void NextRound_FillsAnEmptyBandFromItsNeighbour()
    {
        var pool = Enumerable.Range(1, 50).Select(i => C(6, 20000, i)).ToList();

        var round = ReadingCheckEngine.NextRound(pool, ReadingCheckEngine.ExpressionProbes, new Random(1));

        round.Should().HaveCount(ReadingCheckEngine.RoundSize).And.OnlyHaveUniqueItems(c => c.WordId);
    }

    [Fact]
    public void NextRound_IncludesAnExpressionUntilEnoughWereAsked()
    {
        var pool = Pool().Concat(Enumerable.Range(30_000, 5).Select(i => C(30, 20000, i) with { Expression = true })).ToList();

        ReadingCheckEngine.NextRound(pool, 0, new Random(1)).Should().ContainSingle(c => c.Expression);
        ReadingCheckEngine.NextRound(pool.Where(c => !c.Expression), 0, new Random(1)).Should().HaveCount(ReadingCheckEngine.RoundSize);
    }

    [Fact]
    public void ReviewPage_TakesWordsFromBothSidesOfTheMarkedEdge()
    {
        var pool = Pool();
        var fit = ReadingCheckEngine.Fit(Answers(c => c.Occurrences >= 60, 90));
        var marked = ReadingCheckEngine.Select(fit, pool).Single(t => t.Level == ReadingCheckEngine.DefaultLevel).Known
                                       .Select(c => c.WordId).ToHashSet();

        var page = ReadingCheckEngine.ReviewPage(fit, pool, ReadingCheckEngine.DefaultLevel);

        marked.Should().NotBeEmpty();
        page.Should().HaveCount(ReadingCheckEngine.ReviewPageSize).And.OnlyHaveUniqueItems(c => c.WordId);
        page.Count(c => marked.Contains(c.WordId)).Should().Be(ReadingCheckEngine.ReviewPageSize / 2);
    }

    [Fact]
    public void ReviewPage_SkipsWordsThatCannotBeAsked()
    {
        var pool = Pool().Select(c => c with { Probeable = c.WordId % 2 == 0 }).ToList();
        var fit = ReadingCheckEngine.Fit(Answers(c => c.Occurrences >= 60, 90));

        ReadingCheckEngine.ReviewPage(fit, pool, ReadingCheckEngine.DefaultLevel).Should().NotBeEmpty().And.OnlyContain(c => c.Probeable);
    }

    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(1, false, -0.35)]
    [InlineData(3, false, -0.55)]
    [InlineData(0, true, -0.25)]
    [InlineData(1, true, -0.6)]
    public void WordOffset_AddsKanjiAndVerbAdjustments(int uncommonKanji, bool verb, double expected)
        => ReadingCheckEngine.WordOffset(uncommonKanji, verb).Should().BeApproximately(expected, 1e-9);

    [Fact]
    public void Probability_UncommonKanjiLowersTheOddsAtTheSameExposure()
    {
        var fit = ReadingCheckEngine.Fit(Answers(c => c.Occurrences >= 40));
        var plain = C(30, 15000);
        var harder = plain with { Offset = ReadingCheckEngine.WordOffset(2, false) };

        fit.Probability(harder).Should().BeLessThan(fit.Probability(plain));
    }

    [Theory]
    [InlineData(-1, 100, false)]
    [InlineData(1000, 1010, true)]
    [InlineData(1000, 1100, false)]
    [InlineData(0, 0, true)]
    public void IsStable_NeedsTheMarkedSetSettled(int knownBefore, int known, bool expected)
        => ReadingCheckEngine.IsStable(knownBefore, known).Should().Be(expected);

    [Fact]
    public void RetentionEncounters_IsNullOutsideTheShownRange()
    {
        ReadingCheckEngine.RetentionEncounters(FitOf(1), 20000).Should().Be(89);
        ReadingCheckEngine.RetentionEncounters(FitOf(-6), 20000).Should().BeNull();
        ReadingCheckEngine.RetentionEncounters(FitOf(6), 20000).Should().BeNull();
        ReadingCheckEngine.RetentionEncounters(FitOf(1, b: 0.01), 20000).Should().BeNull();
    }
}
