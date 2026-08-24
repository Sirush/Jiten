using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class MediaListFileImportTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SeedDeck(string title, int? parentDeckId = null)
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        var deck = new Deck { OriginalTitle = title, MediaType = MediaType.Anime, ParentDeckId = parentDeckId };
        jitenDb.Decks.Add(deck);
        await jitenDb.SaveChangesAsync();
        return deck.DeckId;
    }

    private async Task SeedPreference(int deckId, DeckStatus status, bool isFavourite = false, string userId = TestUsers.UserA)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserDeckPreferences.Add(new UserDeckPreference { UserId = userId, DeckId = deckId, Status = status, IsFavourite = isFavourite });
        await userDb.SaveChangesAsync();
    }

    private async Task<UserDeckPreference?> GetPreference(int deckId, string userId = TestUsers.UserA)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.UserDeckPreferences.FirstOrDefaultAsync(p => p.UserId == userId && p.DeckId == deckId);
    }

    private async Task<byte[]> Export(string format, string userId = TestUsers.UserA)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/user/media-list/export?format={format}").WithUser(userId);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private HttpRequestMessage FileRequest(byte[] bytes, string fileName, string contentType, string? userId = TestUsers.UserA)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/media-list/import/file-preview");
        if (userId != null)
            request.WithUser(userId);

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        var form = new MultipartFormDataContent { { file, "file", fileName } };
        request.Content = form;
        return request;
    }

    private Task<HttpResponseMessage> PostFile(string text, string fileName, string contentType, string? userId = TestUsers.UserA) =>
        _client.SendAsync(FileRequest(Encoding.UTF8.GetBytes(text), fileName, contentType, userId));

    [Fact]
    public async Task Preview_CsvExportRoundtripsToAnotherAccount()
    {
        var deck = await SeedDeck("Show, with comma");
        await SeedPreference(deck, DeckStatus.Completed, isFavourite: true);

        var csv = await Export("csv");

        var response = await _client.SendAsync(FileRequest(csv, "jiten-media-list.csv", "text/csv", TestUsers.UserB));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("fileName").GetString().Should().Be("jiten-media-list.csv");
        body.GetProperty("counts").GetProperty("matched").GetInt32().Should().Be(1);
        body.GetProperty("counts").GetProperty("unmatched").GetInt32().Should().Be(0);

        var row = body.GetProperty("matched").EnumerateArray().Single();
        row.GetProperty("deckId").GetInt32().Should().Be(deck);
        row.GetProperty("originalTitle").GetString().Should().Be("Show, with comma");
        row.GetProperty("mappedStatus").GetInt32().Should().Be((int)DeckStatus.Completed);
        row.GetProperty("externalStatus").GetString().Should().Be("Completed");
        row.GetProperty("isFavourite").GetBoolean().Should().BeTrue();
        row.GetProperty("currentStatus").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("currentFavourite").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Preview_JsonExportRoundtrips()
    {
        var deck = await SeedDeck("Show");
        await SeedPreference(deck, DeckStatus.Ongoing);

        var json = await Export("json");

        var response = await _client.SendAsync(FileRequest(json, "jiten-media-list.json", "application/json", TestUsers.UserB));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("counts").GetProperty("matched").GetInt32().Should().Be(1);
        var row = body.GetProperty("matched").EnumerateArray().Single();
        row.GetProperty("deckId").GetInt32().Should().Be(deck);
        row.GetProperty("mappedStatus").GetInt32().Should().Be((int)DeckStatus.Ongoing);
        row.GetProperty("isFavourite").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Preview_ReportsCurrentStatusAndSubdeckCount()
    {
        var deck = await SeedDeck("Series");
        await SeedDeck("Episode 1", parentDeckId: deck);
        await SeedPreference(deck, DeckStatus.Completed, userId: TestUsers.UserB);
        await SeedPreference(deck, DeckStatus.Planning);

        var csv = await Export("csv", TestUsers.UserB);
        var body = await (await _client.SendAsync(FileRequest(csv, "list.csv", "text/csv"))).Content.ReadFromJsonAsync<JsonElement>();

        var row = body.GetProperty("matched").EnumerateArray().Single();
        row.GetProperty("currentStatus").GetInt32().Should().Be((int)DeckStatus.Planning);
        row.GetProperty("subdeckCount").GetInt32().Should().Be(1);
        row.GetProperty("progress").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("finishedAt").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("counts").GetProperty("conflicts").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Preview_ProgressRoundtripsThroughCsvAndJson()
    {
        var deck = await SeedDeck("Series");
        var episode1 = await SeedDeck("Episode 1", parentDeckId: deck);
        await SeedDeck("Episode 2", parentDeckId: deck);
        await SeedPreference(deck, DeckStatus.Ongoing);
        await SeedPreference(episode1, DeckStatus.Completed);

        foreach (var format in new[] { "csv", "json" })
        {
            var file = await Export(format);
            var body = await (await _client.SendAsync(FileRequest(file, $"list.{format}", "text/csv", TestUsers.UserB)))
                             .Content.ReadFromJsonAsync<JsonElement>();

            var row = body.GetProperty("matched").EnumerateArray().Single();
            row.GetProperty("progress").GetInt32().Should().Be(1, $"format {format}");
            row.GetProperty("subdeckCount").GetInt32().Should().Be(2, $"format {format}");
        }
    }

    [Fact]
    public async Task Preview_UnknownDeckIdLandsInUnmatched()
    {
        var deck = await SeedDeck("Known Show");

        var csv = "DeckId,OriginalTitle,RomajiTitle,EnglishTitle,MediaType,Status,IsFavourite,JitenUrl,ExternalLinks\n"
                  + $"{deck},Known Show,,,Anime,Completed,False,https://jiten.moe/decks/media/{deck},\n"
                  + "987654,Gone Show,,,Anime,Ongoing,True,https://jiten.moe/decks/media/987654,\n";

        var body = await (await PostFile(csv, "list.csv", "text/csv")).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("counts").GetProperty("matched").GetInt32().Should().Be(1);
        body.GetProperty("counts").GetProperty("unmatched").GetInt32().Should().Be(1);

        var missing = body.GetProperty("unmatched").EnumerateArray().Single();
        missing.GetProperty("title").GetString().Should().Be("Gone Show");
        missing.GetProperty("url").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Preview_ChildDeckLandsInUnmatched()
    {
        var parent = await SeedDeck("Parent");
        var child = await SeedDeck("Child", parentDeckId: parent);

        var csv = $"DeckId,Status\n{child},Completed\n";
        var body = await (await PostFile(csv, "list.csv", "text/csv")).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("counts").GetProperty("matched").GetInt32().Should().Be(0);
        body.GetProperty("counts").GetProperty("unmatched").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Preview_FallsBackToJitenUrlWhenDeckIdColumnIsMissing()
    {
        var deck = await SeedDeck("Linked Show");

        var csv = $"OriginalTitle,Status,JitenUrl\nLinked Show,Ongoing,https://jiten.moe/decks/media/{deck}\n";
        var body = await (await PostFile(csv, "list.csv", "text/csv")).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("matched").EnumerateArray().Single().GetProperty("deckId").GetInt32().Should().Be(deck);
    }

    [Fact]
    public async Task Preview_KeepsStrongestStatusForDuplicateDeckIds()
    {
        var deck = await SeedDeck("Show");

        var csv = $"DeckId,Status,IsFavourite\n{deck},Planning,True\n{deck},Completed,False\n";
        var body = await (await PostFile(csv, "list.csv", "text/csv")).Content.ReadFromJsonAsync<JsonElement>();

        var row = body.GetProperty("matched").EnumerateArray().Single();
        row.GetProperty("mappedStatus").GetInt32().Should().Be((int)DeckStatus.Completed);
        row.GetProperty("isFavourite").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("not a media list at all", "list.csv")]
    [InlineData("{\"decks\": []}", "list.json")]
    [InlineData("[{\"deckId\": 1,", "list.json")]
    public async Task Preview_MalformedFileIsRejected(string content, string fileName)
    {
        var response = await PostFile(content, fileName, "text/plain");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Preview_FileWithoutUsableRowsIsRejected()
    {
        var response = await PostFile("DeckId,Status\n,\nabc,Nonsense\n", "list.csv", "text/csv");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString().Should().Contain("No entries");
    }

    [Fact]
    public async Task Preview_RequiresAuthentication()
    {
        var response = await PostFile("DeckId,Status\n1,Completed\n", "list.csv", "text/csv", userId: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Apply_SetsFavouriteWithoutClearingExistingOnes()
    {
        var favourited = await SeedDeck("To favourite");
        var untouched = await SeedDeck("Already favourite");
        await SeedPreference(untouched, DeckStatus.Completed, isFavourite: true);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/media-list/import/apply")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new
                                       {
                                           overwriteExisting = true,
                                           entries = new[]
                                                     {
                                                         new { deckId = favourited, status = (int)DeckStatus.Ongoing, isFavourite = true },
                                                         new { deckId = untouched, status = (int)DeckStatus.Completed, isFavourite = false },
                                                     },
                                       });
        var body = await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("favourited").GetInt32().Should().Be(1);
        (await GetPreference(favourited))!.IsFavourite.Should().BeTrue();
        (await GetPreference(untouched))!.IsFavourite.Should().BeTrue();
    }
}
