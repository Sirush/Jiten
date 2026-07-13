using FluentAssertions;
using Jiten.Core.Data.WebNovel;
using Jiten.Core.WebNovel;
using Xunit;

namespace Jiten.Tests;

public class WebNovelScheduleTests
{
    private static WebNovelSource Tracked(int episodes, DateTimeOffset? lastSynced, DateTimeOffset? lastSourceUpdate = null) => new()
    {
        LastEpisodeCount = episodes,
        LastSyncedAt = lastSynced,
        LastSourceUpdate = lastSourceUpdate
    };

    private static WebNovelInfo Polled(int episodes, DateTimeOffset? lastUpdated = null) => new()
    {
        EpisodeCount = episodes,
        LastUpdatedAt = lastUpdated
    };

    [Fact]
    public void CleanNovel_NeverSyncs()
    {
        var tracked = Tracked(100, lastSynced: DateTimeOffset.UtcNow.AddDays(-30));

        WebNovelSchedule.ShouldSync(tracked, Polled(100)).Should().BeFalse();
    }

    [Fact]
    public void SmallBacklog_WaitsForMoreEpisodes()
    {
        // 5 new episodes, synced recently: not worth a reparse yet
        var tracked = Tracked(100, lastSynced: DateTimeOffset.UtcNow.AddDays(-3));

        WebNovelSchedule.ShouldSync(tracked, Polled(105)).Should().BeFalse();
    }

    [Fact]
    public void EpisodeThreshold_TriggersSync()
    {
        var tracked = Tracked(100, lastSynced: DateTimeOffset.UtcNow.AddDays(-1));

        WebNovelSchedule.ShouldSync(tracked, Polled(100 + WebNovelSchedule.MinEpisodesForSync)).Should().BeTrue();
    }

    [Fact]
    public void MaxLag_FlushesSmallBacklog()
    {
        // A single pending episode still lands once it has waited out the lag window
        var tracked = Tracked(100, lastSynced: DateTimeOffset.UtcNow - WebNovelSchedule.MaxSyncLag);

        WebNovelSchedule.ShouldSync(tracked, Polled(101)).Should().BeTrue();
    }

    [Fact]
    public void NeverSynced_DirtyNovel_SyncsImmediately()
    {
        var tracked = Tracked(100, lastSynced: null);

        WebNovelSchedule.ShouldSync(tracked, Polled(101)).Should().BeTrue();
    }

    [Fact]
    public void RevisionOnlyChange_WaitsForMaxLag()
    {
        // Newer lastup but no new episodes (改稿): dirty, but only worth a sync at the lag window
        var seen = DateTimeOffset.UtcNow.AddDays(-10);
        var revised = DateTimeOffset.UtcNow.AddDays(-1);

        var recent = Tracked(100, lastSynced: DateTimeOffset.UtcNow.AddDays(-2), lastSourceUpdate: seen);
        WebNovelSchedule.ShouldSync(recent, Polled(100, revised)).Should().BeFalse();

        var stale = Tracked(100, lastSynced: DateTimeOffset.UtcNow - WebNovelSchedule.MaxSyncLag, lastSourceUpdate: seen);
        WebNovelSchedule.ShouldSync(stale, Polled(100, revised)).Should().BeTrue();
    }
}
