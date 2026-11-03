using System.Net;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Jiten.Api.Jobs;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.WebNovel;
using Jiten.Core.WebNovel;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jiten.Parser.Tests.Integration;

/// <summary>A source whose table of contents and episode text the test sets directly.</summary>
internal sealed class FakeWebNovelSource(WebNovelProvider provider) : IWebNovelSource
{
    public WebNovelProvider Provider => provider;

    public Dictionary<string, WebNovelInfo> Infos { get; } = new();
    public Dictionary<string, Exception> InfoFailures { get; } = new();
    public List<WebNovelEpisodeRef> Toc { get; set; } = new();
    public List<string> FetchedEpisodeIds { get; } = new();

    public Task<WebNovelInfo> GetInfoAsync(string sourceId, CancellationToken ct = default)
    {
        if (InfoFailures.TryGetValue(sourceId, out var failure))
            throw failure;

        return Task.FromResult(Infos.TryGetValue(sourceId, out var info)
                                   ? info
                                   : new WebNovelInfo { Provider = provider, SourceId = sourceId, EpisodeCount = Toc.Count });
    }

    public Task<List<WebNovelEpisodeRef>> GetTocAsync(string sourceId, CancellationToken ct = default) =>
        Task.FromResult(Toc.ToList());

    public Task<string> GetEpisodeTextAsync(string sourceId, WebNovelEpisodeRef episode, CancellationToken ct = default)
    {
        FetchedEpisodeIds.Add(episode.SourceEpisodeId);
        return Task.FromResult($"本文{episode.SourceEpisodeId}");
    }
}

