using FluentAssertions;
using Jiten.Api.Services;
using Xunit;

namespace Jiten.Tests;

public class GlossSearchScorerTests
{
    private static GlossSenseCandidate Sense(int wordId, int senseIndex, params string[] meanings) =>
        new(wordId, senseIndex, meanings.ToList(), [], []);

    [Fact]
    public void ExactGlossBeatsMoreFrequentContainingGloss()
    {
        var korosu = Sense(1, 0, "to kill", "to slay", "to murder");
        var kimeru = Sense(2, 3, "to dress to kill", "to decide");
        var ranks = new Dictionary<int, int> { [1] = 900, [2] = 200 };

        var hits = GlossSearchScorer.Rank("to kill", [kimeru, korosu], ranks);

        hits.Select(h => h.WordId).Should().Equal(1, 2);
    }

    [Fact]
    public void BareVerbQueryStillReachesExactTier()
    {
        var korosu = Sense(1, 0, "to kill");
        var ranks = new Dictionary<int, int> { [1] = 900 };

        var bare = GlossSearchScorer.Rank("kill", [korosu], ranks).Single().Score;
        var explicitVerb = GlossSearchScorer.Rank("to kill", [korosu], ranks).Single().Score;
        bare.Should().BeGreaterThan(explicitVerb - 50).And.BeLessThan(explicitVerb);
    }

    [Fact]
    public void ParentheticalTextMatchesWhenTypedOut()
    {
        var matomeru = Sense(1, 0, "to collect", "to put (it all) together", "to integrate");
        var ranks = new Dictionary<int, int> { [1] = 1500 };

        var typedOut = GlossSearchScorer.Rank("to put it all together", [matomeru], ranks);
        var stripped = GlossSearchScorer.Rank("put together", [matomeru], ranks);

        var control = GlossSearchScorer.Rank("to collect", [matomeru], ranks).Single().Score;
        typedOut.Should().ContainSingle().Which.Score.Should().BeGreaterThan(control - 100);
        stripped.Should().ContainSingle().Which.Score.Should().BeGreaterThan(control - 100);
    }

    [Fact]
    public void NonContiguousTermsStillMatchViaCoverage()
    {
        var darake = Sense(1, 1, "covered all over with (blood, mud, etc.)");
        var ranks = new Dictionary<int, int> { [1] = 3000 };

        var hits = GlossSearchScorer.Rank("covered with", [darake], ranks);

        hits.Should().ContainSingle().Which.Score.Should().BePositive();
    }

    [Fact]
    public void ReturnedMeaningsComeFromTheBestSense()
    {
        var word = new GlossSenseCandidate(1, 2, ["to settle", "to decide"], [], []);
        var other = new GlossSenseCandidate(1, 0, ["to fix", "to determine"], [], []);

        var hit = GlossSearchScorer.Rank("decide", [other, word], new Dictionary<int, int>()).Single();

        hit.SenseIndex.Should().Be(2);
        hit.Meanings.Should().Contain("to decide");
    }

    [Fact]
    public void ArchaicSenseRanksBelowPlainSense()
    {
        var archaic = new GlossSenseCandidate(1, 0, ["to kill"], ["arch"], []);
        var plain = new GlossSenseCandidate(2, 0, ["to kill"], [], []);
        var ranks = new Dictionary<int, int> { [1] = 100, [2] = 5000 };

        GlossSearchScorer.Rank("kill", [archaic, plain], ranks).First().WordId.Should().Be(2);
    }

    [Fact]
    public void FrequencyBreaksTiesWithinATier()
    {
        var rare = Sense(1, 0, "to kill");
        var common = Sense(2, 0, "to kill");
        var ranks = new Dictionary<int, int> { [1] = 50000, [2] = 300 };

        GlossSearchScorer.Rank("kill", [rare, common], ranks).First().WordId.Should().Be(2);
    }

    [Fact]
    public void NestedParentheticalStillCountsAsExact()
    {
        var inu = new GlossSenseCandidate(1, 0, ["dog (Canis (lupus) familiaris)", "canine"], [], [], IsCommon: true);
        var wanko = Sense(2, 0, "dog", "doggy", "bow-wow");
        var ranks = new Dictionary<int, int> { [1] = 1333, [2] = 127413 };

        GlossSearchScorer.Rank("dog", [wanko, inu], ranks).Select(h => h.WordId).Should().Equal(1, 2);
    }

    [Fact]
    public void NounQueryPrefersNounGlossOverVerbGloss()
    {
        var wanko = Sense(1, 0, "dog", "doggy");
        var tsuitemawaru = Sense(2, 0, "to follow (someone) around", "to cling (to)", "to dog");
        var ranks = new Dictionary<int, int> { [1] = 127413, [2] = 28676 };

        GlossSearchScorer.Rank("dog", [tsuitemawaru, wanko], ranks).First().WordId.Should().Be(1);
        GlossSearchScorer.Rank("to dog", [tsuitemawaru, wanko], ranks).First().WordId.Should().Be(2);
    }

    [Fact]
    public void UnrelatedCandidateIsDropped()
    {
        var hits = GlossSearchScorer.Rank("kill", [Sense(1, 0, "to eat")], new Dictionary<int, int>());
        hits.Should().BeEmpty();
    }
}
