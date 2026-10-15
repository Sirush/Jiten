using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Providers;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jiten.Parser.Tests.Integration;

public class RequestMetadataTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private const string AnilistUrl = "https://anilist.co/manga/30002";
    private const string VndbUrl = "https://vndb.org/v17";
    private const int TagIsekai = 9101;
    private const int TagTimeTravel = 9102;

    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        Jobs.Invocations.Clear();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Mock<IBackgroundJobClient> Jobs => Mock.Get(factory.Services.GetRequiredService<IBackgroundJobClient>());

    private List<Job> QueuedFetches() =>
        Jobs.Invocations
            .Where(i => i.Method.Name == nameof(IBackgroundJobClient.Create))
            .Select(i => (Job)i.Arguments[0])
            .Where(j => j.Type == typeof(MediaRequestMetadataJob))
            .ToList();

    private async Task<int> CreateRequest(string? url, string user = TestUsers.UserA, string title = "Request")
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/requests")
            .WithUser(user)
            .WithJsonContent(new { title, mediaType = (int)MediaType.Manga, externalUrl = url }));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<JsonElement> GetRequest(int id)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/requests/{id}").WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task SeedMappings()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        if (await db.Tags.AnyAsync(t => t.TagId == TagIsekai))
            return;

        db.Tags.AddRange(new Tag { TagId = TagIsekai, Name = "Isekai" }, new Tag { TagId = TagTimeTravel, Name = "Time Travel" });
        db.ExternalGenreMappings.AddRange(
            new ExternalGenreMapping { Provider = LinkType.Anilist, ExternalGenreName = "Romance", JitenGenre = Genre.Romance },
            new ExternalGenreMapping { Provider = LinkType.Anilist, ExternalGenreName = "Sci-Fi", JitenGenre = Genre.SciFi });
        db.ExternalTagMappings.AddRange(
            new ExternalTagMapping { Provider = LinkType.Anilist, ExternalTagName = "Isekai", TagId = TagIsekai },
            new ExternalTagMapping { Provider = LinkType.Anilist, ExternalTagName = "Time Manipulation", TagId = TagTimeTravel });
        await db.SaveChangesAsync();
    }

    private async Task ApplyMetadata(int requestId, Metadata metadata)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var request = await db.MediaRequests.Include(r => r.Genres).Include(r => r.Tags).FirstAsync(r => r.Id == requestId);
        await MediaRequestMetadataService.ApplyAsync(db, request, metadata, LinkType.Anilist);
        await db.SaveChangesAsync();
    }

    private static Metadata SteinsGate() => new()
    {
        OriginalTitle = "シュタインズ・ゲート",
        RomajiTitle = "Steins;Gate",
        EnglishTitle = "steins;gate",
        ReleaseDate = new DateTime(2009, 10, 15),
        Genres = ["Sci-Fi", "Unmapped"],
        Tags = [new MetadataTag { Name = "Time Manipulation", Percentage = 90 }]
    };

    [Fact]
    public async Task Create_WithProviderLink_IsPendingAndQueuesOnTheProvidersQueue()
    {
        var id = await CreateRequest(AnilistUrl);

        (await GetRequest(id)).GetProperty("metadataStatus").GetInt32().Should().Be((int)MediaRequestMetadataStatus.Pending);
        var job = QueuedFetches().Should().ContainSingle().Subject;
        job.Method.Name.Should().Be(nameof(MediaRequestMetadataJob.FetchAnilist));
        job.Args.Should().Equal(id, AnilistUrl);
    }

    [Theory]
    [InlineData("https://example.com/some-book", MediaRequestMetadataStatus.Unsupported)]
    [InlineData(null, MediaRequestMetadataStatus.None)]
    public async Task Create_WithoutReadableLink_QueuesNothing(string? url, MediaRequestMetadataStatus expected)
    {
        var id = await CreateRequest(url);

        (await GetRequest(id)).GetProperty("metadataStatus").GetInt32().Should().Be((int)expected);
        QueuedFetches().Should().BeEmpty();
    }

    [Fact]
    public async Task AppliedMetadata_KeepsTheRequestersTitle_AndDropsDuplicateTitles()
    {
        await SeedMappings();
        var id = await CreateRequest(AnilistUrl, title: "my title");
        await ApplyMetadata(id, SteinsGate());

        var dto = await GetRequest(id);
        dto.GetProperty("title").GetString().Should().Be("my title");
        dto.GetProperty("originalTitle").GetString().Should().Be("シュタインズ・ゲート");
        dto.GetProperty("romajiTitle").GetString().Should().Be("Steins;Gate");
        dto.GetProperty("englishTitle").ValueKind.Should().Be(JsonValueKind.Null);
        dto.GetProperty("releaseDate").GetString().Should().Be("2009-10-15");
        dto.GetProperty("metadataStatus").GetInt32().Should().Be((int)MediaRequestMetadataStatus.Fetched);
        dto.GetProperty("genres").EnumerateArray().Select(g => g.GetInt32()).Should().Equal((int)Genre.SciFi);
        var tag = dto.GetProperty("tags").EnumerateArray().Should().ContainSingle().Subject;
        tag.GetProperty("tagId").GetInt32().Should().Be(TagTimeTravel);
        tag.GetProperty("percentage").GetInt32().Should().Be(90);
    }

    [Fact]
    public async Task Refetch_ReplacesGenresAndTags()
    {
        await SeedMappings();
        var id = await CreateRequest(AnilistUrl);
        await ApplyMetadata(id, SteinsGate());

        await ApplyMetadata(id, new Metadata
        {
            OriginalTitle = "シュタインズ・ゲート",
            Genres = ["Romance", "Sci-Fi"],
            Tags = [new MetadataTag { Name = "Isekai", Percentage = 40 }, new MetadataTag { Name = "Time Manipulation", Percentage = 70 }]
        });

        var dto = await GetRequest(id);
        dto.GetProperty("genres").EnumerateArray().Select(g => g.GetInt32()).Should().BeEquivalentTo([(int)Genre.Romance, (int)Genre.SciFi]);
        dto.GetProperty("tags").EnumerateArray().Select(t => (t.GetProperty("tagId").GetInt32(), t.GetProperty("percentage").GetInt32()))
           .Should().Equal((TagTimeTravel, 70), (TagIsekai, 40));
        dto.GetProperty("romajiTitle").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task OwnerChangingTheLink_ClearsOldMetadata_AndQueuesTheNewLink()
    {
        await SeedMappings();
        var id = await CreateRequest(AnilistUrl);
        await ApplyMetadata(id, SteinsGate());
        Jobs.Invocations.Clear();

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/edit-description")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { description = "d", externalUrl = VndbUrl }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await GetRequest(id);
        dto.GetProperty("metadataStatus").GetInt32().Should().Be((int)MediaRequestMetadataStatus.Pending);
        dto.GetProperty("originalTitle").ValueKind.Should().Be(JsonValueKind.Null);
        dto.GetProperty("genres").GetArrayLength().Should().Be(0);
        dto.GetProperty("tags").GetArrayLength().Should().Be(0);
        QueuedFetches().Should().ContainSingle().Which.Method.Name.Should().Be(nameof(MediaRequestMetadataJob.FetchVndb));
    }

    [Fact]
    public async Task OwnerEditingOnlyTheDescription_KeepsMetadata_AndQueuesNothing()
    {
        await SeedMappings();
        var id = await CreateRequest(AnilistUrl);
        await ApplyMetadata(id, SteinsGate());
        Jobs.Invocations.Clear();

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/edit-description")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { description = "new description", externalUrl = AnilistUrl }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await GetRequest(id);
        dto.GetProperty("metadataStatus").GetInt32().Should().Be((int)MediaRequestMetadataStatus.Fetched);
        dto.GetProperty("genres").GetArrayLength().Should().Be(1);
        QueuedFetches().Should().BeEmpty();
    }

    [Fact]
    public async Task OwnerLinkChanges_AreCappedPerHour()
    {
        var id = await CreateRequest(null, TestUsers.UserB);

        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 1; i <= MediaRequestMetadataService.RequesterFetchesPerHour + 1; i++)
        {
            var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/edit-description")
                .WithUser(TestUsers.UserB)
                .WithJsonContent(new { externalUrl = $"https://anilist.co/manga/{i}" }));
            last = response.StatusCode;
            if (i <= MediaRequestMetadataService.RequesterFetchesPerHour)
                last.Should().Be(HttpStatusCode.OK);
        }

        last.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Refetch_IsAdminOnly_AndQueuesEvenForAnUnchangedLink()
    {
        var id = await CreateRequest(AnilistUrl);
        Jobs.Invocations.Clear();

        var asUser = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/requests/{id}/refetch-metadata").WithUser(TestUsers.UserA));
        asUser.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var asAdmin = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/requests/{id}/refetch-metadata").WithAdmin());
        asAdmin.StatusCode.Should().Be(HttpStatusCode.OK);
        QueuedFetches().Should().ContainSingle();
    }

    [Fact]
    public async Task Backfill_QueuesOnlyRequestsNotYetFetched()
    {
        await SeedMappings();
        var fetched = await CreateRequest(AnilistUrl, title: "a");
        await ApplyMetadata(fetched, SteinsGate());
        var pending = await CreateRequest(VndbUrl, title: "b");
        var unsupported = await CreateRequest("https://example.com/x", title: "c");
        await CreateRequest(null, title: "d");
        Jobs.Invocations.Clear();

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/admin/requests/backfill-metadata").WithAdmin());
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("queued").GetInt32().Should().Be(1);
        body.GetProperty("unsupported").GetInt32().Should().Be(1);
        QueuedFetches().Should().ContainSingle().Which.Args.Should().Equal(pending, VndbUrl);
        (await GetRequest(unsupported)).GetProperty("metadataStatus").GetInt32().Should().Be((int)MediaRequestMetadataStatus.Unsupported);
    }

    [Fact]
    public async Task GenreAndTagFilters_AndFacets_FollowTheMediaListRules()
    {
        await SeedMappings();
        var sciFi = await CreateRequest(AnilistUrl, title: "sci-fi");
        await ApplyMetadata(sciFi, SteinsGate());
        var romance = await CreateRequest("https://anilist.co/manga/2", title: "romance");
        await ApplyMetadata(romance, new Metadata
        {
            OriginalTitle = "恋", Genres = ["Romance"], Tags = [new MetadataTag { Name = "Isekai", Percentage = 80 }]
        });
        await CreateRequest(null, title: "bare");

        async Task<List<int>> Ids(string query)
        {
            var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/requests?{query}").WithUser(TestUsers.UserA));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return body.GetProperty("data").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ToList();
        }

        (await Ids($"genres={(int)Genre.SciFi}")).Should().Equal(sciFi);
        (await Ids($"excludeGenres={(int)Genre.SciFi}")).Should().HaveCount(2).And.NotContain(sciFi);
        (await Ids($"tags={TagIsekai}")).Should().Equal(romance);
        (await Ids($"tags={TagIsekai},{TagTimeTravel}")).Should().BeEmpty();

        var facetsResponse = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/requests/facets?tags={TagIsekai}")
            .WithUser(TestUsers.UserA));
        var facets = await facetsResponse.Content.ReadFromJsonAsync<JsonElement>();
        facets.GetProperty("genreCounts").EnumerateObject().Select(p => (p.Name, p.Value.GetInt32()))
              .Should().Equal((((int)Genre.Romance).ToString(), 1));
        facets.GetProperty("tagCounts").EnumerateObject().Select(p => (p.Name, p.Value.GetInt32()))
              .Should().Equal((TagIsekai.ToString(), 1));
    }

    [Fact]
    public async Task Job_SkipsAStaleLink_AndRecordsAMissingEntryAsFailed()
    {
        var id = await CreateRequest(AnilistUrl);
        var resolver = new Mock<IExternalMetadataResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<MediaType?>())).ReturnsAsync((Metadata?)null);
        var job = new MediaRequestMetadataJob(factory.Services.GetRequiredService<IDbContextFactory<JitenDbContext>>(), resolver.Object,
            NullLogger<MediaRequestMetadataJob>.Instance);

        await job.FetchSyosetu(id, "https://anilist.co/manga/1");
        resolver.Verify(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<MediaType?>()), Times.Never);

        await job.FetchSyosetu(id, AnilistUrl);
        (await GetRequest(id)).GetProperty("metadataStatus").GetInt32().Should().Be((int)MediaRequestMetadataStatus.Failed);
    }
}
