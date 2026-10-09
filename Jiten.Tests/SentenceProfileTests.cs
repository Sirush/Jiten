using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core.Data;
using Jiten.Parser;

namespace Jiten.Tests;

public class SentenceProfileTests
{
    private static int K(int wordId) => ExampleSentenceTokens.WordKey(wordId, 0);

    private static SentenceProfile Profile(params int[][] sentences) =>
        SentenceProfileCodec.Decode(SentenceProfileCodec.Encode(sentences.Select(s => (IReadOnlyCollection<int>)s.Select(K).ToArray()).ToList()));

    private static Func<int, bool> Knows(params int[] wordIds)
    {
        var known = wordIds.Select(K).ToHashSet();
        return known.Contains;
    }

    [Fact]
    public void Codec_RoundTripsEverySentenceAsAKeySet()
    {
        var sentences = new List<IReadOnlyCollection<int>>
        {
            new[] { K(1000001), K(2000002), K(1500000) },
            new[] { K(1000001) },
            new[] { K(2999999), K(1000001), unchecked((int)0xFFFFFF00u) }
        };

        var decoded = SentenceProfileCodec.Decode(SentenceProfileCodec.Encode(sentences));

        decoded.SentenceCount.Should().Be(3);
        SentenceProfileCodec.Sentences(decoded).Select(s => s.OrderBy(x => x).ToArray())
                            .Should().BeEquivalentTo(sentences.Select(s => s.OrderBy(x => x).ToArray()), o => o.WithStrictOrdering());
    }

    [Fact]
    public void Codec_GivesTheMostFrequentWordTheFirstId()
    {
        var profile = Profile([5, 1], [1], [1, 7]);

        profile.Keys[0].Should().Be(K(1));
    }

    [Fact]
    public void Codec_EmptyProfileHasNoSentences()
    {
        var bytes = SentenceProfileCodec.Encode([]);

        SentenceProfileCodec.ReadSentenceCount(bytes).Should().Be(0);
        SentenceProfileCodec.Decode(bytes).SentenceCount.Should().Be(0);
    }

    [Fact]
    public void ReadSentenceCount_MatchesTheEncodedSentences()
    {
        var bytes = SentenceProfileCodec.Encode([new[] { K(1) }, new[] { K(2), K(3) }]);

        SentenceProfileCodec.ReadSentenceCount(bytes).Should().Be(2);
    }

    [Fact]
    public void CountReadable_SplitsSentencesByUnknownWords()
    {
        var bytes = SentenceProfileCodec.Encode([new[] { K(1), K(2) }, new[] { K(1), K(3) }, new[] { K(3), K(4) }, new[] { K(1) }]);

        var (total, readable, oneUnknown) = SentenceProfileCodec.CountReadable(bytes, Knows(1, 2));

        total.Should().Be(4);
        readable.Should().Be(2);
        oneUnknown.Should().Be(1);
    }

    [Fact]
    public void Sample_KeepsEverySentenceWhenUnderTheCap()
    {
        var bytes = SentenceProfileCodec.Sample([Profile([1], [2]), Profile([3])], 400);

        SentenceProfileCodec.ReadSentenceCount(bytes).Should().Be(3);
    }

    [Fact]
    public void Sample_DrawsFromEachPartInProportionToItsSize()
    {
        var big = Profile(Enumerable.Range(0, 300).Select(_ => new[] { 1 }).ToArray());
        var small = Profile(Enumerable.Range(0, 100).Select(_ => new[] { 2 }).ToArray());

        var sample = SentenceProfileCodec.Decode(SentenceProfileCodec.Sample([big, small], 40));

        var fromBig = SentenceProfileCodec.Sentences(sample).Count(s => s[0] == K(1));
        sample.SentenceCount.Should().Be(40);
        fromBig.Should().Be(30);
    }

    [Fact]
    public void Calculator_CountsSentencesByDistinctUnknownWords()
    {
        var stats = SentenceStatsCalculator.Compute([Profile([1, 2], [1, 3], [3, 4], [3, 4, 5, 6])], Knows(1, 2));

        stats.Total.Should().Be(4);
        stats.ByUnknown.Should().Equal(1, 1, 1, 1);
        stats.UnknownWords.Should().Be(4);
    }

