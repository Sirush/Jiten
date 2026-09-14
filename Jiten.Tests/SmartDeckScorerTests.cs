using FluentAssertions;
using Jiten.Core.Services.SmartDeck;

namespace Jiten.Tests;

public class SmartDeckScorerTests
{
    private static SmartDeckPart Part(int deckId, double weight, params (int WordId, int Occ)[] words)
        => new(deckId, weight, words.Select(w => new SmartDeckWordOccurrence(w.WordId, 0, w.Occ)).ToList());

    private static List<SmartDeckScoredWord> Score(params SmartDeckTitleInput[] titles)
        => SmartDeckScorer.Score(titles, _ => false, _ => int.MaxValue, SmartDeckConstants.MaxWords);

    [Fact]
    public void CoverageTarget_StopsAtTheShareOfOccurrences_MostFrequentFirst()
    {
        // 60 + 25 = 85% of 100; the 10 word pushes past 95 and is the last one in, the two 2s and the 1 stay out.
        var part = Part(1, 1.0, (1, 60), (2, 2), (3, 25), (4, 10), (5, 2), (6, 1));

        var kept = SmartDeckScorer.CoverageSlice(part.Words, _ => false, 95).Select(w => w.WordId).ToList();

        kept.Should().Equal(1, 3, 4);
    }

    [Fact]
    public void CoverageTarget_CountsKnownWordsAsCovered()
    {
        var part = Part(1, 1.0, (1, 90), (2, 5), (3, 5));

        var kept = SmartDeckScorer.CoverageSlice(part.Words, key => (key >> 8) == 1, 95).Select(w => w.WordId).ToList();

        kept.Should().Equal([2], "the known word already covers 90%, one more 5 reaches 95");
    }

    [Fact]
    public void CoverageTarget_AtHundred_KeepsEveryUnknownWord()
    {
        var part = Part(1, 1.0, (1, 90), (2, 1), (3, 1));

        SmartDeckScorer.CoverageSlice(part.Words, key => (key >> 8) == 1, 100).Should().HaveCount(2);
    }

    [Fact]
    public void CardedWords_StayIn_AndCountAsCovered_WhateverTheTarget()
    {
        var part = Part(1, 1.0, (1, 90), (2, 5), (3, 5));

        var kept = SmartDeckScorer.CoverageSlice(part.Words, _ => false, 95, hasCard: key => (key >> 8) == 1).Select(w => w.WordId).ToList();

        kept.Should().BeEquivalentTo([1, 2], "the carded word is kept for its reviews and covers 90%, one unknown 5 reaches 95");
    }

    [Fact]
    public void CoverageTarget_AppliesPerPart_SoAWindowUnitCanAddATailWord()
    {
        var ranked = SmartDeckScorer.Score(
        [
            new SmartDeckTitleInput(1, 1.0,
            [
                Part(1, SmartDeckConstants.WholeTitleWeight, (100, 97), (200, 2), (300, 1)),
                Part(11, SmartDeckConstants.WindowWeight - SmartDeckConstants.WholeTitleWeight, (300, 1)),
            ]),
        ], _ => false, _ => int.MaxValue, SmartDeckConstants.MaxWords, 95);

        ranked.Select(w => w.WordId).Should().BeEquivalentTo([100, 300], "200 is past the whole-title target and 300 is kept by the window unit");
    }

    [Fact]
    public void WordSharedAcrossTitles_BeatsWordInOneTitle()
    {
        var ranked = Score(
            new SmartDeckTitleInput(1, 1.0, [Part(1, 1.0, (100, 3), (200, 3))]),
            new SmartDeckTitleInput(2, 1.0, [Part(2, 1.0, (100, 3))]));

        ranked[0].WordId.Should().Be(100);
        ranked[0].Score.Should().BeApproximately(2 * Math.Log2(4), 1e-9);
        ranked[1].WordId.Should().Be(200);
    }

    [Fact]
    public void WindowUnit_OutweighsWholeTitleOccurrences()
    {
        var ranked = Score(new SmartDeckTitleInput(1, 1.0,
        [
            Part(1, SmartDeckConstants.WholeTitleWeight, (100, 20), (200, 2)),
            Part(11, SmartDeckConstants.WindowWeight - SmartDeckConstants.WholeTitleWeight, (200, 2)),
        ]));

        ranked.Select(w => w.WordId).Should().Equal(200, 100);
    }

