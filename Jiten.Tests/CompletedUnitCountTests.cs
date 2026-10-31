using FluentAssertions;
using Jiten.Api.Helpers;
using Jiten.Core.Data;

namespace Jiten.Tests;

public class CompletedUnitCountTests
{
    private static ReadingTally.CompletedDeckInfo Root(int deckId, MediaType mediaType = MediaType.Novel)
        => new(deckId, null, mediaType, 0, 0);

    private static ReadingTally.CompletedDeckInfo Child(int deckId, int parentDeckId, MediaType mediaType = MediaType.Novel)
        => new(deckId, parentDeckId, mediaType, 0, 0);

    private static (int DeckCount, int UnitCount) Resolve(
        IReadOnlyList<ReadingTally.CompletedDeckInfo> completed,
        Dictionary<int, int>? childCounts = null)
    {
        var (effective, units) = ReadingTally.ResolveCompletedUnits(completed, childCounts ?? new Dictionary<int, int>());
        return (effective.Count(d => d.ParentDeckId == null), effective.Sum(d => units[d.DeckId]));
    }

    [Fact]
    public void StandaloneDeckCountsAsOneUnit()
    {
        Resolve([Root(1)]).Should().Be((1, 1));
    }

    [Fact]
    public void CompletedParentCountsItsChildren()
    {
        Resolve([Root(1)], new Dictionary<int, int> { [1] = 3 }).Should().Be((1, 3));
    }

    [Fact]
    public void ChildrenOfAnIncompleteParentCountAsUnitsOnly()
    {
        Resolve([Child(11, 1), Child(12, 1)], new Dictionary<int, int> { [1] = 3 }).Should().Be((0, 2));
    }

    [Fact]
    public void CompletedParentAndChildrenAreNotDoubleCounted()
    {
        Resolve([Root(1), Child(11, 1), Child(12, 1), Child(13, 1)], new Dictionary<int, int> { [1] = 3 })
            .Should().Be((1, 3));
    }

    [Fact]
    public void UnitsAreGroupedByTheDeckTheyCameFrom()
    {
        List<ReadingTally.CompletedDeckInfo> completed =
        [
            Root(1, MediaType.Novel),
            Root(2, MediaType.Anime),
            Child(21, 3, MediaType.Manga)
        ];
        var childCounts = new Dictionary<int, int> { [1] = 3, [2] = 12 };

        var (effective, units) = ReadingTally.ResolveCompletedUnits(completed, childCounts);

        effective.Where(d => d.MediaType == MediaType.Novel).Sum(d => units[d.DeckId]).Should().Be(3);
        effective.Where(d => d.MediaType == MediaType.Anime).Sum(d => units[d.DeckId]).Should().Be(12);
        effective.Where(d => d.MediaType == MediaType.Manga).Sum(d => units[d.DeckId]).Should().Be(1);
        effective.Sum(d => units[d.DeckId]).Should().Be(16);
    }

    private static ReadingTally.CompletedDeckInfo Sized(int deckId, int characters, int words)
        => new(deckId, null, MediaType.VisualNovel, characters, words);

    private static List<ReadingTally.CompletedEntry> Passes(params int?[] typed) =>
        typed.Select((c, i) => new ReadingTally.CompletedEntry(i + 1, c)).ToList();

    [Fact]
    public void DeckWithoutEntriesCountsOnce()
    {
        ReadingTally.SumCompletedReading([Sized(1, 1000, 400)], new Dictionary<int, List<ReadingTally.CompletedEntry>>())
                    .Should().Be((1000L, 400L));
    }

    [Fact]
    public void EachCompletedEntryAddsTheDeckAgain_ATypedCountReplacingCharactersButNotWords()
    {
        var entries = new Dictionary<int, List<ReadingTally.CompletedEntry>> { [1] = Passes(600, null, null) };
        ReadingTally.SumCompletedReading([Sized(1, 1000, 400)], entries).Should().Be((2600L, 1200L));
    }

    private static ReadingTally.CompletedDeckInfo SizedChild(int deckId, int parentDeckId, int characters)
        => new(deckId, parentDeckId, MediaType.Novel, characters, 100);

