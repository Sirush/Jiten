using FluentAssertions;
using Jiten.Api.Services;
using Microsoft.Extensions.Time.Testing;

namespace Jiten.Tests;

public class ParseThrottleServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TinyRequestsAreChargedTheMinimum()
    {
        var clock = new FakeTimeProvider(Start);
        var throttle = new ParseThrottleService(clock);
        var allowed = ParseThrottleService.BudgetPerWindow / ParseThrottleService.MinimumCharge;

        for (var i = 0; i < allowed; i++)
            throttle.TryConsume("u", 20, out _).Should().BeTrue($"request {i} is within budget");

        throttle.TryConsume("u", 20, out var retryAfter).Should().BeFalse();
        retryAfter.Should().Be(ParseThrottleService.Window);
    }

    [Fact]
    public void LargeRequestsAreChargedTheirLength()
    {
        var clock = new FakeTimeProvider(Start);
        var throttle = new ParseThrottleService(clock);

        throttle.TryConsume("u", 150_000, out _).Should().BeTrue();
        throttle.TryConsume("u", 60_000, out _).Should().BeFalse();
        throttle.TryConsume("u", 50_000, out _).Should().BeTrue();
    }

    [Fact]
    public void RetryAfterCountsDownToTheWindowReset()
    {
        var clock = new FakeTimeProvider(Start);
        var throttle = new ParseThrottleService(clock);

        throttle.TryConsume("u", ParseThrottleService.BudgetPerWindow, out _).Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(45));

        throttle.TryConsume("u", 1, out var retryAfter).Should().BeFalse();
        retryAfter.Should().Be(TimeSpan.FromSeconds(15));

        clock.Advance(TimeSpan.FromSeconds(15));
        throttle.TryConsume("u", 1, out retryAfter).Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void BucketsAreIndependentPerUser()
    {
        var clock = new FakeTimeProvider(Start);
        var throttle = new ParseThrottleService(clock);

        throttle.TryConsume("a", ParseThrottleService.BudgetPerWindow, out _).Should().BeTrue();
        throttle.TryConsume("a", 1, out _).Should().BeFalse();
        throttle.TryConsume("b", 1, out _).Should().BeTrue();
    }
}
