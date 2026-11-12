using FluentAssertions;
using Jiten.Api.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace Jiten.Tests;

public class BulkDownloadDetectorTests
{
    private static readonly DateTime Day = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly Dictionary<string, HashSet<string>> _sets = new();
    private readonly Mock<IDatabase> _db = new();
    private readonly IConnectionMultiplexer _redis;

    public BulkDownloadDetectorTests()
    {
        _db.Setup(d => d.SetAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
           .Returns((RedisKey key, RedisValue member, CommandFlags _) => Task.FromResult(Set(key).Add(member.ToString())));
        _db.Setup(d => d.SetLengthAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
           .Returns((RedisKey key, CommandFlags _) => Task.FromResult((long)Set(key).Count));
        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_db.Object);
        _redis = mux.Object;
    }

    private HashSet<string> Set(RedisKey key)
    {
        if (!_sets.TryGetValue(key.ToString(), out var set)) _sets[key.ToString()] = set = new HashSet<string>();
        return set;
    }

    private Task<bool> Download(string subject, int deckId, DateTime? at = null) =>
        BulkDownloadDetector.IsBulkAsync(_redis, subject, deckId, at ?? Day, NullLogger.Instance);

    [Fact]
    public async Task DecksUpToTheThreshold_AreNotBulk_AndTheNextOneIs()
    {
        for (var deck = 1; deck <= BulkDownloadDetector.DailyDistinctDecks; deck++)
            (await Download("ip:1.2.3.4", deck)).Should().BeFalse();

        (await Download("ip:1.2.3.4", 999)).Should().BeTrue();
    }

    [Fact]
    public async Task RedownloadingTheSameDeck_NeverBecomesBulk()
    {
        for (var i = 0; i < 100; i++)
            (await Download("user:u1", 42)).Should().BeFalse();
    }

    [Fact]
    public async Task CountsAreKeptPerSubjectAndPerDay()
    {
        for (var deck = 1; deck <= BulkDownloadDetector.DailyDistinctDecks; deck++)
            await Download("ip:1.2.3.4", deck);

        (await Download("ip:5.6.7.8", 999)).Should().BeFalse();
        (await Download("ip:1.2.3.4", 999, Day.AddDays(1))).Should().BeFalse();
        (await Download("ip:1.2.3.4", 999)).Should().BeTrue();
    }

    [Fact]
    public async Task RedisOutage_CountsAsNormalDownload()
    {
        _db.Setup(d => d.SetAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
           .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));

        (await Download("ip:1.2.3.4", 1)).Should().BeFalse();
    }
}
