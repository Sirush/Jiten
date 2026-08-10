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
}