    [Fact]
    public void Calculator_LearnsTheWordThatUnlocksTheMostSentencesFirst()
    {
        // 3 stands alone in two sentences, 4 in one; learning 3 then turns [3,5] into an i+1 for 5.
        var stats = SentenceStatsCalculator.Compute([Profile([1, 3], [2, 3], [1, 4], [3, 5], [5, 1])], Knows(1, 2));

        stats.LearnNext[0].WordKey.Should().Be(K(3));
        stats.LearnNext[0].Unlocked.Should().Be(2);
        stats.LearnNext[1].WordKey.Should().Be(K(5));
        stats.LearnNext[1].Unlocked.Should().Be(2);
        stats.LearnNext[1].ReadableAfter.Should().Be(4);
    }

    [Fact]
    public void Calculator_UnboundedRun_RanksEveryUnknownWordAndKeepsTheBoundedPrefix()
    {
        var sentences = Enumerable.Range(2, 2500).Select(w => new[] { 1, w, w + 1 }).ToArray();
        var profile = Profile(sentences);

        var bounded = SentenceStatsCalculator.Compute([profile], Knows(1), learnNextCount: 2000, maxSteps: 2000);
        var full = SentenceStatsCalculator.Compute([profile], Knows(1), learnNextCount: int.MaxValue, maxSteps: int.MaxValue);

        full.LearnNext.Should().HaveCount(2501);
        full.LearnNext.Take(2000).Select(l => l.WordKey).Should().Equal(bounded.LearnNext.Select(l => l.WordKey));
    }

    [Fact]
    public void Calculator_ProjectionStartsAtTheReadableCountAndEndsWithEverything()
    {
        var stats = SentenceStatsCalculator.Compute([Profile([1], [2, 3], [3, 4], [5])], Knows(1));

        stats.Projection[0].Should().Be(new SentenceProjectionPoint(0, 1, 1));
        stats.Projection[^1].Greedy.Should().Be(4);
        stats.Projection[^1].ByFrequency.Should().Be(4);
    }

    [Fact]
    public void Calculator_MilestoneIsTheFirstStepReachingThePercentage()
    {
        var stats = SentenceStatsCalculator.Compute([Profile([1], [2], [3], [4])], Knows(1));

        stats.Milestones.Single(m => m.Percent == 50).Words.Should().Be(1);
        stats.Milestones.Single(m => m.Percent == 90).Words.Should().Be(3);
    }

    [Fact]
    public void Calculator_SegmentsFollowPartsWhenThereAreSeveral()
    {
        var stats = SentenceStatsCalculator.Compute([Profile([1], [2]), Profile([2]), Profile([1])], Knows(1));

        stats.Segments.Select(s => (s.FirstPart, s.Total, s.Readable)).Should().Equal((0, 2, 1), (1, 1, 0), (2, 1, 1));
    }

    [Fact]
    public void Calculator_SegmentsCountOneAndTwoUnknownSeparately()
    {
        var stats = SentenceStatsCalculator.Compute([Profile([1], [2], [2, 3], [2, 3, 4]), Profile([1])], Knows(1));

        stats.Segments.Select(s => (s.Readable, s.OneUnknown, s.TwoUnknown)).Should().Equal((1, 1, 1), (1, 0, 0));
    }

    [Fact]
    public void Calculator_GroupsPartsWhenThereAreMoreThanTheSegmentCap()
    {
        var parts = Enumerable.Range(0, 10).Select(_ => Profile([1])).ToList();

        var stats = SentenceStatsCalculator.Compute(parts, Knows(1), maxSegments: 4);

        stats.Segments.Should().HaveCount(4);
        stats.Segments.Sum(s => s.Total).Should().Be(10);
        stats.Segments[0].FirstPart.Should().Be(0);
        stats.Segments[^1].LastPart.Should().Be(9);
    }

    [Fact]
    public void Calculator_SlicesASingleText()
    {
        var stats = SentenceStatsCalculator.Compute([Profile(Enumerable.Range(0, 20).Select(i => new[] { i + 1 }).ToArray())], Knows(1, 2),
                                                    textSlices: 10);

        stats.Segments.Should().HaveCount(10);
        stats.Segments[0].Readable.Should().Be(2);
        stats.Segments.Skip(1).Should().OnlyContain(s => s.Readable == 0 && s.Total == 2);
    }

