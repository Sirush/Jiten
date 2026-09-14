using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Jiten.Api.Services.SmartDeck;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace Jiten.Tests;

public class SmartDeckDirtyServiceTests
{
    private readonly Dictionary<string, long> _counters = new();
    private readonly HashSet<string> _keys = new();
    private readonly HashSet<string> _enabled = new();
    private readonly Mock<IBackgroundJobClient> _jobs = new();
    private readonly SmartDeckDirtyService _service;

    public SmartDeckDirtyServiceTests()
    {
        var db = new Mock<IDatabase>();
        db.Setup(d => d.SetContainsAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
          .Returns((RedisKey _, RedisValue member, CommandFlags _) => Task.FromResult(_enabled.Contains(member.ToString())));
        db.Setup(d => d.SetAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
          .Returns((RedisKey _, RedisValue member, CommandFlags _) => Task.FromResult(_enabled.Add(member.ToString())));
        db.Setup(d => d.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
          .Returns((RedisKey key, long by, CommandFlags _) =>
          {
              _counters[key.ToString()] = _counters.GetValueOrDefault(key.ToString()) + by;
              return Task.FromResult(_counters[key.ToString()]);
          });
        db.Setup(d => d.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);
        db.Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
          .Returns((RedisKey key, RedisValue _, TimeSpan? _, bool _, When when, CommandFlags _) =>
              Task.FromResult(when != When.NotExists || _keys.Add(key.ToString())));
        db.Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
          .Returns((RedisKey key, CommandFlags _) => Task.FromResult(_keys.Remove(key.ToString())));
        db.Setup(d => d.StringGetDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
          .Returns((RedisKey key, CommandFlags _) =>
          {
              var had = _counters.Remove(key.ToString(), out var value);
              return Task.FromResult(had ? (RedisValue)value : RedisValue.Null);
          });

        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db.Object);
        _service = new SmartDeckDirtyService(mux.Object, _jobs.Object, NullLogger<SmartDeckDirtyService>.Instance);
    }

    [Fact]
    public async Task BurstOfMarks_SchedulesOneJob_AndCountsEveryMark()
    {
        await _service.SetEnabled("u1", true);
        for (var i = 0; i < 500; i++) await _service.MarkDirty("u1");

        _jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<ScheduledState>()), Times.Once);
        (await _service.ConsumeDirty("u1")).Should().Be(500);
        (await _service.ConsumeDirty("u1")).Should().Be(0);
    }

    [Fact]
    public async Task MarksForUsersWithoutSmartDeck_AreIgnored()
    {
        (await _service.MarkDirty("nobody")).Should().BeFalse();

        _jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
        (await _service.ConsumeDirty("nobody")).Should().Be(0);
    }

    [Fact]
    public async Task MarkDirty_ReportsSuccess_WhetherItScheduledOrJoinedTheGate()
    {
        await _service.SetEnabled("u1", true);

        (await _service.MarkDirty("u1")).Should().BeTrue();
        (await _service.MarkDirty("u1")).Should().BeTrue();
        _jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<ScheduledState>()), Times.Once);
    }

    [Fact]
    public async Task MarkDirty_WithoutRedis_ReportsFailure()
    {
        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Throws(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        var service = new SmartDeckDirtyService(mux.Object, _jobs.Object, NullLogger<SmartDeckDirtyService>.Instance);

        (await service.MarkDirty("u1")).Should().BeFalse();
        (await service.IsRebuildPending("u1")).Should().BeFalse();
        _jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task ReleasingTheGate_LetsTheNextMarkScheduleAgain()
    {
        await _service.SetEnabled("u1", true);
        await _service.MarkDirty("u1");
        await _service.MarkDirty("u1");
        await _service.ReleaseQueuedGate("u1");
        await _service.MarkDirty("u1");

        _jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<ScheduledState>()), Times.Exactly(2));
    }
}
