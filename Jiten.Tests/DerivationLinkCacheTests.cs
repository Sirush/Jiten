using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jiten.Tests;

public class DerivationLinkCacheTests : IDisposable
{
    // 強い kanji[0] / kana[1], 強さ kanji[0] / kana[1], 強がる kanji[0] / kana[1], 深み kanji[0].
    private const int Tsuyoi = 100;
    private const int Tsuyosa = 101;
    private const int Tsuyogaru = 102;
    private const int Fukami = 103;

    private static readonly IReadOnlySet<DerivationCategory> AllCategories =
        new HashSet<DerivationCategory> { DerivationCategory.SaNominal, DerivationCategory.Garu, DerivationCategory.MiNominal };

    private readonly SqliteConnection _connection;

    public DerivationLinkCacheTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<JitenDbContext>
    {
        public JitenDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<JitenDbContext>().UseSqlite(connection).Options);
    }

    private static JmDictWordDerivation Link(int baseWordId, byte baseIndex, int derivedWordId, byte derivedIndex,
                                             DerivationCategory category,
                                             DerivationDirection direction = DerivationDirection.Bidirectional)
        => new()
        {
            BaseWordId = baseWordId, BaseReadingIndex = baseIndex,
            DerivedWordId = derivedWordId, DerivedReadingIndex = derivedIndex,
            Category = category, Direction = direction, Source = DerivationSource.RuleGenerated
        };

    /// <summary>Every test word gets reading index 0 as its kanji form and index 1 as its kana form, which is
    /// what the cache reads to decide whether a link may be walked backwards.</summary>
    private DerivationLinkCache BuildCache(params JmDictWordDerivation[] links)
    {
        var factory = new Factory(_connection);
        using (var context = factory.CreateDbContext())
        {
            context.Database.EnsureCreated();
            context.WordDerivations.RemoveRange(context.WordDerivations);
            context.WordForms.RemoveRange(context.WordForms);
            context.SaveChanges();

            foreach (var wordId in new[] { Tsuyoi, Tsuyosa, Tsuyogaru, Fukami })
            {
                if (!context.JMDictWords.Any(w => w.WordId == wordId))
                    context.JMDictWords.Add(new JmDictWord { WordId = wordId, PartsOfSpeech = ["n"] });

                context.WordForms.Add(new JmDictWordForm
                {
                    WordId = wordId, ReadingIndex = 0, Text = $"K{wordId}", RubyText = $"K{wordId}",
                    FormType = JmDictFormType.KanjiForm
                });
                context.WordForms.Add(new JmDictWordForm
                {
                    WordId = wordId, ReadingIndex = 1, Text = $"k{wordId}", RubyText = $"k{wordId}",
                    FormType = JmDictFormType.KanaForm
                });
            }

            context.WordDerivations.AddRange(links);
            context.SaveChanges();
        }

        return new DerivationLinkCache(factory, NullLogger<DerivationLinkCache>.Instance);
    }

    /// <summary>Form closure as the builder emits it: kanji base covers both scripts, kana base only kana.</summary>
    private static JmDictWordDerivation[] FormClosure(int baseWordId, int derivedWordId, DerivationCategory category,
                                                      DerivationDirection direction = DerivationDirection.Bidirectional)
        =>
        [
            Link(baseWordId, 0, derivedWordId, 0, category, direction),
            Link(baseWordId, 0, derivedWordId, 1, category, direction),
            Link(baseWordId, 1, derivedWordId, 1, category, direction)
        ];

    [Fact]
    public void BaseCoversDerived_InBothScripts()
    {
        var cache = BuildCache(FormClosure(Tsuyoi, Tsuyosa, DerivationCategory.SaNominal));

        var covering = cache.GetCoveringKeys(Tsuyosa, 0, AllCategories);

        covering.Should().ContainSingle()
                .Which.Should().Be(new DerivationCover(Tsuyoi, 0, DerivationCategory.SaNominal));
    }

    [Fact]
    public void KanaBaseForm_NeverCoversTheKanjiDerivedForm()
    {
        var cache = BuildCache(FormClosure(Tsuyoi, Tsuyosa, DerivationCategory.SaNominal));

        var coveredByKana = cache.GetCoveredKeys(Tsuyoi, 1, AllCategories);
        var coveredByKanji = cache.GetCoveredKeys(Tsuyoi, 0, AllCategories);

        coveredByKana.Select(c => (c.WordId, c.ReadingIndex))
                     .Should().BeEquivalentTo([(Tsuyosa, (byte)1)]);
        coveredByKanji.Select(c => (c.WordId, c.ReadingIndex))
                      .Should().BeEquivalentTo([(Tsuyosa, (byte)0), (Tsuyosa, (byte)1), (Tsuyoi, (byte)1)]);
    }

    [Fact]
    public void KanaDerivedForm_DoesNotReachTheKanjiFormThroughItsBase()
    {
        var cache = BuildCache(FormClosure(Tsuyoi, Tsuyosa, DerivationCategory.SaNominal));

        // つよさ reaches つよい, but neither 強い nor 強さ in kanji.
        cache.GetCoveredKeys(Tsuyosa, 1, AllCategories)
             .Select(c => (c.WordId, c.ReadingIndex))
             .Should().BeEquivalentTo([(Tsuyoi, (byte)1)]);
    }

    [Fact]
    public void BidirectionalLink_LetsTheDerivedFormCoverItsBase()
    {
        var cache = BuildCache(FormClosure(Tsuyoi, Tsuyosa, DerivationCategory.SaNominal));

        cache.GetCoveringKeys(Tsuyoi, 0, AllCategories)
             .Should().ContainSingle()
             .Which.WordId.Should().Be(Tsuyosa);
    }

    [Fact]
    public void BaseToDerivedOnlyLink_DoesNotConductBackwards()
    {
        var cache = BuildCache(FormClosure(Tsuyoi, Tsuyosa, DerivationCategory.SaNominal,
                                           DerivationDirection.BaseToDerivedOnly));

        cache.GetCoveringKeys(Tsuyosa, 0, AllCategories).Should().ContainSingle();
        cache.GetCoveringKeys(Tsuyoi, 0, AllCategories).Should().BeEmpty();
    }

    [Fact]
    public void DisabledCategory_SeversTheLink()
    {
        var cache = BuildCache(FormClosure(Tsuyoi, Tsuyosa, DerivationCategory.SaNominal));

        cache.GetCoveringKeys(Tsuyosa, 0, new HashSet<DerivationCategory> { DerivationCategory.Garu })
             .Should().BeEmpty();
    }

    [Fact]
    public void EmptyCategorySet_ResolvesNothing()
    {
        var cache = BuildCache(FormClosure(Tsuyoi, Tsuyosa, DerivationCategory.SaNominal));

        cache.GetCoveringKeys(Tsuyosa, 0, new HashSet<DerivationCategory>()).Should().BeEmpty();
    }

    [Fact]
    public void CoverageIsTransitiveThroughTheBaseWord()
    {
        var cache = BuildCache([
            ..FormClosure(Tsuyoi, Tsuyosa, DerivationCategory.SaNominal),
            ..FormClosure(Tsuyoi, Tsuyogaru, DerivationCategory.Garu)
        ]);

        cache.GetCoveringKeys(Tsuyosa, 0, AllCategories)
             .Select(c => c.WordId)
             .Should().BeEquivalentTo(new[] { Tsuyoi, Tsuyogaru });
    }

    [Fact]
    public void TransitiveHopStopsWhenTheLinkingCategoryIsOff()
    {
        var cache = BuildCache([
            ..FormClosure(Tsuyoi, Tsuyosa, DerivationCategory.SaNominal),
            ..FormClosure(Tsuyoi, Tsuyogaru, DerivationCategory.Garu)
        ]);

        cache.GetCoveringKeys(Tsuyosa, 0, new HashSet<DerivationCategory> { DerivationCategory.SaNominal })
             .Select(c => c.WordId)
             .Should().BeEquivalentTo(new[] { Tsuyoi });
    }

    [Fact]
    public void ReportedCategoryIsTheOneTouchingTheCoveredForm()
    {
        var cache = BuildCache([
            ..FormClosure(Tsuyoi, Tsuyosa, DerivationCategory.SaNominal),
            ..FormClosure(Tsuyoi, Tsuyogaru, DerivationCategory.Garu)
        ]);

        var covers = cache.GetCoveringKeys(Tsuyosa, 0, AllCategories);

        covers.Should().OnlyContain(c => c.ViaCategory == DerivationCategory.SaNominal);
    }

    [Fact]
    public void UnlinkedForm_ResolvesNothing()
    {
        var cache = BuildCache(FormClosure(Tsuyoi, Tsuyosa, DerivationCategory.SaNominal));

        cache.GetCoveringKeys(Fukami, 0, AllCategories).Should().BeEmpty();
        cache.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void PairCountsAreCountedPerWordPairNotPerFormRow()
    {
        var cache = BuildCache([
            ..FormClosure(Tsuyoi, Tsuyosa, DerivationCategory.SaNominal),
            ..FormClosure(Tsuyoi, Tsuyogaru, DerivationCategory.Garu)
        ]);

        cache.PairCounts[DerivationCategory.SaNominal].Should().Be(1);
        cache.PairCounts[DerivationCategory.Garu].Should().Be(1);
    }

    [Fact]
    public void BaseAndDerivedLinksAreExposedSeparatelyForDisplay()
    {
        var cache = BuildCache(FormClosure(Tsuyoi, Tsuyosa, DerivationCategory.SaNominal));

        cache.GetBaseLinks(Tsuyosa, 0).Should().ContainSingle().Which.WordId.Should().Be(Tsuyoi);
        cache.GetDerivedLinks(Tsuyosa, 0).Should().BeEmpty();
        cache.GetDerivedLinks(Tsuyoi, 0).Should().HaveCount(2);
    }
}
