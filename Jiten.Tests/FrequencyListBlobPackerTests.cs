using FluentAssertions;
using Jiten.Core.Data.Billing;

namespace Jiten.Tests;

public class FrequencyListBlobPackerTests
{
    private static readonly List<(int WordId, byte ReadingIndex)> Sample =
    [
        (1, 0), (5, 2), (1_000_000, 255), (42, 1)
    ];

    [Fact]
    public void Pack_Unpack_RoundTrips()
    {
        var blob = FrequencyListBlobPacker.Pack(Sample);

        blob.Length.Should().Be(Sample.Count * FrequencyListBlobPacker.EntrySize);
        FrequencyListBlobPacker.Unpack(blob).Should().Equal(Sample);
    }

    [Fact]
    public void Unpack_EmptyOrNull_ReturnsEmpty()
    {
        FrequencyListBlobPacker.Unpack(null).Should().BeEmpty();
        FrequencyListBlobPacker.Unpack([]).Should().BeEmpty();
        FrequencyListBlobPacker.Pack([]).Should().BeEmpty();
    }

    [Fact]
    public void Slice_IsOneBasedAndInclusive()
    {
        var sliced = FrequencyListBlobPacker.Slice(Sample, 2, 3);

        sliced.Should().Equal((5, (byte)2, 2), (1_000_000, (byte)255, 3));
    }

    [Fact]
    public void Slice_NullBoundsCoverEverything()
    {
        var sliced = FrequencyListBlobPacker.Slice(Sample, null, null);

        sliced.Should().HaveCount(Sample.Count);
        sliced.Select(e => e.Rank).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public void Slice_ClampsOutOfRangeBounds()
    {
        FrequencyListBlobPacker.Slice(Sample, -5, 99).Should().HaveCount(Sample.Count);
        FrequencyListBlobPacker.Slice(Sample, 10, 20).Should().BeEmpty();
        FrequencyListBlobPacker.Slice(Sample, 3, 2).Should().BeEmpty();
    }

    [Fact]
    public void DecodeToKeySet_UsesSharedKeyConvention()
    {
        var keys = FrequencyListBlobPacker.DecodeToKeySet(FrequencyListBlobPacker.Pack(Sample));

        keys.Should().BeEquivalentTo(Sample.Select(e => ((long)e.WordId << 8) | e.ReadingIndex));
    }
}
