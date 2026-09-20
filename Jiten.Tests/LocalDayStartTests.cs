using FluentAssertions;
using Jiten.Api.Helpers;
using Xunit;

namespace Jiten.Tests;

public class LocalDayStartTests
{
    // Chile springs forward at 00:00 local on 2025-09-07, so that day has no midnight.
    private static readonly DateTime SantiagoDstStartNoonUtc = new(2025, 9, 7, 16, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DstGapAtMidnight_UsesFirstValidInstant()
    {
        var start = FsrsSettingsHelper.LocalDayStartUtc(SantiagoDstStartNoonUtc, "America/Santiago");

        start.Should().Be(new DateTime(2025, 9, 7, 4, 0, 0, DateTimeKind.Utc));
        start.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void DayBeforeDstGap_NextDayStartDoesNotThrow()
    {
        var dayBefore = SantiagoDstStartNoonUtc.AddDays(-1);

        var next = FsrsSettingsHelper.LocalDayStartUtc(dayBefore, "America/Santiago", 1);

        next.Should().Be(new DateTime(2025, 9, 7, 4, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void OrdinaryDay_IsLocalMidnight()
    {
        var utcNow = new DateTime(2025, 3, 10, 12, 0, 0, DateTimeKind.Utc);

        var start = FsrsSettingsHelper.LocalDayStartUtc(utcNow, "Asia/Tokyo");

        start.Should().Be(new DateTime(2025, 3, 9, 15, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void UnknownOrMissingZone_FallsBackToUtcDays()
    {
        var utcNow = new DateTime(2025, 3, 10, 12, 0, 0, DateTimeKind.Utc);

        FsrsSettingsHelper.LocalDayStartUtc(utcNow, null, 1).Should().Be(new DateTime(2025, 3, 11));
        FsrsSettingsHelper.LocalDayStartUtc(utcNow, "Not/AZone", 1).Should().Be(new DateTime(2025, 3, 11));
    }
}