    private static readonly ReadingTally.CompletedDeckInfo Series = new(10, null, MediaType.Novel, 3000, 300);

    private static readonly List<ReadingTally.CompletedDeckInfo> SeriesAndVolumes =
        [Series, SizedChild(11, 10, 1000), SizedChild(12, 10, 1000), SizedChild(13, 10, 1000)];

    /// <summary>Volume completions as "volume:link:count", with "-" for none, in the order the entries were created.</summary>
    private static Dictionary<int, List<ReadingTally.CompletedEntry>> SeriesEntries(List<ReadingTally.CompletedEntry> seriesPasses, string volumeCompletions)
    {
        var entries = new Dictionary<int, List<ReadingTally.CompletedEntry>> { [10] = seriesPasses };
        var id = 100;
        foreach (var completion in volumeCompletions.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = completion.Split(':');
            var deckId = int.Parse(parts[0]);
            long? link = parts[1] == "-" ? null : long.Parse(parts[1]);
            int? typed = parts.Length > 2 && parts[2] != "-" ? int.Parse(parts[2]) : null;
            if (!entries.TryGetValue(deckId, out var list))
                entries[deckId] = list = [];
            list.Add(new ReadingTally.CompletedEntry(id++, typed, link));
        }

        return entries;
    }

    private static (long Characters, long Words) SeriesReading(List<ReadingTally.CompletedEntry> seriesPasses, string volumeCompletions)
    {
        var entries = SeriesEntries(seriesPasses, volumeCompletions);
        var totals = ReadingTally.ResolveSeriesTotals(SeriesAndVolumes, entries);
        var (effective, _) = ReadingTally.ResolveCompletedUnits(SeriesAndVolumes, new Dictionary<int, int> { [10] = 3 });
        return ReadingTally.SumCompletedReading(effective, entries, totals);
    }

    /// <summary>Series passes have entry ids 1 and 2; each covers one completion per volume, linked to it or else the oldest unlinked one.</summary>
    [Theory]
    [InlineData(1, "11:-,12:-,13:-", 3000L, 300L)]
    [InlineData(2, "11:-,12:-,13:-,11:2,12:2,13:2", 6000L, 600L)]
    [InlineData(2, "11:-,12:-,13:-,11:-,12:-,13:-", 6000L, 600L)]
    [InlineData(2, "11:-,12:-,13:-,11:2,12:2,13:2,11:-", 7000L, 700L)]
    [InlineData(1, "11:-,12:-,13:-,11:2", 4000L, 400L)]
    public void CompletedSeries_CountsEachVolumeOncePerPass_AndOtherReadsInFull(int seriesPasses, string volumeCompletions, long characters, long words)
    {
        SeriesReading(Passes(new int?[seriesPasses]), volumeCompletions).Should().Be((characters, words));
    }

    [Fact]
    public void TypedVolumeCounts_AdjustTheSeries_ButATypedSeriesCountStandsForItsPass()
    {
        SeriesReading(Passes([null]), "11:-:1400,12:-:900,12:-:900").Characters.Should().Be(3000 + 400 - 100 + 900);
        SeriesReading(Passes(2500), "11:-:1400").Characters.Should().Be(2500);
    }

    [Fact]
    public void VolumeCountsTypedFarBelowJitens_NeverMakeTheSeriesNegative()
    {
        var series = new ReadingTally.CompletedDeckInfo(10, null, MediaType.Novel, 500, 300);
        var completed = new List<ReadingTally.CompletedDeckInfo> { series, SizedChild(11, 10, 1000) };
        var entries = new Dictionary<int, List<ReadingTally.CompletedEntry>> { [10] = Passes([null]), [11] = [new(5, 10)] };

        var totals = ReadingTally.ResolveSeriesTotals(completed, entries);
        var (effective, _) = ReadingTally.ResolveCompletedUnits(completed, new Dictionary<int, int> { [10] = 1 });

        ReadingTally.SumCompletedReading(effective, entries, totals).Characters.Should().Be(0);
    }