public class WebNovelJobTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private const int ParentDeckId = 9100;
    private const int ChildDeckId = 9101;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        await InDbAsync(async db =>
        {
            await db.WebNovelChapters.ExecuteDeleteAsync();
            await db.WebNovelSources.ExecuteDeleteAsync();
            return 0;
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<T> InDbAsync<T>(Func<JitenDbContext, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<JitenDbContext>());
    }

    private IDbContextFactory<JitenDbContext> ContextFactory() =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<IDbContextFactory<JitenDbContext>>();

    private static WebNovelEpisodeRef Ref(string id, int position) =>
        new() { SourceEpisodeId = id, Number = position, Title = $"第{position}話" };

    /// <summary>A tracked novel with one open subdeck holding the given episodes, in ledger order.</summary>
    private async Task SeedNovelAsync(WebNovelProvider provider, string sourceId, params string[] episodeIds)
    {
        await InDbAsync(async db =>
        {
            db.Decks.Add(new Deck { DeckId = ParentDeckId, OriginalTitle = $"novel {sourceId}", MediaType = MediaType.WebNovel });
            db.Decks.Add(new Deck
            {
                DeckId = ChildDeckId, ParentDeckId = ParentDeckId, DeckOrder = 1, OriginalTitle = "第1話〜第3話",
                MediaType = MediaType.WebNovel, RawText = new DeckRawText(string.Join("\n\n", episodeIds.Select(e => $"本文{e}")))
            });
            await db.SaveChangesAsync();

            db.WebNovelSources.Add(new WebNovelSource
            {
                DeckId = ParentDeckId, Provider = provider, SourceId = sourceId, LastEpisodeCount = episodeIds.Length,
                NextCheckAt = DateTimeOffset.UtcNow.AddDays(-1), LastSyncedAt = DateTimeOffset.UtcNow.AddDays(-30)
            });
            for (var i = 0; i < episodeIds.Length; i++)
            {
                db.WebNovelChapters.Add(new WebNovelChapter
                {
                    DeckId = ParentDeckId, EpisodeNumber = i + 1, SourceEpisodeId = episodeIds[i], ChildDeckId = ChildDeckId,
                    Title = $"第{i + 1}話", CharCount = 3
                });
            }

            return await db.SaveChangesAsync();
        });
    }

    private WebNovelFetchJob FetchJob(IWebNovelSource source, IBackgroundJobClient jobs) =>
        new(ContextFactory(), new WebNovelSourceResolver([source]), jobs, NullLogger<WebNovelFetchJob>.Instance);

    [Fact]
    public async Task Sync_StableIdSource_AppendsNewEpisodesById_AcrossInsertionsAndDeletions()
    {
        await SeedNovelAsync(WebNovelProvider.Kakuyomu, "777", "a", "b", "c");
        var source = new FakeWebNovelSource(WebNovelProvider.Kakuyomu)
        {
            Toc = [Ref("a", 1), Ref("x", 2), Ref("b", 3), Ref("d", 4)]
        };
        var jobs = new Mock<IBackgroundJobClient>();

        await FetchJob(source, jobs.Object).Sync(WebNovelProvider.Kakuyomu, ParentDeckId);

        source.FetchedEpisodeIds.Should().Equal("x", "d");

        var ledger = await InDbAsync(db => db.WebNovelChapters.Where(c => c.DeckId == ParentDeckId)
                                             .OrderBy(c => c.EpisodeNumber)
                                             .Select(c => new { c.EpisodeNumber, c.SourceEpisodeId, c.ChildDeckId })
                                             .ToListAsync());
        ledger.Select(c => (c.EpisodeNumber, c.SourceEpisodeId))
              .Should().Equal((1, "a"), (2, "b"), (3, "c"), (4, "x"), (5, "d"));
        ledger.Should().OnlyContain(c => c.ChildDeckId == ChildDeckId);

        var rawText = await InDbAsync(db => db.DeckRawTexts.Where(t => t.DeckId == ChildDeckId).Select(t => t.RawText).SingleAsync());
        rawText.Should().Be("本文a\n\n本文b\n\n本文c\n\n本文x\n\n本文d");

        var tracked = await InDbAsync(db => db.WebNovelSources.SingleAsync(s => s.DeckId == ParentDeckId));
        tracked.LastEpisodeCount.Should().Be(5);
        tracked.ConsecutiveFailures.Should().Be(0);
        tracked.LastError.Should().BeNull();

        jobs.Verify(j => j.Create(It.Is<Job>(job => job.Method.Name == nameof(ParseNewSubdecksJob.ParseNewSubdecks)),
                                  It.IsAny<EnqueuedState>()), Times.Once);
    }

    [Fact]
    public async Task Sync_PositionalSource_AppendsTheTailUnderItsPositions()
    {
        await SeedNovelAsync(WebNovelProvider.Syosetu, "n1234ab", "1", "2", "3");
        var source = new FakeWebNovelSource(WebNovelProvider.Syosetu)
        {
            Toc = [Ref("1", 1), Ref("2", 2), Ref("3", 3), Ref("4", 4), Ref("5", 5)]
        };

        await FetchJob(source, Mock.Of<IBackgroundJobClient>()).Sync(WebNovelProvider.Syosetu, ParentDeckId);

        var ledger = await InDbAsync(db => db.WebNovelChapters.Where(c => c.DeckId == ParentDeckId)
                                             .OrderBy(c => c.EpisodeNumber)
                                             .Select(c => new { c.EpisodeNumber, c.SourceEpisodeId })
                                             .ToListAsync());
        ledger.Select(c => (c.EpisodeNumber, c.SourceEpisodeId))
              .Should().Equal((1, "1"), (2, "2"), (3, "3"), (4, "4"), (5, "5"));
    }

    [Fact]
    public async Task Sync_PositionalSource_WithDeletedEpisodes_RecordsTheFailureAndAppendsNothing()
    {
        await SeedNovelAsync(WebNovelProvider.Syosetu, "n1234ab", "1", "2", "3");
        var source = new FakeWebNovelSource(WebNovelProvider.Syosetu) { Toc = [Ref("1", 1), Ref("2", 2)] };

        var act = () => FetchJob(source, Mock.Of<IBackgroundJobClient>()).Sync(WebNovelProvider.Syosetu, ParentDeckId);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var tracked = await InDbAsync(db => db.WebNovelSources.SingleAsync(s => s.DeckId == ParentDeckId));
        tracked.ConsecutiveFailures.Should().Be(1);
        tracked.LastError.Should().Contain("deleted or renumbered");
        (await InDbAsync(db => db.WebNovelChapters.CountAsync(c => c.DeckId == ParentDeckId))).Should().Be(3);
        source.FetchedEpisodeIds.Should().BeEmpty();
    }

    [Fact]
    public async Task RebuildSubdeck_RefetchesByEpisodeId_AndSkipsEpisodesGoneFromTheSource()
    {
        await SeedNovelAsync(WebNovelProvider.Kakuyomu, "777", "a", "b", "c");
        var source = new FakeWebNovelSource(WebNovelProvider.Kakuyomu) { Toc = [Ref("c", 1), Ref("a", 2)] };

        await FetchJob(source, Mock.Of<IBackgroundJobClient>()).RebuildSubdeck(WebNovelProvider.Kakuyomu, ParentDeckId, ChildDeckId);

        source.FetchedEpisodeIds.Should().Equal("a", "c");
        var rawText = await InDbAsync(db => db.DeckRawTexts.Where(t => t.DeckId == ChildDeckId).Select(t => t.RawText).SingleAsync());
        rawText.Should().Be("本文a\n\n本文c");
    }

    [Fact]
    public async Task SweepProvider_PollsEachNovel_OneFailureDoesNotStopTheRest()
    {
        await InDbAsync(async db =>
        {
            foreach (var (deckId, sourceId) in new[] { (9201, "gone"), (9202, "broken"), (9203, "busy"), (9204, "shrunk") })
            {
                db.Decks.Add(new Deck { DeckId = deckId, OriginalTitle = $"novel {sourceId}", MediaType = MediaType.WebNovel });
                db.WebNovelSources.Add(new WebNovelSource
                {
                    DeckId = deckId, Provider = WebNovelProvider.Kakuyomu, SourceId = sourceId, LastEpisodeCount = 10,
                    NextCheckAt = DateTimeOffset.UtcNow.AddMinutes(-1), LastSyncedAt = DateTimeOffset.UtcNow
                });
            }

            return await db.SaveChangesAsync();
        });

        var source = new FakeWebNovelSource(WebNovelProvider.Kakuyomu);
        source.InfoFailures["gone"] = new HttpRequestException("404", null, HttpStatusCode.NotFound);
        source.InfoFailures["broken"] = new InvalidOperationException("markup changed");
        source.Infos["busy"] = new WebNovelInfo { Provider = WebNovelProvider.Kakuyomu, SourceId = "busy", EpisodeCount = 30 };
        source.Infos["shrunk"] = new WebNovelInfo { Provider = WebNovelProvider.Kakuyomu, SourceId = "shrunk", EpisodeCount = 8 };

        var jobs = new Mock<IBackgroundJobClient>();
        var sweep = new WebNovelSyncSweepJob(ContextFactory(), new WebNovelSourceResolver([source]), jobs.Object,
                                             NullLogger<WebNovelSyncSweepJob>.Instance);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            var due = await db.WebNovelSources.Where(s => s.Provider == WebNovelProvider.Kakuyomu).ToListAsync();

            (await sweep.PollAsync(WebNovelProvider.Kakuyomu, due)).Should().Be(1);
            await db.SaveChangesAsync();
        }

        var states = await InDbAsync(db => db.WebNovelSources.ToDictionaryAsync(s => s.SourceId));
        states["gone"].LastError.Should().Be("Not found at the source.");
        states["gone"].ConsecutiveFailures.Should().Be(1);
        states["broken"].LastError.Should().Be("markup changed");
        states["shrunk"].ConsecutiveFailures.Should().Be(0, "a stable-id source may delete episodes without breaking the ledger");
        states["shrunk"].LastError.Should().BeNull();

        jobs.Verify(j => j.Create(It.Is<Job>(job => job.Method.Name == nameof(WebNovelFetchJob.Sync) &&
                                                    (int)job.Args[1] == 9203 &&
                                                    (WebNovelProvider)job.Args[0] == WebNovelProvider.Kakuyomu),
                                  It.IsAny<EnqueuedState>()), Times.Once);
        jobs.Verify(j => j.Create(It.Is<Job>(job => job.Method.Name == nameof(WebNovelFetchJob.Sync)), It.IsAny<IState>()),
                    Times.Once);
    }

    [Fact]
    public void Sweep_HandsEachEnabledProviderToItsOwnPoll()
    {
        var jobs = new Mock<IBackgroundJobClient>();
        var sweep = new WebNovelSyncSweepJob(ContextFactory(),
                                             new WebNovelSourceResolver([new FakeWebNovelSource(WebNovelProvider.Kakuyomu)]),
                                             jobs.Object, NullLogger<WebNovelSyncSweepJob>.Instance);

        sweep.QueueProviderPolls([(WebNovelProvider.Kakuyomu, 3), (WebNovelProvider.Hameln, 2)]);

        jobs.Verify(j => j.Create(It.Is<Job>(job => job.Method.Name == nameof(WebNovelSyncSweepJob.SweepProvider) &&
                                                    (WebNovelProvider)job.Args[0] == WebNovelProvider.Kakuyomu),
                                  It.IsAny<IState>()), Times.Once);
        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);
    }
}
