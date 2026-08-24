using System.Diagnostics;
using FluentAssertions;
using Jiten.Api.Services.ExternalMediaList;

namespace Jiten.Tests;

public class ExternalFetchGateTests
{
    private static ExternalFetchGate Gate(int spacingMs, int maxWaitMs) =>
        new(TimeSpan.FromMilliseconds(spacingMs), TimeSpan.FromMilliseconds(spacingMs), TimeSpan.FromMilliseconds(maxWaitMs));

    [Fact]
    public async Task SpacesConsecutiveFetchesOfTheSameProvider()
    {
        var gate = Gate(spacingMs: 200, maxWaitMs: 5000);

        (await gate.EnterAsync(ExternalListProvider.Anilist)).Should().BeTrue();
        gate.Exit(ExternalListProvider.Anilist);

        var stopwatch = Stopwatch.StartNew();
        (await gate.EnterAsync(ExternalListProvider.Anilist)).Should().BeTrue();
        stopwatch.Stop();
        gate.Exit(ExternalListProvider.Anilist);

        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150));
    }

    [Fact]
    public async Task DoesNotSpaceAcrossProviders()
    {
        var gate = Gate(spacingMs: 2000, maxWaitMs: 5000);

        (await gate.EnterAsync(ExternalListProvider.Anilist)).Should().BeTrue();
        gate.Exit(ExternalListProvider.Anilist);

        var stopwatch = Stopwatch.StartNew();
        (await gate.EnterAsync(ExternalListProvider.Vndb)).Should().BeTrue();
        stopwatch.Stop();
        gate.Exit(ExternalListProvider.Vndb);

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(1000));
    }

    [Fact]
    public async Task ReturnsFalseWhenTheProviderStaysBusy()
    {
        var gate = Gate(spacingMs: 0, maxWaitMs: 100);

        (await gate.EnterAsync(ExternalListProvider.Anilist)).Should().BeTrue();

        (await gate.EnterAsync(ExternalListProvider.Anilist)).Should().BeFalse();

        gate.Exit(ExternalListProvider.Anilist);
        (await gate.EnterAsync(ExternalListProvider.Anilist)).Should().BeTrue();
        gate.Exit(ExternalListProvider.Anilist);
    }

    [Fact]
    public async Task ReturnsFalseWhenSpacingWouldOutlastTheBudget()
    {
        var gate = Gate(spacingMs: 400, maxWaitMs: 100);

        (await gate.EnterAsync(ExternalListProvider.Anilist)).Should().BeTrue();
        gate.Exit(ExternalListProvider.Anilist);

        (await gate.EnterAsync(ExternalListProvider.Anilist)).Should().BeFalse();

        // A refusal releases the slot, so a caller past the spacing window still gets in.
        await Task.Delay(500);
        (await gate.EnterAsync(ExternalListProvider.Anilist)).Should().BeTrue();
        gate.Exit(ExternalListProvider.Anilist);
    }
}