    [Fact]
    public void Builder_KeepsOnlyContentWordsThatBecameDeckWords()
    {
        var sentence = new SentenceInfo("猫が走った。");
        sentence.Words.Add((new WordInfo { Text = "猫", PartOfSpeech = PartOfSpeech.Noun, KeptForm = (1000001, 0) }, 0, 1));
        sentence.Words.Add((new WordInfo { Text = "が", PartOfSpeech = PartOfSpeech.Particle, KeptForm = (2000001, 0) }, 1, 1));
        sentence.Words.Add((new WordInfo { Text = "走った", PartOfSpeech = PartOfSpeech.Verb, KeptForm = (1000002, 0) }, 2, 3));
        sentence.Words.Add((new WordInfo { Text = "太郎", PartOfSpeech = PartOfSpeech.Name, KeptForm = (5000001, 0) }, 5, 2));
        sentence.Words.Add((new WordInfo { Text = "謎", PartOfSpeech = PartOfSpeech.Noun, KeptForm = (1000003, 0) }, 7, 1));
        var interjection = new SentenceInfo("ああ。");
        interjection.Words.Add((new WordInfo { Text = "ああ", PartOfSpeech = PartOfSpeech.Particle, KeptForm = (2000002, 0) }, 0, 2));

        var deckWords = new[] { 1000001, 2000001, 1000002, 5000001, 2000002 }.Select(id => new DeckWord { WordId = id }).ToList();
        var profile = SentenceProfileCodec.Decode(SentenceProfileBuilder.Build([sentence, interjection], deckWords));

        profile.SentenceCount.Should().Be(1);
        SentenceProfileCodec.Sentences(profile).Single().Should().BeEquivalentTo([K(1000001), K(1000002)]);
    }

    [Fact]
    public void Builder_SplitsSentencesAtLineBreaks()
    {
        // 猫が / 走った / 犬 on three lines with no sentence ender between them.
        var sentence = new SentenceInfo("猫が走った犬");
        sentence.Words.Add((new WordInfo { Text = "猫", PartOfSpeech = PartOfSpeech.Noun, KeptForm = (1000001, 0), StartOffset = 0 }, 0, 1));
        sentence.Words.Add((new WordInfo { Text = "が", PartOfSpeech = PartOfSpeech.Particle, KeptForm = (2000001, 0), StartOffset = 1 }, 1, 1));
        sentence.Words.Add((new WordInfo { Text = "走った", PartOfSpeech = PartOfSpeech.Verb, KeptForm = (1000002, 0), StartOffset = 2 }, 2, 3));
        sentence.Words.Add((new WordInfo { Text = "犬", PartOfSpeech = PartOfSpeech.Noun, KeptForm = (1000003, 0), StartOffset = 5 }, 5, 1));
        var deckWords = new[] { 1000001, 2000001, 1000002, 1000003 }.Select(id => new DeckWord { WordId = id }).ToList();

        var whole = SentenceProfileCodec.Decode(SentenceProfileBuilder.Build([sentence], deckWords));
        var split = SentenceProfileCodec.Decode(SentenceProfileBuilder.Build([sentence], deckWords, [2, 5]));

        whole.SentenceCount.Should().Be(1);
        SentenceProfileCodec.Sentences(split).Should().BeEquivalentTo(new[] { new[] { K(1000001) }, [K(1000002)], [K(1000003)] },
                                                                        o => o.WithStrictOrdering());
    }

    [Fact]
    public void Writer_MarksDecksWithoutASampleAsNoData()
    {
        var chunks = SentenceCoverageWriter.BuildChunks([(5, 2500, 1200)], 1030, "user", DateTime.UtcNow);

        chunks.Should().HaveCount(4);
        var readable = chunks.Single(c => c.Metric == (short)UserCoverageMetric.ReadableSentences && c.ChunkIndex == 0);
        readable.Values[5].Should().Be(2500);
        readable.Values[6].Should().Be(SentenceCoverageWriter.NoData);
    }
}
