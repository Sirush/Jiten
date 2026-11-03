using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Jiten.Api.Jobs;
using Jiten.Core.Data;
using Jiten.Core.Data.Providers;
using Jiten.Core.Data.WebNovel;
using Jiten.Core.WebNovel;
using Moq;

namespace Jiten.Tests;

public class WebNovelUrlParserTests
{
    [Theory]
    [InlineData("https://ncode.syosetu.com/n9669bk/", WebNovelProvider.Syosetu, "n9669bk")]
    [InlineData("https://ncode.syosetu.com/N9669BK/12/", WebNovelProvider.Syosetu, "n9669bk")]
    [InlineData("n9669bk", WebNovelProvider.Syosetu, "n9669bk")]
    [InlineData("https://novel18.syosetu.com/n1234ab/", WebNovelProvider.SyosetuNovel18, "n1234ab")]
    [InlineData("https://kakuyomu.jp/works/16816452219192161720", WebNovelProvider.Kakuyomu, "16816452219192161720")]
    [InlineData("https://kakuyomu.jp/works/16816452219192161720/", WebNovelProvider.Kakuyomu, "16816452219192161720")]
    [InlineData("https://kakuyomu.jp/works/1177354054891139802/episodes/1177354054891141560", WebNovelProvider.Kakuyomu, "1177354054891139802")]
    [InlineData("https://kakuyomu.jp/works/1177354054891139802?utm=x", WebNovelProvider.Kakuyomu, "1177354054891139802")]
    public void ParsesWorkUrls(string input, WebNovelProvider provider, string sourceId)
    {
        WebNovelUrlParser.TryParse(input, out var parsedProvider, out var parsedId).Should().BeTrue();
        parsedProvider.Should().Be(provider);
        parsedId.Should().Be(sourceId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("16816452219192161720")]
    [InlineData("https://kakuyomu.jp/publication/entry/2024052301")]
    [InlineData("https://kakuyomu.jp/works/")]
    [InlineData("https://kakuyomu.jp/works/abc")]
    [InlineData("https://kakuyomu.jp/users/kinokoinusuki")]
    [InlineData("https://syosetu.com/")]
    [InlineData("https://example.com/works/16816452219192161720")]
    public void RejectsNonWorkInput(string input)
    {
        WebNovelUrlParser.TryParse(input, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void KakuyomuBookPage_IsAnIndirectLink()
    {
        const string url = "https://kakuyomu.jp/publication/entry/2024052301";

        WebNovelUrlParser.TryMatchIndirect(url, out var provider).Should().BeTrue();
        provider.Should().Be(WebNovelProvider.Kakuyomu);
        WebNovelUrlParser.IsWebNovelLink(url, out _).Should().BeTrue();
    }

    [Fact]
    public void BareNcode_IsNotALink()
    {
        WebNovelUrlParser.IsWebNovelLink("n9669bk", out _).Should().BeFalse();
    }

    [Fact]
    public void FirstWorkLink_SkipsOtherSitesAndNonWorkUrls()
    {
        var links = new[]
        {
            new Link { LinkType = LinkType.Anilist, Url = "https://anilist.co/manga/1" },
            new Link { LinkType = LinkType.Kakuyomu, Url = "https://kakuyomu.jp/users/someone" },
            new Link { LinkType = LinkType.Kakuyomu, Url = "https://kakuyomu.jp/works/16816452219192161720" }
        };

        WebNovelUrlParser.FirstWorkLink(links).Should().Be(new WebNovelWorkRef(WebNovelProvider.Kakuyomu, "16816452219192161720"));
    }
}

public class WebNovelProviderRegistryTests
{
    public static TheoryData<WebNovelProvider> Providers()
    {
        var data = new TheoryData<WebNovelProvider>();
        foreach (var descriptor in WebNovelProviders.All)
            data.Add(descriptor.Provider);
        return data;
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void BuiltWorkUrl_ParsesBackToTheSameWork(WebNovelProvider provider)
    {
        var descriptor = WebNovelProviders.Get(provider);
        var sampleId = provider == WebNovelProvider.Kakuyomu ? "16816452219192161720" : "n9669bk";

        var url = descriptor.BuildWorkUrl(sampleId);

        WebNovelUrlParser.TryParse(url, out var parsedProvider, out var parsedId).Should().BeTrue();
        parsedProvider.Should().Be(provider);
        parsedId.Should().Be(sampleId);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void WorkUrl_IsDetectedAsTheProvidersLinkType(WebNovelProvider provider)
    {
        var descriptor = WebNovelProviders.Get(provider);
        var url = descriptor.BuildWorkUrl(provider == WebNovelProvider.Kakuyomu ? "16816452219192161720" : "n9669bk");

        ExternalUrlParser.DetectLinkType(new Uri(url)).Should().Be(descriptor.LinkType);
        WebNovelProviders.IsWebNovelLinkType(descriptor.LinkType).Should().BeTrue();
    }

    [Fact]
    public void QueueNames_AreValidHangfireQueues()
    {
        WebNovelQueues.All.Should().OnlyContain(q => Regex.IsMatch(q, "^[a-z0-9_-]+$"));
        WebNovelQueues.All.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void SyosetuQueues_KeepTheirNames()
    {
        WebNovelQueues.For(WebNovelProviders.Get(WebNovelProvider.Syosetu).Site, WebNovelQueueKind.Fetch).Should().Be("webnovel-syosetu");
        WebNovelQueues.For(WebNovelProviders.Get(WebNovelProvider.SyosetuNovel18).Site, WebNovelQueueKind.Fetch).Should().Be("webnovel-syosetu");
        WebNovelQueues.For(WebNovelProviders.Get(WebNovelProvider.Syosetu).Site, WebNovelQueueKind.Metadata).Should().Be("webnovel-syosetu-metadata");
    }

    [Fact]
    public void ProvidersWithIndirectLinks_HaveSourcesThatResolveThem()
    {
        var sources = new IWebNovelSource[]
        {
            new SyosetuSource(Mock.Of<IHttpClientFactory>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SyosetuSource>.Instance),
            new KakuyomuSource(Mock.Of<IHttpClientFactory>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<KakuyomuSource>.Instance)
        };

        foreach (var descriptor in WebNovelProviders.All.Where(d => d.IsIndirectWorkUrl != null))
            sources.Single(s => s.Provider == descriptor.Provider).Should().BeAssignableTo<IWebNovelLinkResolver>();
    }

    [Fact]
    public void FetchEstimate_IncludesSyosetusPauseSteps()
    {
        WebNovelProviders.Syosetu.Politeness.EstimateSeconds(100).Should().BeApproximately(100 * 0.7 + 10 * 5, 0.001);
        WebNovelProviders.Kakuyomu.Politeness.EstimateSeconds(100).Should().BeApproximately(100, 0.001);
    }
}

public class WebNovelQueueRoutingTests
{
    /// <summary>Runs the real Hangfire client pipeline against a fake storage and reports the queue the job lands on.</summary>
    private static string? EnqueuedQueue(Action<IBackgroundJobClient> enqueue)
    {
        string? queue = null;

        var transaction = new Mock<IWriteOnlyTransaction>();
        transaction.Setup(t => t.AddToQueue(It.IsAny<string>(), It.IsAny<string>()))
                   .Callback<string, string>((q, _) => queue = q);

        var connection = new Mock<IStorageConnection>();
        connection.Setup(c => c.CreateExpiredJob(It.IsAny<Job>(), It.IsAny<IDictionary<string, string>>(),
                                                 It.IsAny<DateTime>(), It.IsAny<TimeSpan>()))
                  .Returns("1");
        connection.Setup(c => c.CreateWriteTransaction()).Returns(transaction.Object);

        var storage = new Mock<JobStorage> { CallBase = true };
        storage.Setup(s => s.GetConnection()).Returns(connection.Object);

        enqueue(new BackgroundJobClient(storage.Object));
        return queue;
    }

    [Fact]
    public void FetchJobs_LandOnTheirSitesFetchQueue()
    {
        EnqueuedQueue(c => c.Enqueue<WebNovelFetchJob>(j => j.Sync(WebNovelProvider.Kakuyomu, 1)))
            .Should().Be("webnovel-kakuyomu");
        EnqueuedQueue(c => c.Enqueue<WebNovelFetchJob>(j => j.RebuildSubdeck(WebNovelProvider.SyosetuNovel18, 1, 2)))
            .Should().Be("webnovel-syosetu");
        EnqueuedQueue(c => c.Enqueue<WebNovelImportJob>(j => j.Import(WebNovelProvider.Kakuyomu, "1", null, null, null)))
            .Should().Be("webnovel-kakuyomu");
    }

    [Fact]
    public void MetadataJobs_LandOnTheirSitesMetadataQueue()
    {
        EnqueuedQueue(c => c.Enqueue<WebNovelSyncSweepJob>(j => j.SweepProvider(WebNovelProvider.Kakuyomu)))
            .Should().Be("webnovel-kakuyomu-metadata");
        EnqueuedQueue(c => c.Enqueue<FetchMetadataJob>(j => j.FetchWebNovelMetadata(WebNovelProvider.Syosetu, 1)))
            .Should().Be("webnovel-syosetu-metadata");
        EnqueuedQueue(c => c.Enqueue<MediaRequestMetadataJob>(j => j.FetchWebNovel(WebNovelProvider.Kakuyomu, 1, "u")))
            .Should().Be("webnovel-kakuyomu-metadata");
    }

    [Fact]
    public void EveryRoutedJob_TakesAProvider()
    {
        var routed = typeof(WebNovelQueueAttribute).Assembly.GetTypes()
                                                   .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                                                   .Where(m => m.GetCustomAttribute<WebNovelQueueAttribute>() != null)
                                                   .ToList();

        routed.Should().HaveCountGreaterThanOrEqualTo(6);
        routed.Should().OnlyContain(m => m.GetParameters().Any(p => p.ParameterType == typeof(WebNovelProvider)));
        routed.Should().OnlyContain(m => m.GetCustomAttribute<QueueAttribute>() == null);
    }
}

public class WebNovelLedgerTests
{
    private static WebNovelEpisodeRef Ref(string id, int position, DateTimeOffset? updated = null) =>
        new() { SourceEpisodeId = id, Number = position, Title = $"ep {id}", UpdatedAt = updated };

    private static WebNovelChapter Chapter(string id, int number, DateTimeOffset? updated = null) =>
        new() { SourceEpisodeId = id, EpisodeNumber = number, SourceUpdatedAt = updated };

    [Fact]
    public void EmptyLedger_NumbersEveryEpisodeFromOne()
    {
        var diff = WebNovelLedger.Diff([], [Ref("a", 1), Ref("b", 2)], stableEpisodeIds: true);

        diff.NewEpisodes.Select(e => (e.Reference.SourceEpisodeId, e.Number)).Should().Equal(("a", 1), ("b", 2));
    }

    [Fact]
    public void PositionalSource_AppendsTheTail()
    {
        var ledger = new[] { Chapter("1", 1), Chapter("2", 2), Chapter("3", 3) };

        var diff = WebNovelLedger.Diff(ledger, [Ref("1", 1), Ref("2", 2), Ref("3", 3), Ref("4", 4), Ref("5", 5)],
                                       stableEpisodeIds: false);

        diff.NewEpisodes.Select(e => (e.Reference.SourceEpisodeId, e.Number)).Should().Equal(("4", 4), ("5", 5));
        diff.MissingAtSource.Should().Be(0);
    }

    [Fact]
    public void PositionalSource_ShrinkingTocStops()
    {
        var ledger = new[] { Chapter("1", 1), Chapter("2", 2), Chapter("3", 3) };

        var act = () => WebNovelLedger.Diff(ledger, [Ref("1", 1), Ref("2", 2)], stableEpisodeIds: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*deleted or renumbered*");
    }

    [Fact]
    public void StableSource_ToleratesDeletionsAndAppendsInsertionsAfterTheIngestedRange()
    {
        var ledger = new[] { Chapter("a", 1), Chapter("b", 2), Chapter("c", 3) };

        var diff = WebNovelLedger.Diff(ledger, [Ref("a", 1), Ref("x", 2), Ref("b", 3), Ref("d", 4)], stableEpisodeIds: true);

        diff.NewEpisodes.Select(e => (e.Reference.SourceEpisodeId, e.Number)).Should().Equal(("x", 4), ("d", 5));
        diff.MissingAtSource.Should().Be(1);
        diff.InsertedBeforeIngested.Should().Be(1);
    }

    [Fact]
    public void NewNumbers_FollowTheHighestIngestedNumber()
    {
        var ledger = new[] { Chapter("a", 1), Chapter("c", 3) };

        var diff = WebNovelLedger.Diff(ledger, [Ref("a", 1), Ref("c", 2), Ref("e", 3)], stableEpisodeIds: true);

        diff.NewEpisodes.Single().Number.Should().Be(4);
    }

    [Fact]
    public void DuplicateOrEmptyIds_Throw()
    {
        var duplicate = () => WebNovelLedger.Diff([], [Ref("a", 1), Ref("a", 2)], stableEpisodeIds: true);
        var empty = () => WebNovelLedger.Diff([], [Ref("", 1)], stableEpisodeIds: true);

        duplicate.Should().Throw<InvalidOperationException>().WithMessage("*twice*");
        empty.Should().Throw<InvalidOperationException>().WithMessage("*without an id*");
    }

    [Fact]
    public void MarkRevisions_MatchesById_AndIgnoresSourcesWithoutRevisionTimes()
    {
        var day1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ledger = new[] { Chapter("a", 1, day1), Chapter("b", 2, day1), Chapter("c", 3, null) };

        var revised = WebNovelLedger.MarkRevisions(ledger, [Ref("b", 1, day1), Ref("a", 2, day1.AddDays(1)), Ref("c", 3, day1)]);

        revised.Should().Be(1);
        ledger.Select(c => c.RevisedAtSource).Should().Equal(true, false, false);
    }
}
