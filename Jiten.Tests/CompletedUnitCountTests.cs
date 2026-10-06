using FluentAssertions;
using Jiten.Api.Jobs;
using Jiten.Core.Data;

namespace Jiten.Tests;

public class CompletedUnitCountTests
{
    private static ComputationJob.CompletedDeckInfo Root(int deckId, MediaType mediaType = MediaType.Novel)
        => new(deckId, null, mediaType, 0, 0);

    private static ComputationJob.CompletedDeckInfo Child(int deckId, int parentDeckId, MediaType mediaType = MediaType.Novel)
        => new(deckId, parentDeckId, mediaType, 0, 0);

    private static (int DeckCount, int UnitCount) Resolve(
        IReadOnlyList<ComputationJob.CompletedDeckInfo> completed,
        Dictionary<int, int>? childCounts = null)
    {
        var (effective, units) = ComputationJob.ResolveCompletedUnits(completed, childCounts ?? new Dictionary<int, int>());
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
        List<ComputationJob.CompletedDeckInfo> completed =
        [
            Root(1, MediaType.Novel),
            Root(2, MediaType.Anime),
            Child(21, 3, MediaType.Manga)
        ];
        var childCounts = new Dictionary<int, int> { [1] = 3, [2] = 12 };

        var (effective, units) = ComputationJob.ResolveCompletedUnits(completed, childCounts);

        effective.Where(d => d.MediaType == MediaType.Novel).Sum(d => units[d.DeckId]).Should().Be(3);
        effective.Where(d => d.MediaType == MediaType.Anime).Sum(d => units[d.DeckId]).Should().Be(12);
        effective.Where(d => d.MediaType == MediaType.Manga).Sum(d => units[d.DeckId]).Should().Be(1);
        effective.Sum(d => units[d.DeckId]).Should().Be(16);
    }

    private static ComputationJob.CompletedDeckInfo Sized(int deckId, int characters, int words)
        => new(deckId, null, MediaType.VisualNovel, characters, words);

    private static List<ComputationJob.CompletedEntry> Passes(params int?[] typed) =>
        typed.Select((c, i) => new ComputationJob.CompletedEntry(i + 1, c)).ToList();

    [Fact]
    public void DeckWithoutEntriesCountsOnce()
    {
        ComputationJob.SumCompletedReading([Sized(1, 1000, 400)], new Dictionary<int, List<ComputationJob.CompletedEntry>>())
                      .Should().Be((1000L, 400L));
    }

    [Fact]
    public void EachCompletedEntryAddsTheDeckAgain_ATypedCountReplacingCharactersButNotWords()
    {
        var entries = new Dictionary<int, List<ComputationJob.CompletedEntry>> { [1] = Passes(600, null, null) };
        ComputationJob.SumCompletedReading([Sized(1, 1000, 400)], entries).Should().Be((2600L, 1200L));
    }

    private static ComputationJob.CompletedDeckInfo SizedChild(int deckId, int parentDeckId, int characters)
        => new(deckId, parentDeckId, MediaType.Novel, characters, 100);

    private static readonly ComputationJob.CompletedDeckInfo Series = new(10, null, MediaType.Novel, 3000, 300);

    private static readonly List<ComputationJob.CompletedDeckInfo> SeriesAndVolumes =
        [Series, SizedChild(11, 10, 1000), SizedChild(12, 10, 1000), SizedChild(13, 10, 1000)];

    /// <summary>Volume completions as "volume:link:count", with "-" for none, in the order the entries were created.</summary>
    private static Dictionary<int, List<ComputationJob.CompletedEntry>> SeriesEntries(List<ComputationJob.CompletedEntry> seriesPasses, string volumeCompletions)
    {
        var entries = new Dictionary<int, List<ComputationJob.CompletedEntry>> { [10] = seriesPasses };
        var id = 100;
        foreach (var completion in volumeCompletions.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = completion.Split(':');
            var deckId = int.Parse(parts[0]);
            long? link = parts[1] == "-" ? null : long.Parse(parts[1]);
            int? typed = parts.Length > 2 && parts[2] != "-" ? int.Parse(parts[2]) : null;
            if (!entries.TryGetValue(deckId, out var list))
                entries[deckId] = list = [];
            list.Add(new ComputationJob.CompletedEntry(id++, typed, link));
        }

        return entries;
    }

    private static (long Characters, long Words) SeriesReading(List<ComputationJob.CompletedEntry> seriesPasses, string volumeCompletions)
    {
        var entries = SeriesEntries(seriesPasses, volumeCompletions);
        var totals = ComputationJob.ResolveSeriesTotals(SeriesAndVolumes, entries);
        var (effective, _) = ComputationJob.ResolveCompletedUnits(SeriesAndVolumes, new Dictionary<int, int> { [10] = 3 });
        return ComputationJob.SumCompletedReading(effective, entries, totals);
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
        var series = new ComputationJob.CompletedDeckInfo(10, null, MediaType.Novel, 500, 300);
        var completed = new List<ComputationJob.CompletedDeckInfo> { series, SizedChild(11, 10, 1000) };
        var entries = new Dictionary<int, List<ComputationJob.CompletedEntry>> { [10] = Passes([null]), [11] = [new(5, 10)] };

        var totals = ComputationJob.ResolveSeriesTotals(completed, entries);
        var (effective, _) = ComputationJob.ResolveCompletedUnits(completed, new Dictionary<int, int> { [10] = 1 });

        ComputationJob.SumCompletedReading(effective, entries, totals).Characters.Should().Be(0);
    }

    private static Dictionary<int, (MediaType MediaType, long Characters)> Unfinished(Dictionary<int, long> typed, params ComputationJob.CompletedDeckInfo[] completedVolumes)
    {
        var decks = typed.Keys.Select(id => new ComputationJob.UnfinishedDeckInfo(id, id == 10 ? null : 10, MediaType.Novel, id == 10)).ToList();
        var entries = completedVolumes.ToDictionary(v => v.DeckId, _ => Passes([null]));
        return ComputationJob.ResolveUnfinishedCharacters(decks, typed, completedVolumes, entries, new HashSet<int>(), new HashSet<int>(),
                                                          new Dictionary<int, ComputationJob.SeriesTotals>());
    }

    [Fact]
    public void SeriesReadAgain_KeepsItsVolumesPartialCounts()
    {
        var decks = new List<ComputationJob.UnfinishedDeckInfo> { new(12, 10, MediaType.Novel, false) };
        var typed = new Dictionary<int, long> { [12] = 300 };
        var completedRoots = new HashSet<int> { 10 };
        var noEntries = new Dictionary<int, List<ComputationJob.CompletedEntry>>();
        var noTotals = new Dictionary<int, ComputationJob.SeriesTotals>();

        var settled = ComputationJob.ResolveUnfinishedCharacters(decks, typed, [], noEntries, completedRoots, new HashSet<int>(), noTotals);
        var reread = ComputationJob.ResolveUnfinishedCharacters(decks, typed, [], noEntries, completedRoots, completedRoots, noTotals);

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
}