    private static Dictionary<int, (MediaType MediaType, long Characters)> Unfinished(Dictionary<int, long> typed, params ReadingTally.CompletedDeckInfo[] completedVolumes)
    {
        var decks = typed.Keys.Select(id => new ReadingTally.UnfinishedDeckInfo(id, id == 10 ? null : 10, MediaType.Novel, id == 10)).ToList();
        var entries = completedVolumes.ToDictionary(v => v.DeckId, _ => Passes([null]));
        return ReadingTally.ResolveUnfinishedCharacters(decks, typed, completedVolumes, entries, new HashSet<int>(), new HashSet<int>(),
                                                        new Dictionary<int, ReadingTally.SeriesTotals>());
    }

    [Fact]
    public void SeriesReadAgain_KeepsItsVolumesPartialCounts()
    {
        var decks = new List<ReadingTally.UnfinishedDeckInfo> { new(12, 10, MediaType.Novel, false) };
        var typed = new Dictionary<int, long> { [12] = 300 };
        var completedRoots = new HashSet<int> { 10 };
        var noEntries = new Dictionary<int, List<ReadingTally.CompletedEntry>>();
        var noTotals = new Dictionary<int, ReadingTally.SeriesTotals>();

        var settled = ReadingTally.ResolveUnfinishedCharacters(decks, typed, [], noEntries, completedRoots, new HashSet<int>(), noTotals);
        var reread = ReadingTally.ResolveUnfinishedCharacters(decks, typed, [], noEntries, completedRoots, completedRoots, noTotals);

        settled.Should().BeEmpty();
        reread[12].Characters.Should().Be(300);
    }

    [Fact]
    public void SeriesCountAboveItsVolumes_TakesTheirPlace()
    {
        var result = Unfinished(new Dictionary<int, long> { [10] = 5000, [12] = 300 }, SizedChild(11, 10, 1000));

        result.Should().ContainKey(10).WhoseValue.Characters.Should().Be(4000);
        result.Should().NotContainKey(12);
    }

    [Fact]
    public void SeriesCountBelowItsVolumes_IsIgnored()
    {
        var result = Unfinished(new Dictionary<int, long> { [10] = 500, [12] = 300 }, SizedChild(11, 10, 1000));

        result[10].Characters.Should().Be(300);
        result.Values.Sum(v => v.Characters).Should().Be(300);
    }

    private static ReadingTally.ReadingLedger Ledger(List<ReadingTally.CompletedDeckInfo> completed, Dictionary<int, List<ReadingTally.CompletedEntry>> entries,
                                                     Dictionary<int, int> childCounts,
                                                     Dictionary<int, (MediaType MediaType, long Characters)>? unfinished = null,
                                                     Dictionary<int, int>? unfinishedTitles = null)
    {
        var (effective, units) = ReadingTally.ResolveCompletedUnits(completed, childCounts);
        return new ReadingTally.ReadingLedger(completed, effective, units, entries, ReadingTally.ResolveSeriesTotals(completed, entries),
                                              unfinished ?? [], unfinishedTitles ?? []);
    }

    [Theory]
    [InlineData(1, "11:-,12:-,13:-")]
    [InlineData(1, "11:-,12:-")]
    [InlineData(2, "11:-,12:-,13:-,11:2,12:2,13:2,11:-")]
    [InlineData(1, "11:-:1400,12:-:900,12:-:900")]
    [InlineData(1, "11:2,12:-")]
    public void Credits_AddUpToTheSeriesTotal(int seriesPasses, string volumeCompletions)
    {
        var ledger = Ledger(SeriesAndVolumes, SeriesEntries(Passes(new int?[seriesPasses]), volumeCompletions), new Dictionary<int, int> { [10] = 3 });

        var credits = ReadingTally.CreditCompletedReading(ledger);

        credits.Should().OnlyContain(c => c.TitleId == 10);
        credits.Sum(c => c.Characters).Should().Be(SeriesReading(Passes(new int?[seriesPasses]), volumeCompletions).Characters);
    }

