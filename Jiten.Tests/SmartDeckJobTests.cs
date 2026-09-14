using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Jiten.Api.Jobs;
using Jiten.Api.Services.SmartDeck;
using Jiten.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jiten.Tests;

public class SmartDeckJobTests
{
    private static readonly SmartDeckBuildResult Built = new(true, null, 1, 10, TimeSpan.Zero);

    private static SmartDeckJob CreateJob(Mock<ISmartDeckBuilder> builder, Mock<ISmartDeckDirtyService> dirty, IBackgroundJobClient jobs)
        => new(builder.Object, dirty.Object, Mock.Of<IDbContextFactory<UserDbContext>>(), jobs, NullLogger<SmartDeckJob>.Instance);

    [Fact]
    public async Task Rebuild_ConsumesMarks_AndReschedulesWhenMarksLandedMidBuild()
    {
        var dirty = new Mock<ISmartDeckDirtyService>();
        dirty.SetupSequence(d => d.ConsumeDirty("u1")).ReturnsAsync(3).ReturnsAsync(2);
        var builder = new Mock<ISmartDeckBuilder>();
        builder.Setup(b => b.Rebuild("u1", It.IsAny<CancellationToken>())).ReturnsAsync(Built);
        var jobs = new Mock<IBackgroundJobClient>();

        await CreateJob(builder, dirty, jobs.Object).Rebuild("u1");

        dirty.Verify(d => d.ReleaseQueuedGate("u1"), Times.Once);
        builder.Verify(b => b.Rebuild("u1", It.IsAny<CancellationToken>()), Times.Once);
        dirty.Verify(d => d.MarkDirty("u1"), Times.Once);
        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task Rebuild_ReleasesTheGate_WhenTheBuildThrows()
    {
        var dirty = new Mock<ISmartDeckDirtyService>();
        dirty.Setup(d => d.ConsumeDirty("u1")).ReturnsAsync(0);
        var builder = new Mock<ISmartDeckBuilder>();
        builder.Setup(b => b.Rebuild("u1", It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));

        var act = () => CreateJob(builder, dirty, Mock.Of<IBackgroundJobClient>()).Rebuild("u1");

        await act.Should().ThrowAsync<InvalidOperationException>();
        dirty.Verify(d => d.ReleaseQueuedGate("u1"), Times.Once);
    }

    [Fact]
    public async Task Rebuild_QuietBuild_DoesNotReschedule()
    {
        var dirty = new Mock<ISmartDeckDirtyService>();
        dirty.Setup(d => d.ConsumeDirty("u1")).ReturnsAsync(0);
        var builder = new Mock<ISmartDeckBuilder>();
        builder.Setup(b => b.Rebuild("u1", It.IsAny<CancellationToken>())).ReturnsAsync(Built);
        var jobs = new Mock<IBackgroundJobClient>();

        await CreateJob(builder, dirty, jobs.Object).Rebuild("u1");

        dirty.Verify(d => d.MarkDirty("u1"), Times.Never);
        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task Rebuild_ConcurrentSameUser_RunsOnce_AndMarksTheLoserDirty()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var dirty = new Mock<ISmartDeckDirtyService>();
        dirty.Setup(d => d.ConsumeDirty("u1")).ReturnsAsync(0);
        var builder = new Mock<ISmartDeckBuilder>();
        builder.Setup(b => b.Rebuild("u1", It.IsAny<CancellationToken>()))
               .Returns(async () =>
               {
                   started.TrySetResult();
                   await release.Task;
                   return Built;
               });

        var job = CreateJob(builder, dirty, Mock.Of<IBackgroundJobClient>());
        var first = job.Rebuild("u1");
        await started.Task;
        await job.Rebuild("u1");
        release.SetResult();
        await first;

        builder.Verify(b => b.Rebuild("u1", It.IsAny<CancellationToken>()), Times.Once);
        dirty.Verify(d => d.MarkDirty("u1"), Times.Once);
    }
}
