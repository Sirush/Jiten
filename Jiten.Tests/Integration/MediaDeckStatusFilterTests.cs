using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class MediaDeckStatusFilterTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>1 Planning, 2 Ongoing, 3 Completed, 4 Dropped, 5 no row, 6 row with no status, 7 Planning + ignored, 8 ignored only, 9 favourite only.</summary>
    private async Task SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        db.Decks.AddRange(Enumerable.Range(1, 9).Select(id => new Deck
        {
            DeckId = id, OriginalTitle = $"Deck {id}", MediaType = MediaType.Anime, Difficulty = 2.0f
        }));
        await db.SaveChangesAsync();

        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserDeckPreferences.AddRange(
            Pref(1, DeckStatus.Planning),
            Pref(2, DeckStatus.Ongoing),
            Pref(3, DeckStatus.Completed),
            Pref(4, DeckStatus.Dropped),
            Pref(6, DeckStatus.None),
            Pref(7, DeckStatus.Planning, ignored: true),
            Pref(8, DeckStatus.None, ignored: true),
            Pref(9, DeckStatus.None, favourite: true));
        await userDb.SaveChangesAsync();
    }

    private static UserDeckPreference Pref(int deckId, DeckStatus status, bool ignored = false, bool favourite = false) =>
        new()
        {
            UserId = TestUsers.UserA, DeckId = deckId, Status = status, IsIgnored = ignored, IsFavourite = favourite,
            UpdatedAt = DateTime.UtcNow
        };

    private async Task<List<int>> GetDeckIdsAsync(string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/media-deck/get-media-decks{query}").WithUser(TestUsers.UserA);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").EnumerateArray().Select(d => d.GetProperty("deckId").GetInt32()).OrderBy(id => id).ToList();
    }

    [Theory]
    [InlineData("", new[] { 1, 2, 3, 4, 5, 6, 9 })]
    [InlineData("?status=none", new[] { 1, 2, 3, 4, 5, 6, 9 })]
    [InlineData("?status=planning", new[] { 1 })]
    [InlineData("?status=completed", new[] { 3 })]
    [InlineData("?status=nostatus", new[] { 5, 6, 9 })]
    [InlineData("?status=ignore", new[] { 7, 8 })]
    [InlineData("?status=planning,nostatus", new[] { 1, 5, 6, 9 })]
    [InlineData("?status=nostatus,planning", new[] { 1, 5, 6, 9 })]
    [InlineData("?status=planning,ongoing", new[] { 1, 2 })]
    [InlineData("?status=planning,ignore", new[] { 1, 7, 8 })]
    [InlineData("?status=nostatus,ignore", new[] { 5, 6, 7, 8, 9 })]
    [InlineData("?status=planning,ongoing,completed,dropped,nostatus", new[] { 1, 2, 3, 4, 5, 6, 9 })]
    [InlineData("?status=fav", new[] { 9 })]
    [InlineData("?status=bogus", new[] { 1, 2, 3, 4, 5, 6, 9 })]
    public async Task StatusFilter_UnionsTickedStatuses(string query, int[] expected)
    {
        await SeedAsync();

        var ids = await GetDeckIdsAsync(query);

        ids.Should().Equal(expected);
    }
}