    [Fact]
    public void SeriesShorterThanItsVolumes_StillCreditsExactlyItsPass()
    {
        var series = new ReadingTally.CompletedDeckInfo(10, null, MediaType.Novel, 1500, 300);
        List<ReadingTally.CompletedDeckInfo> completed = [series, SizedChild(11, 10, 1000), SizedChild(12, 10, 1000)];
        var entries = new Dictionary<int, List<ReadingTally.CompletedEntry>> { [10] = Passes([null]), [11] = [new(5, null, 1)], [12] = [new(6, null, 1)] };

        var credits = ReadingTally.CreditCompletedReading(Ledger(completed, entries, new Dictionary<int, int> { [10] = 2 }));

        credits.Should().OnlyContain(c => c.Characters >= 0);
        credits.Sum(c => c.Characters).Should().Be(1500);
    }

    [Fact]
    public void TypedSeriesPass_IsOneCredit_AndItsVolumesAddNothing()
    {
        var ledger = Ledger(SeriesAndVolumes, SeriesEntries(Passes(2500), "11:-:1400,12:-"), new Dictionary<int, int> { [10] = 3 });

        ReadingTally.CreditCompletedReading(ledger).Where(c => c.Characters > 0).Select(c => c.Characters).Should().Equal(2500);
    }

    [Fact]
    public void VolumesLandOnTheirOwnDates_AndTheSeriesPassAddsTheRest()
    {
        var january = new DateOnly(2026, 1, 10);
        var february = new DateOnly(2026, 2, 10);
        var march = new DateOnly(2026, 3, 20);
        var april = new DateOnly(2026, 4, 2);
        var entries = new Dictionary<int, List<ReadingTally.CompletedEntry>>
                      {
                          [10] = [new(1, null, FinishedOn: march)],
                          [11] = [new(100, null, 1, january), new(103, 800, null, april)],
                          [12] = [new(101, null, 1, february)]
                      };

        var credits = ReadingTally.CreditCompletedReading(Ledger(SeriesAndVolumes, entries, new Dictionary<int, int> { [10] = 3 }));

        credits.GroupBy(c => c.FinishedOn).ToDictionary(g => g.Key!.Value, g => g.Sum(c => c.Characters))
               .Should().Equal(new Dictionary<DateOnly, long> { [january] = 1000, [february] = 1000, [march] = 1000, [april] = 800 });
    }

    [Fact]
    public void EachPassOfAStandaloneTitle_IsCreditedOnItsDate_WithATypedCountFirst()
    {
        var first = new DateOnly(2025, 6, 1);
        var second = new DateOnly(2026, 6, 1);
        var entries = new Dictionary<int, List<ReadingTally.CompletedEntry>> { [1] = [new(1, 600, FinishedOn: first), new(2, null, FinishedOn: second)] };

        var credits = ReadingTally.CreditCompletedReading(Ledger([Sized(1, 1000, 400)], entries, []));

        credits.Should().Equal(new ReadingTally.ReadingCredit(1, MediaType.VisualNovel, first, 600), new ReadingTally.ReadingCredit(1, MediaType.VisualNovel, second, 1000));
    }

    [Fact]
    public void CharactersByTitle_AddsReadingInProgress_AndFoldsVolumesIntoTheirSeries()
    {
        var volumeOfUnfinishedSeries = SizedChild(21, 20, 1000);
        var entries = new Dictionary<int, List<ReadingTally.CompletedEntry>> { [21] = Passes([null]) };
        var unfinished = new Dictionary<int, (MediaType MediaType, long Characters)> { [20] = (MediaType.Novel, 500), [31] = (MediaType.Novel, 250) };

        var ledger = Ledger([volumeOfUnfinishedSeries], entries, [], unfinished, new Dictionary<int, int> { [20] = 20, [31] = 30 });
        var credits = ReadingTally.CreditCompletedReading(ledger);

        ReadingTally.CharactersByTitle(ledger, credits, withUnfinished: true).Should().Equal(new Dictionary<int, long> { [20] = 1500, [30] = 250 });
        ReadingTally.CharactersByTitle(ledger, credits, withUnfinished: false).Should().Equal(new Dictionary<int, long> { [20] = 1000 });
    }
}
