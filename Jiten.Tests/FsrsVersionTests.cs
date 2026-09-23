using FluentAssertions;
using Jiten.Core.Data.FSRS;

namespace Jiten.Parser.Tests;

public class FsrsVersionTests
{
    [Theory]
    [InlineData(21, FsrsVersion.V6)]
    [InlineData(34, FsrsVersion.V7)]
    public void FromParameterCount_MapsKnownLengths(int count, FsrsVersion expected)
        => FsrsVersions.FromParameterCount(count).Should().Be(expected);

    [Theory]
    [InlineData(0)]
    [InlineData(19)]
    [InlineData(35)]
    public void FromParameterCount_RejectsOtherLengths(int count)
        => FsrsVersions.FromParameterCount(count).Should().BeNull();

    [Fact]
    public void DefaultParameters_HaveTheirVersionsLength()
    {
        FsrsVersions.ParameterCount(FsrsVersion.V6).Should().Be(21);
        FsrsVersions.ParameterCount(FsrsVersion.V7).Should().Be(34);
    }

    [Fact]
    public void FollowsUnoptimised_TreatsEmptyAndUnoptimisedDefaultsAsFollowing()
    {
        FsrsVersions.FollowsUnoptimised([]).Should().BeTrue();
        FsrsVersions.FollowsUnoptimised(FsrsVersions.DefaultParameters(FsrsVersions.Unoptimised).ToArray()).Should().BeTrue();
    }

    [Fact]
    public void FollowsUnoptimised_PinsTheOtherVersionsDefaultsAndCustomSets()
    {
        var other = FsrsVersions.Unoptimised == FsrsVersion.V6 ? FsrsVersion.V7 : FsrsVersion.V6;
        FsrsVersions.FollowsUnoptimised(FsrsVersions.DefaultParameters(other).ToArray()).Should().BeFalse();

        var custom = FsrsVersions.DefaultParameters(FsrsVersions.Unoptimised).ToArray();
        custom[^1] += 0.01;
        FsrsVersions.FollowsUnoptimised(custom).Should().BeFalse();
        FsrsVersions.FollowsUnoptimised([1.0, 2.0]).Should().BeFalse();
    }

    [Fact]
    public void IsDefaultFor_MatchesOnlyItsOwnVersion()
    {
        FsrsVersions.IsDefaultFor(FsrsConstants.DefaultParameters.ToArray(), FsrsVersion.V6).Should().BeTrue();
        FsrsVersions.IsDefaultFor(FsrsConstants.DefaultParameters.ToArray(), FsrsVersion.V7).Should().BeFalse();
        FsrsVersions.IsDefaultFor(FsrsConstants.DefaultParametersV7.ToArray(), FsrsVersion.V7).Should().BeTrue();
    }
}
