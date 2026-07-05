using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class DeckOriginFileTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string ParentFileName = "本好きの下剋上 第一部.epub";
    private const string ChildFileName = "[Judas] Sousou no Frieren - S01E01.srt";

    private async Task<int> SeedParentWithChild()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        var parent = new Deck
        {
            OriginalTitle = "Parent Series", MediaType = MediaType.Anime, OriginalFileName = ParentFileName
        };
        db.Decks.Add(parent);
        await db.SaveChangesAsync();

        var child = new Deck
        {
            OriginalTitle = "Episode 1", MediaType = MediaType.Anime, ParentDeckId = parent.DeckId, DeckOrder = 1,
            OriginalFileName = ChildFileName
        };
        db.Decks.Add(child);
        await db.SaveChangesAsync();

        return parent.DeckId;
    }

    [Fact]
    public async Task AdminGetDeck_ReturnsOriginalFileName_OnMainDeckAndSubDecks()
    {
        var parentId = await SeedParentWithChild();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/deck/{parentId}").WithAdmin();
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("mainDeck").GetProperty("originalFileName").GetString().Should().Be(ParentFileName);

        var subDecks = body.GetProperty("subDecks");
        subDecks.GetArrayLength().Should().Be(1);
        subDecks[0].GetProperty("originalFileName").GetString().Should().Be(ChildFileName);
    }

    [Fact]
    public async Task PublicDeckDetail_DoesNotLeakOriginalFileName()
    {
        var parentId = await SeedParentWithChild();

        var response = await _client.GetAsync($"/api/media-deck/{parentId}/detail");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var mainDeck = body.GetProperty("data").GetProperty("mainDeck");

        // Property is serialized (nullable) but must never carry a value on public endpoints.
        if (mainDeck.TryGetProperty("originalFileName", out var origin))
            origin.ValueKind.Should().Be(JsonValueKind.Null);
    }
}
