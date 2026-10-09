using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class FranchiseSuggestionTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        await db.Lookups.Where(l => l.WordId == 990).ExecuteDeleteAsync();
        await db.JMDictWords.Where(w => w.WordId == 990).ExecuteDeleteAsync();

        db.Franchises.Add(new Franchise { FranchiseId = 50, OriginalTitle = "星のカービィ" });
        db.Decks.AddRange(
            Deck(1, "日本統一", MediaType.Movie),
            Deck(2, "日本統一2", MediaType.Movie),
            Deck(3, "星のカービィ", MediaType.VideoGame, franchiseId: 50),
            Deck(4, "星のカービィ2", MediaType.VideoGame, franchiseId: 50),
            Deck(5, "星のカービィ3", MediaType.VideoGame),
            Deck(6, "異世界の旅人", MediaType.Novel),
            Deck(7, "異世界の勇者", MediaType.Novel));
        db.JMDictWords.Add(new JmDictWord { WordId = 990, PartsOfSpeech = ["n"] });
        db.Lookups.Add(new JmDictLookup { WordId = 990, LookupKey = "異世界" });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static Deck Deck(int id, string title, MediaType type, int? franchiseId = null) =>
        new() { DeckId = id, OriginalTitle = title, MediaType = type, ReleaseDate = new DateOnly(2000 + id, 1, 1), FranchiseId = franchiseId };

    private async Task<PaginatedResponse<List<FranchiseSuggestionDto>>> ListAsync(string query = "")
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/admin/franchise/suggestions{query}").WithAdmin());
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PaginatedResponse<List<FranchiseSuggestionDto>>>())!;
    }

    private async Task PostAsync(string action, string rootKey, params int[] deckIds)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/admin/franchise/suggestions/{action}")
                                               .WithAdmin()
                                               .WithJsonContent(new { rootKey, deckIds }));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task List_GroupsSharedRoots_AndSkipsCommonWords()
    {
        var page = await ListAsync();

        page.Data.Select(s => s.Root).Should().BeEquivalentTo("日本統一", "星のカービィ");

        var kirby = page.Data.Single(s => s.Root == "星のカービィ");
        kirby.AnchorDeckId.Should().Be(3);
        kirby.Decks.Select(d => (d.Deck.DeckId, d.FranchiseTitles?.OriginalTitle)).Should().Equal((3, "星のカービィ"), (4, "星のカービィ"), (5, null));
    }

    [Fact]
    public async Task List_FiltersByScopeAndMediaType()
    {
        (await ListAsync($"?scope={(int)FranchiseSuggestionScope.Unlinked}")).Data.Should().ContainSingle().Which.Root.Should().Be("日本統一");
        (await ListAsync($"?mediaType={(int)MediaType.VideoGame}")).Data.Should().ContainSingle().Which.Root.Should().Be("星のカービィ");
    }

    [Fact]
    public async Task Dismiss_HidesTheGroup_UntilRestored()
    {
        await PostAsync("dismiss", "日本統一", 1, 2);
        (await ListAsync()).Data.Select(s => s.Root).Should().Equal("星のカービィ");

        await PostAsync("restore", "日本統一", 1, 2);
        (await ListAsync()).Data.Should().HaveCount(2);
    }

    [Fact]
    public async Task Dismiss_ReturnsWhenANewDeckJoinsTheRoot()
    {
        await PostAsync("dismiss", "日本統一", 1, 2);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            db.Decks.Add(Deck(8, "日本統一3", MediaType.Movie));
            await db.SaveChangesAsync();
        }

        (await ListAsync()).Data.Should().Contain(s => s.Root == "日本統一");
    }

    [Fact]
    public async Task BuilderDecks_ReturnsTheRequestedNodes()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/admin/franchise-builder/decks?ids=1&ids=5").WithAdmin());
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var nodes = (await response.Content.ReadFromJsonAsync<List<FranchiseNodeDto>>())!;
        nodes.Select(n => n.DeckId).Should().BeEquivalentTo([1, 5]);
    }
}
