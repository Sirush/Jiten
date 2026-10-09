using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Services;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class MediaGroupTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int SagaSeriesId = 1;
    private const int LoneDeckId = 6;
    private const int SettingId = 3;
    private int _franchiseId;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        await SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // Line A: 1 (anime, 2000) <- 2 (anime, 2002) sequel, 1 -> 3 (novel, 2001) adaptation.
    // Line B: 4 (anime, 2005) <- 5 (game, 2006) sequel. No link joins the lines; the "Saga" series (1, 4, 5) does.
    // Deck 6 stands alone; it shares only the setting "World" with 1.
    private async Task SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        await db.DeckRelationships.ExecuteDeleteAsync();
        await db.DeckWords.ExecuteDeleteAsync();
        await db.Definitions.ExecuteDeleteAsync();
        await db.WordForms.ExecuteDeleteAsync();
        await db.JMDictWords.ExecuteDeleteAsync();

        db.Decks.AddRange(Deck(1, "Saga I", MediaType.Anime, 2000, 1000, 2f), Deck(2, "Saga II", MediaType.Anime, 2002, 3000, 4f),
                          Deck(3, "Saga Novel", MediaType.Novel, 2001, 2000, 3f), Deck(4, "Saga Gaiden", MediaType.Anime, 2005, 1000, 1f),
                          Deck(5, "Saga Gaiden Game", MediaType.VideoGame, 2006, 500, 0f), Deck(LoneDeckId, "Alone", MediaType.Anime, 2010, 100, 1f));
        await db.SaveChangesAsync();

        db.DeckRelationships.AddRange(Rel(2, 1, DeckRelationshipType.Sequel), Rel(3, 1, DeckRelationshipType.Adaptation),
                                      Rel(5, 4, DeckRelationshipType.Sequel));
        var saga = new Series { SeriesId = SagaSeriesId, OriginalTitle = "Saga", Kind = SeriesKind.Series };
        saga.Members.Add(new SeriesMember { SeriesId = SagaSeriesId, DeckId = 1 });
        saga.Members.Add(new SeriesMember { SeriesId = SagaSeriesId, DeckId = 4 });
        saga.Members.Add(new SeriesMember { SeriesId = SagaSeriesId, DeckId = 5 });
        db.Series.Add(saga);

        var world = new Series { SeriesId = SettingId, OriginalTitle = "World", Kind = SeriesKind.Setting };
        world.Members.Add(new SeriesMember { SeriesId = SettingId, DeckId = 1 });
        world.Members.Add(new SeriesMember { SeriesId = SettingId, DeckId = LoneDeckId });
        db.Series.Add(world);

        (int Deck, int Word, int Occurrences)[] rows = [(1, 1, 10), (1, 2, 5), (2, 2, 7), (2, 3, 3), (3, 4, 2), (4, 5, 20), (5, 1, 1), (6, 6, 50)];
        foreach (var (deckId, wordId, occurrences) in rows)
            db.DeckWords.Add(new DeckWord { DeckId = deckId, WordId = wordId, ReadingIndex = 0, Occurrences = occurrences });

        for (var wordId = 1; wordId <= 6; wordId++)
        {
            db.JMDictWords.Add(new JmDictWord { WordId = wordId, PartsOfSpeech = ["n"] });
            db.Definitions.Add(new JmDictDefinition { WordId = wordId, SenseIndex = 0, EnglishMeanings = [$"meaning{wordId}"], PartsOfSpeech = ["n"] });
            db.WordForms.Add(new JmDictWordForm { WordId = wordId, ReadingIndex = 0, Text = $"語{wordId}", RubyText = $"語{wordId}", FormType = JmDictFormType.KanjiForm });
        }

        await db.SaveChangesAsync();

        await scope.ServiceProvider.GetRequiredService<FranchiseSyncRunner>().RunAsync();
        _franchiseId = (await db.Decks.AsNoTracking().Where(d => d.DeckId == 1).Select(d => d.FranchiseId).SingleAsync())!.Value;
    }

    private static Deck Deck(int id, string title, MediaType mediaType, int year, int chars, float difficulty) =>
        new()
        {
            DeckId = id, OriginalTitle = title, RomajiTitle = title.ToLowerInvariant(), MediaType = mediaType,
            ReleaseDate = new DateOnly(year, 1, 1), CharacterCount = chars, Difficulty = difficulty, DifficultyOverride = -1
        };

    private static DeckRelationship Rel(int source, int target, DeckRelationshipType type) =>
        new() { SourceDeckId = source, TargetDeckId = target, RelationshipType = type };

    private async Task<HttpResponseMessage> SendAsync(string url, bool authenticated = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        if (authenticated)
            request.WithUser(TestUsers.UserA);
        return await _client.SendAsync(request);
    }

    private async Task<T> GetAsync<T>(string url, bool authenticated = false)
    {
        var response = await SendAsync(url, authenticated);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private Task<MediaGroupMembersDto> MembersAsync(int kind, int id, bool authenticated = false) =>
        GetAsync<MediaGroupMembersDto>($"/api/media-group/members?kind={kind}&id={id}", authenticated);

    private Task<MediaGroupStatsDto> StatsAsync(int kind, int id, string filters = "") =>
        GetAsync<MediaGroupStatsDto>($"/api/media-group/stats?kind={kind}&id={id}{filters}");

    private async Task<List<(int WordId, int Occurrences)>> VocabularyAsync(int kind, int id, string filters = "")
    {
        var page = await GetAsync<PaginatedResponse<DeckVocabularyListDto>>($"/api/media-group/vocabulary?kind={kind}&id={id}&sortBy=deckFreq{filters}");
        return page.Data.Words.Select(w => (w.WordId, w.Occurrences)).ToList();
    }

    [Fact]
    public async Task Members_Franchise_ListsEveryDeckInReleaseOrder()
    {
        var members = await MembersAsync(1, _franchiseId);

        members.Kind.Should().Be(MediaGroupKind.Franchise);
        members.Id.Should().Be(_franchiseId);
        members.OriginalTitle.Should().Be("Saga");
        members.FranchiseId.Should().Be(_franchiseId);
        members.Members.Select(m => m.DeckId).Should().Equal(1, 3, 2, 4, 5);
    }

    [Fact]
    public async Task Members_Series_ListsItsMembers()
    {
        var members = await MembersAsync(2, SagaSeriesId);

        members.OriginalTitle.Should().Be("Saga");
        members.FranchiseId.Should().Be(_franchiseId);
        members.Members.Select(m => m.DeckId).Should().Equal(1, 4, 5);
    }

    [Fact]
    public async Task Members_Line_FollowsStoryLinksOnly()
    {
        var line = await MembersAsync(3, 1, authenticated: true);

        line.OriginalTitle.Should().Be("Saga I");
        line.FranchiseId.Should().Be(_franchiseId);
        line.Members.Select(m => m.DeckId).Should().Equal(1, 3, 2);

        (await MembersAsync(3, 4)).Members.Select(m => m.DeckId).Should().Equal(4, 5);
    }

    [Fact]
    public async Task Members_LoneDeckLine_HasNoFranchise()
    {
        var line = await MembersAsync(3, LoneDeckId);

        line.FranchiseId.Should().BeNull();
        line.Members.Select(m => m.DeckId).Should().Equal(LoneDeckId);
    }

    [Fact]
    public async Task Members_Setting_HasNoFranchise()
    {
        var setting = await MembersAsync(2, SettingId);

        setting.FranchiseId.Should().BeNull();
        setting.Members.Select(m => m.DeckId).Should().Equal(1, LoneDeckId);
    }

    [Theory]
    [InlineData(1, 999)]
    [InlineData(2, 999)]
    [InlineData(3, 999)]
    public async Task UnknownGroup_Is404(int kind, int id)
    {
        (await SendAsync($"/api/media-group/members?kind={kind}&id={id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync($"/api/media-group/stats?kind={kind}&id={id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var vocabulary = await GetAsync<PaginatedResponse<DeckVocabularyListDto?>>($"/api/media-group/vocabulary?kind={kind}&id={id}");
        vocabulary.Data.Should().BeNull();
        vocabulary.TotalItems.Should().Be(0);
    }

    [Fact]
    public async Task UnknownKind_Is400()
    {
        foreach (var endpoint in new[] { "members", "stats", "vocabulary" })
            (await SendAsync($"/api/media-group/{endpoint}?kind=9&id=1")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Stats_ApplyTheFilters()
    {
        var whole = await StatsAsync(1, _franchiseId);
        whole.CharacterCount.Should().Be(7500);
        whole.UniqueWordCount.Should().Be(5);

        var anime = await StatsAsync(1, _franchiseId, "&mediaTypes=1");
        anime.CharacterCount.Should().Be(5000);
        anime.Difficulty.Should().BeApproximately(3f, 0.001f);

        var withoutSequel = await StatsAsync(1, _franchiseId, "&mediaTypes=1&excludeDeckIds=2");
        withoutSequel.CharacterCount.Should().Be(2000);
        withoutSequel.UniqueWordCount.Should().Be(3);
    }

    [Fact]
    public async Task Stats_ForSeriesAndLine()
    {
        var series = await StatsAsync(2, SagaSeriesId);
        series.CharacterCount.Should().Be(2500);
        series.UniqueWordCount.Should().Be(3);

        var line = await StatsAsync(3, 1);
        line.CharacterCount.Should().Be(6000);
        line.UniqueWordCount.Should().Be(4);
    }

    [Fact]
    public async Task Vocabulary_MergesEachKind()
    {
        (await VocabularyAsync(1, _franchiseId)).Should().Equal((5, 20), (2, 12), (1, 11), (3, 3), (4, 2));
        (await VocabularyAsync(1, _franchiseId, "&mediaTypes=1&excludeDeckIds=4")).Should().Equal((2, 12), (1, 10), (3, 3));
        (await VocabularyAsync(2, SagaSeriesId, "&excludeDeckIds=4")).Should().Equal((1, 11), (2, 5));
        (await VocabularyAsync(3, 1)).Should().Equal((2, 12), (1, 10), (3, 3), (4, 2));
        (await VocabularyAsync(3, 4, "&mediaTypes=8")).Should().BeEmpty();
    }

    [Fact]
    public async Task Line_CoversTheWholeStoryComponent()
    {
        var chain = Enumerable.Range(100, 120).ToList();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            db.Decks.AddRange(chain.Select(id => Deck(id, $"Chain {id}", MediaType.Anime, 2000, 10, 1f)));
            await db.SaveChangesAsync();
            db.DeckRelationships.AddRange(chain.Skip(1).Select(id => Rel(id, id - 1, DeckRelationshipType.Sequel)));
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<FranchiseSyncRunner>().RunAsync();
        }

        (await MembersAsync(3, 100)).Members.Select(m => m.DeckId).Should().BeEquivalentTo(chain);
    }
}
