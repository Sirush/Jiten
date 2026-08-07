using FluentAssertions;
using Jiten.Core.Data.FSRS;

namespace Jiten.Tests;

public class ReviewLogPackerTests
{
    private static readonly DateTime Base = new(2024, 3, 1, 8, 30, 0, DateTimeKind.Utc);

    private static List<PackedReview> Series(int count, int? durationMs = 4200)
        => Enumerable.Range(0, count)
                     .Select(i => new PackedReview((FsrsRating)(i % 4 + 1), Base.AddDays(i).AddMinutes(i), durationMs))
                     .ToList();

    [Fact]
    public void Pack_NoReviews_ProducesNoBlob()
    {
        var result = ReviewLogPacker.Pack(Array.Empty<PackedReview>());

        result.Logs.Should().BeNull();
        result.FirstReview.Should().BeNull();
        result.ReviewCount.Should().Be(0);
        result.Truncated.Should().BeFalse();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(500)]
    [InlineData(5000)]
    public void RoundTrip_PreservesEveryReview(int count)
    {
        var reviews = Series(count);

        var packed = ReviewLogPacker.Pack(reviews);
        packed.ReviewCount.Should().Be(count);
        packed.Truncated.Should().BeFalse();
        packed.Logs!.Length.Should().Be(8 + count * 7);

        var unpacked = ReviewLogPacker.Unpack(packed.Logs!, packed.FirstReview!.Value);

        unpacked.Should().HaveCount(count);
        for (var i = 0; i < count; i++)
        {
            unpacked[i].Rating.Should().Be(reviews[i].Rating);
            unpacked[i].ReviewDateTime.Should().Be(reviews[i].ReviewDateTime);
            unpacked[i].ReviewDuration.Should().Be(4200);
        }
    }

    [Fact]
    public void Pack_BeyondTheCap_KeepsTheMostRecentAndReportsTheTrueTotal()
    {
        var reviews = Series(5001);

        var packed = ReviewLogPacker.Pack(reviews);

        packed.ReviewCount.Should().Be(5001);
        packed.Truncated.Should().BeTrue();
        ReviewLogPacker.IsTruncated(packed.Logs).Should().BeTrue();

        var unpacked = ReviewLogPacker.Unpack(packed.Logs!, packed.FirstReview!.Value);
        unpacked.Should().HaveCount(ReviewLogPacker.MaxEntries);
        unpacked[0].ReviewDateTime.Should().Be(reviews[1].ReviewDateTime);
        unpacked[^1].ReviewDateTime.Should().Be(reviews[^1].ReviewDateTime);
    }

    [Fact]
    public void RoundTrip_NullAndClampedDurations()
    {
        var reviews = new List<PackedReview>
                      {
                          new(FsrsRating.Good, Base, null),
                          new(FsrsRating.Good, Base.AddMinutes(1), 0),
                          new(FsrsRating.Good, Base.AddMinutes(2), 60),
                          new(FsrsRating.Good, Base.AddMinutes(3), 99_999_999)
                      };

        var packed = ReviewLogPacker.Pack(reviews);
        var unpacked = ReviewLogPacker.Unpack(packed.Logs!, packed.FirstReview!.Value);

        unpacked[0].ReviewDuration.Should().BeNull();
        unpacked[1].ReviewDuration.Should().BeNull();
        unpacked[2].ReviewDuration.Should().Be(100);
        unpacked[3].ReviewDuration.Should().Be(6_553_400);
    }

    [Fact]
    public void RoundTrip_SpansManyYears()
    {
        var reviews = new List<PackedReview>
                      {
                          new(FsrsRating.Again, Base, 1000),
                          new(FsrsRating.Easy, Base.AddYears(40), 1000)
                      };

        var packed = ReviewLogPacker.Pack(reviews);
        var unpacked = ReviewLogPacker.Unpack(packed.Logs!, packed.FirstReview!.Value);

        unpacked[1].ReviewDateTime.Should().Be(Base.AddYears(40));
    }

    [Fact]
    public void Pack_SortsBeforeWriting()
    {
        var reviews = new List<PackedReview>
                      {
                          new(FsrsRating.Easy, Base.AddDays(5), null),
                          new(FsrsRating.Again, Base, null),
                          new(FsrsRating.Good, Base.AddDays(2), null)
                      };

        var packed = ReviewLogPacker.Pack(reviews);

        packed.FirstReview.Should().Be(Base);
        var unpacked = ReviewLogPacker.Unpack(packed.Logs!, packed.FirstReview!.Value);
        unpacked.Select(r => r.ReviewDateTime).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Unpack_ShortBuffer_Throws()
    {
        var act = () => ReviewLogPacker.Unpack([1, 0, 0], Base);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Unpack_UnknownVersion_Throws()
    {
        var packed = ReviewLogPacker.Pack(Series(3));
        packed.Logs![0] = 99;

        var act = () => ReviewLogPacker.Unpack(packed.Logs, packed.FirstReview!.Value);
        act.Should().Throw<InvalidDataException>().WithMessage("*version 99*");
    }

    [Fact]
    public void Unpack_EntryCountDisagreeingWithLength_Throws()
    {
        var packed = ReviewLogPacker.Pack(Series(3));
        var truncated = packed.Logs!.Take(packed.Logs.Length - 3).ToArray();

        var act = () => ReviewLogPacker.Unpack(truncated, packed.FirstReview!.Value);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Unpack_InvalidRating_Throws()
    {
        var packed = ReviewLogPacker.Pack(Series(3));
        packed.Logs![8 + 4] = 7;

        var act = () => ReviewLogPacker.Unpack(packed.Logs, packed.FirstReview!.Value);
        act.Should().Throw<InvalidDataException>().WithMessage("*rating 7*");
    }

    [Fact]
    public void Unpack_NonMonotonicDeltas_Throws()
    {
        var packed = ReviewLogPacker.Pack(Series(3));
        // Rewrite the third entry's delta to zero, putting it before the second.
        Array.Clear(packed.Logs!, 8 + 2 * 7, 4);

        var act = () => ReviewLogPacker.Unpack(packed.Logs!, packed.FirstReview!.Value);
        act.Should().Throw<InvalidDataException>().WithMessage("*backwards in time*");
    }

    [Fact]
    public void Pack_MarkTruncated_SurvivesTheRoundTrip()
    {
        var packed = ReviewLogPacker.Pack(Series(3), markTruncated: true);

        ReviewLogPacker.IsTruncated(packed.Logs).Should().BeTrue();
        ReviewLogPacker.Unpack(packed.Logs!, packed.FirstReview!.Value).Should().HaveCount(3);
    }
}
