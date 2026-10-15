using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
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

public class RequestDuplicateTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private const string AnilistUrl = "https://anilist.co/anime/9253";
    private const string MalUrl = "https://myanimelist.net/anime/9253";
    private const string VndbUrl = "https://vndb.org/v2002";
    private const int DeckId = 9400;

    private readonly HttpClient _client = factory.CreateClient();
    private readonly Dictionary<string, Metadata> _providerData = new();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static Metadata SteinsGate(params Link[] links) => new()
    {
        OriginalTitle = "シュタインズ・ゲート",
        RomajiTitle = "Steins;Gate",
        Aliases = ["SG", "命運探知"],
        Links = links.ToList()
    };

    private async Task<int> CreateRequest(string title, string? url, MediaType mediaType = MediaType.Anime, string user = TestUsers.UserA)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/requests")
            .WithUser(user)
            .WithJsonContent(new { title, mediaType = (int)mediaType, externalUrl = url }));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    /// <summary>Runs the lookup job against canned provider data instead of the network.</summary>
    private async Task Fetch(int requestId, string url)
    {
        var resolver = new Mock<IExternalMetadataResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<MediaType?>()))
                .ReturnsAsync((string u, MediaType? _) => _providerData.GetValueOrDefault(u));
        var job = new MediaRequestMetadataJob(factory.Services.GetRequiredService<IDbContextFactory<JitenDbContext>>(), resolver.Object,
            NullLogger<MediaRequestMetadataJob>.Instance);
        await job.FetchSyosetu(requestId, url);
    }

    private async Task<JsonElement> Get(int id)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/requests/{id}").WithUser(TestUsers.UserB));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static int? DuplicateOf(JsonElement dto) =>
        dto.GetProperty("possibleDuplicateOfRequestId").ValueKind == JsonValueKind.Null ? null : dto.GetProperty("possibleDuplicateOfRequestId").GetInt32();

    [Fact]
    public async Task NewerRequest_NamedDifferently_IsFlaggedOnceItsLookupShowsTheSameTitle()
    {
        _providerData[AnilistUrl] = SteinsGate();
        _providerData[VndbUrl] = new Metadata { OriginalTitle = "シュタインズ・ゲート", EnglishTitle = "Steins;Gate" };

        var older = await CreateRequest("Steins Gate anime", AnilistUrl);
        await Fetch(older, AnilistUrl);
        var newer = await CreateRequest("STEINS GATE", VndbUrl);
        await Fetch(newer, VndbUrl);

        DuplicateOf(await Get(newer)).Should().Be(older);
        (await Get(newer)).GetProperty("possibleDuplicateOfTitle").GetString().Should().Be("Steins Gate anime");
        DuplicateOf(await Get(older)).Should().BeNull();
    }

    [Fact]
    public async Task WhenTheOlderRequestIsReadLast_ItFlagsTheNewerOne()
    {
        _providerData[AnilistUrl] = SteinsGate();

        var older = await CreateRequest("シュタインズ・ゲート", null);
        var newer = await CreateRequest("Some name", AnilistUrl);
        await Fetch(newer, AnilistUrl);

        DuplicateOf(await Get(newer)).Should().Be(older);
    }

    [Fact]
    public async Task ProviderCrossLinks_MatchAcrossCatalogues_WithoutSharedTitles()
    {
        _providerData[AnilistUrl] = new Metadata { OriginalTitle = "Title A", Links = [new Link { LinkType = LinkType.Mal, Url = MalUrl }] };
        _providerData[MalUrl] = new Metadata { OriginalTitle = "Completely different" };

        var older = await CreateRequest("first", AnilistUrl);
        await Fetch(older, AnilistUrl);
        var newer = await CreateRequest("second", MalUrl);
        await Fetch(newer, MalUrl);

        DuplicateOf(await Get(newer)).Should().Be(older);
    }

    [Fact]
    public async Task ShortLatinAliases_AndOtherMediaTypes_DoNotCount()
    {
        _providerData[AnilistUrl] = SteinsGate();
        _providerData[VndbUrl] = new Metadata { OriginalTitle = "別の作品", Aliases = ["SG"] };

        var animeRequest = await CreateRequest("Steins;Gate", AnilistUrl);
        await Fetch(animeRequest, AnilistUrl);
        var abbreviationOnly = await CreateRequest("Another work", VndbUrl);
        await Fetch(abbreviationOnly, VndbUrl);
        var manga = await CreateRequest("Steins;Gate", "https://anilist.co/manga/1", MediaType.Manga);
        _providerData["https://anilist.co/manga/1"] = SteinsGate();
        await Fetch(manga, "https://anilist.co/manga/1");

        DuplicateOf(await Get(abbreviationOnly)).Should().BeNull();
        DuplicateOf(await Get(manga)).Should().BeNull();
    }

    [Fact]
    public async Task ExistingDeck_WithTheSameName_IsReported()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            db.Decks.Add(new Deck
            {
                DeckId = DeckId, OriginalTitle = "シュタインズ・ゲート", MediaType = MediaType.Anime,
                Titles = [new DeckTitle { DeckId = DeckId, Title = "シュタインズ・ゲート", TitleType = DeckTitleType.Original }]
            });
            await db.SaveChangesAsync();
        }

        _providerData[AnilistUrl] = SteinsGate();
        var id = await CreateRequest("please add", AnilistUrl);
        await Fetch(id, AnilistUrl);

        var dto = await Get(id);
        dto.GetProperty("possibleDuplicateDeckId").GetInt32().Should().Be(DeckId);
        dto.GetProperty("possibleDuplicateDeckTitle").GetString().Should().Be("シュタインズ・ゲート");
    }

    [Fact]
    public async Task DismissedFlag_StaysCleared_WhenTheSameLinkIsReadAgain()
    {
        _providerData[AnilistUrl] = SteinsGate();
        _providerData[VndbUrl] = new Metadata { OriginalTitle = "シュタインズ・ゲート" };
        var older = await CreateRequest("a", AnilistUrl);
        await Fetch(older, AnilistUrl);
        var newer = await CreateRequest("b", VndbUrl);
        await Fetch(newer, VndbUrl);
        DuplicateOf(await Get(newer)).Should().Be(older);

        var asUser = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/requests/{newer}/dismiss-duplicate").WithUser(TestUsers.UserA));
        asUser.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var dismiss = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/requests/{newer}/dismiss-duplicate").WithAdmin());
        dismiss.StatusCode.Should().Be(HttpStatusCode.OK);

        await Fetch(newer, VndbUrl);
        DuplicateOf(await Get(newer)).Should().BeNull();
    }

    [Fact]
    public async Task ClosedOriginal_IsNoLongerReported()
    {
        _providerData[AnilistUrl] = SteinsGate();
        _providerData[VndbUrl] = new Metadata { OriginalTitle = "シュタインズ・ゲート" };
        var older = await CreateRequest("a", AnilistUrl);
        await Fetch(older, AnilistUrl);
        var newer = await CreateRequest("b", VndbUrl);
        await Fetch(newer, VndbUrl);

        var reject = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{older}/status")
            .WithAdmin()
            .WithJsonContent(new { status = (int)MediaRequestStatus.Rejected, adminNote = "Out of scope" }));
        reject.StatusCode.Should().Be(HttpStatusCode.OK);

        DuplicateOf(await Get(newer)).Should().BeNull();
    }

    [Fact]
    public async Task DuplicateCheck_FindsARequest_ByAProviderAlias()
    {
        _providerData[AnilistUrl] = SteinsGate();
        var id = await CreateRequest("Steins Gate anime", AnilistUrl);
        await Fetch(id, AnilistUrl);

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get,
                $"/api/requests/duplicate-check?title={Uri.EscapeDataString("命運探知")}&mediaType={(int)MediaType.Anime}")
            .WithUser(TestUsers.UserB));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var match = body.GetProperty("existingRequests").EnumerateArray().Should().ContainSingle().Subject;
        match.GetProperty("id").GetInt32().Should().Be(id);
        match.GetProperty("isExactMatch").GetBoolean().Should().BeTrue();
    }
}