    [Fact]
    public void LogDamping_KeepsOneLongTitleFromSwamping()
    {
        var ranked = Score(
            new SmartDeckTitleInput(1, 1.0, [Part(1, 1.0, (100, 1000))]),
            new SmartDeckTitleInput(2, 1.0, [Part(2, 1.0, (200, 5))]),
            new SmartDeckTitleInput(3, 1.0, [Part(3, 1.0, (200, 5))]),
            new SmartDeckTitleInput(4, 1.0, [Part(4, 1.0, (200, 5))]),
            new SmartDeckTitleInput(5, 1.0, [Part(5, 1.0, (200, 5))]));

        ranked[0].WordId.Should().Be(200, "four titles with 5 occurrences each beat one title with 1000");
    }

    [Fact]
    public void RecencyWeight_ScalesTitleContribution()
    {
        var stale = SmartDeckConstants.TitleWeight(false, false, ageDays: 28, halfLifeDays: 14);
        var ranked = Score(
            new SmartDeckTitleInput(1, stale, [Part(1, 1.0, (100, 10))]),
            new SmartDeckTitleInput(2, 1.0, [Part(2, 1.0, (200, 10))]));

        stale.Should().BeApproximately(0.25, 1e-9);
        ranked[0].WordId.Should().Be(200);
        ranked[1].Score.Should().BeApproximately(ranked[0].Score * 0.25, 1e-9);
    }

    [Fact]
    public void RecencyWeight_FloorsAtMinimum()
    {
        SmartDeckConstants.TitleWeight(false, false, ageDays: 365, halfLifeDays: 7).Should().Be(SmartDeckConstants.MinRecencyWeight);
        SmartDeckConstants.TitleWeight(true, false, ageDays: 365, halfLifeDays: 7).Should().Be(SmartDeckConstants.PinnedWeight);
        SmartDeckConstants.TitleWeight(false, true, ageDays: 0, halfLifeDays: 14).Should().Be(SmartDeckConstants.PlanningWeight);
        SmartDeckConstants.TitleWeight(false, true, ageDays: 300, halfLifeDays: 14).Should().Be(SmartDeckConstants.PlanningWeight);
    }

    [Fact]
    public void RecencyWeight_NeverFadesKeepsFullWeight()
    {
        SmartDeckConstants.TitleWeight(false, false, ageDays: 900, halfLifeDays: SmartDeckConstants.NeverFadesHalfLife).Should().Be(1.0);
    }

    [Fact]
    public void ExcludedKeys_NeverAppear()
    {
        var excludedKey = SmartDeckScorer.EncodeKey(100, 0);
        var ranked = SmartDeckScorer.Score(
            [new SmartDeckTitleInput(1, 1.0, [Part(1, 1.0, (100, 50), (200, 1))])],
            key => key == excludedKey, _ => 0, SmartDeckConstants.MaxWords);

        ranked.Select(w => w.WordId).Should().Equal(200);
    }

    [Fact]
    public void Ties_BreakOnFrequencyRankThenKey()
    {
        var ranks = new Dictionary<int, int> { [300] = 10, [100] = 5, [200] = 5 };
        var ranked = SmartDeckScorer.Score(
            [new SmartDeckTitleInput(1, 1.0, [Part(1, 1.0, (300, 4), (200, 4), (100, 4))])],
            _ => false, key => ranks[(int)(key >> 8)], SmartDeckConstants.MaxWords);

        ranked.Select(w => w.WordId).Should().Equal(100, 200, 300);
    }

    [Fact]
    public void Cap_TruncatesTail()
    {
        var words = Enumerable.Range(1, 50).Select(i => (i, 51 - i)).ToArray();
        var ranked = SmartDeckScorer.Score([new SmartDeckTitleInput(1, 1.0, [Part(1, 1.0, words)])], _ => false, _ => 0, cap: 10);

        ranked.Should().HaveCount(10);
        ranked.Select(w => w.WordId).Should().Equal(Enumerable.Range(1, 10));
    }
}
