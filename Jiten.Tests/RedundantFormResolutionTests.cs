using FluentAssertions;
using Jiten.Api.Helpers;
using Jiten.Api.Services;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Xunit;

namespace Jiten.Tests;

public class RedundantFormResolutionTests
{
    private const int WordId = 1;

    private static JmDictWordForm Form(short ri, string text, JmDictFormType type, string? ruby = null)
        => new() { WordId = WordId, ReadingIndex = ri, Text = text, FormType = type, RubyText = ruby ?? text };

    private static FsrsCard Card(byte ri, FsrsState state, long cardId = 0)
        => new("user", WordId, ri, cardId: cardId == 0 ? ri + 1 : cardId, state: state);

    private sealed class StubCache : IWordFormSiblingCache
    {
        private readonly WordFormInfo _info;

        public StubCache(IReadOnlyList<JmDictWordForm> forms)
        {
            var edges = RedundancyGraphHelper.BuildEdges(forms);
            _info = new WordFormInfo
            {
                RedundantBySource = edges.GroupBy(e => e.Source)
                                         .ToDictionary(g => g.Key, g => g.Select(e => e.Target).Distinct().ToArray()),
                SourcesByRedundant = edges.GroupBy(e => e.Target)
                                          .ToDictionary(g => g.Key, g => g.Select(e => e.Source).Distinct().ToArray())
            };
        }

        public byte[]? GetKanaIndexesForKanji(int wordId, byte readingIndex) => _info.RedundantBySource.GetValueOrDefault(readingIndex);
        public byte[]? GetKanjiIndexesForKana(int wordId, byte readingIndex) => _info.SourcesByRedundant.GetValueOrDefault(readingIndex);
        public WordFormInfo? GetWordFormInfo(int wordId) => _info;
        public void Reload() { }
    }

    private static StubCache TenohiraCache() => new(new List<JmDictWordForm>
    {
        Form(0, "手のひら", JmDictFormType.KanjiForm, "手[て]のひら"),
        Form(1, "掌", JmDictFormType.KanjiForm, "掌[てのひら]"),
        Form(2, "手の平", JmDictFormType.KanjiForm, "手[て]の平[ひら]"),
        Form(3, "てのひら", JmDictFormType.KanaForm),
    });

    [Fact]
    public void MasteredKanjiForm_CoversItsKanaDegradation()
    {
        var cards = new List<FsrsCard> { Card(2, FsrsState.Mastered), Card(0, FsrsState.Review) };

        var redundant = WordFormHelper.FindRedundantForms(TenohiraCache(), WordId, cards);

        redundant.Should().ContainSingle()
                 .Which.Should().Be(new RedundantFormPair(0, 2));
    }

    [Fact]
    public void CoveredForm_IsNotDroppedWhenItsDominatorIsNotMastered()
    {
        var cards = new List<FsrsCard> { Card(2, FsrsState.Review), Card(0, FsrsState.Review) };

        WordFormHelper.FindRedundantForms(TenohiraCache(), WordId, cards).Should().BeEmpty();
    }

    [Fact]
    public void DominanceIsOneWay_MasteredKanaFormDoesNotCoverTheKanjiForm()
    {
        var cards = new List<FsrsCard> { Card(0, FsrsState.Mastered), Card(2, FsrsState.Review) };

        WordFormHelper.FindRedundantForms(TenohiraCache(), WordId, cards).Should().BeEmpty();
    }

    [Fact]
    public void SeparateDominanceComponents_BothSurvive()
    {
        var cards = new List<FsrsCard> { Card(1, FsrsState.Mastered), Card(2, FsrsState.Mastered) };

        WordFormHelper.FindRedundantForms(TenohiraCache(), WordId, cards).Should().BeEmpty();
    }

    [Fact]
    public void ChainedDominance_LeavesEveryRemovedFormCoveredByASurvivor()
    {
        var cards = new List<FsrsCard>
        {
            Card(2, FsrsState.Mastered), // 手の平
            Card(0, FsrsState.Mastered), // 手のひら, covered by 手の平
            Card(3, FsrsState.Review),   // てのひら, covered by both
        };

        var redundant = WordFormHelper.FindRedundantForms(TenohiraCache(), WordId, cards);

        redundant.Select(r => r.ReadingIndex).Should().BeEquivalentTo(new byte[] { 0, 3 });
        redundant.Should().OnlyContain(r => r.CoveringReadingIndex == 2);
    }

    [Fact]
    public void BlacklistedForm_IsNeitherRemovedNorUsedAsCover()
    {
        var blacklistedIsKept = WordFormHelper.FindRedundantForms(
            TenohiraCache(), WordId, new List<FsrsCard> { Card(2, FsrsState.Mastered), Card(0, FsrsState.Blacklisted) });
        blacklistedIsKept.Should().BeEmpty();

        var blacklistDoesNotCover = WordFormHelper.FindRedundantForms(
            TenohiraCache(), WordId, new List<FsrsCard> { Card(2, FsrsState.Blacklisted), Card(0, FsrsState.Review) });
        blacklistDoesNotCover.Should().BeEmpty();
    }

    [Fact]
    public void MutuallyRedundantScriptVariants_KeepExactlyOne()
    {
        var cache = new StubCache(new List<JmDictWordForm>
        {
            Form(0, "おちつける", JmDictFormType.KanaForm),
            Form(1, "オチツケル", JmDictFormType.KanaForm),
        });
        var cards = new List<FsrsCard> { Card(0, FsrsState.Mastered), Card(1, FsrsState.Mastered) };

        var redundant = WordFormHelper.FindRedundantForms(cache, WordId, cards);

        redundant.Should().ContainSingle();
        redundant[0].CoveringReadingIndex.Should().NotBe(redundant[0].ReadingIndex);
    }

    [Fact]
    public void MutualTie_KeepsTheFormCarryingTheReviewHistory()
    {
        var cache = new StubCache(new List<JmDictWordForm>
        {
            Form(0, "おちつける", JmDictFormType.KanaForm),
            Form(1, "オチツケル", JmDictFormType.KanaForm),
        });
        var cards = new List<FsrsCard> { Card(0, FsrsState.Mastered, cardId: 10), Card(1, FsrsState.Mastered, cardId: 11) };
        var reviewCounts = new Dictionary<long, int> { [10] = 0, [11] = 42 };

        var redundant = WordFormHelper.FindRedundantForms(cache, WordId, cards, reviewCounts);

        redundant.Should().ContainSingle()
                 .Which.Should().Be(new RedundantFormPair(0, 1));
    }

    [Fact]
    public void EveryRemovedForm_HasASurvivingCover()
    {
        var cache = TenohiraCache();
        var cards = new List<FsrsCard>
        {
            Card(0, FsrsState.Mastered), Card(1, FsrsState.Mastered),
            Card(2, FsrsState.Mastered), Card(3, FsrsState.Review),
        };

        var redundant = WordFormHelper.FindRedundantForms(cache, WordId, cards);
        var removed = redundant.Select(r => r.ReadingIndex).ToHashSet();

        redundant.Should().OnlyContain(r => !removed.Contains(r.CoveringReadingIndex));
    }
}
