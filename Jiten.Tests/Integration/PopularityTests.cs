using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class PopularityTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedDecksAsync(params Deck[] decks)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        db.Decks.AddRange(decks);
        await db.SaveChangesAsync();
    }

    private static Deck Deck(int id, string title, byte rating = 0) =>
        new() { DeckId = id, OriginalTitle = title, RomajiTitle = title, MediaType = MediaType.Anime, Difficulty = 2.0f, ExternalRating = rating, CreationDate = DateTimeOffset.UtcNow.AddYears(-1) };

    private async Task<List<DeckDto>> ListAsync(string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/media-deck/get-media-decks{query}");
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<PaginatedResponse<List<DeckDto>>>();
        return page!.Data;
    }

    private async Task<HttpStatusCode> ViewAsync(int deckId) =>
        (await _client.PostAsync($"/api/media-deck/{deckId}/view", null)).StatusCode;

    [Fact]
    public async Task View_beacon_counts_a_repeat_visitor_once_and_drops_unknown_decks()
    {
        await SeedDecksAsync(Deck(1, "A"));

        (await ViewAsync(1)).Should().Be(HttpStatusCode.NoContent);
        (await ViewAsync(1)).Should().Be(HttpStatusCode.NoContent);
        (await ViewAsync(999)).Should().Be(HttpStatusCode.NoContent);

        var buffer = factory.Services.GetRequiredService<IDeckActivityBuffer>();
        buffer.RecordView(1, "203.0.113.7");
        buffer.RecordView(1, "203.0.113.7");
        buffer.RecordView(1, "203.0.113.8");
        await buffer.FlushAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var rows = await db.DeckActivityDailies.ToListAsync();
        rows.Should().ContainSingle();
        rows[0].DeckId.Should().Be(1);
        rows[0].Views.Should().Be(3);
        rows[0].Date.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow));
    }

    [Fact]
    public async Task Popularity_is_the_default_sort_and_follows_the_recomputed_score()
    {
        await SeedDecksAsync(Deck(1, "Quiet", 90), Deck(2, "Loved", 10), Deck(3, "Seen", 50));

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.UserDeckPreferences.Add(new UserDeckPreference { UserId = TestUsers.UserA, DeckId = 2, Status = DeckStatus.Completed });
            await userDb.SaveChangesAsync();

            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            db.DeckActivityDailies.Add(new DeckActivityDaily { DeckId = 3, Date = DateOnly.FromDateTime(DateTime.UtcNow), Views = 50 });
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<PopularityScoreJob>().RecomputeAll();
        }

        var byDefault = await ListAsync("");
        byDefault.Select(d => d.DeckId).Should().ContainInOrder(2, 3, 1);

        var explicitAsc = await ListAsync("?sortBy=popularity&sortOrder=0");
        explicitAsc.Select(d => d.DeckId).Should().ContainInOrder(3, 2, 1);
    }

    [Fact]
    public async Task Preference_rows_are_stamped_only_when_the_signal_strengthens()
    {
        await SeedDecksAsync(Deck(1, "A"));
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var pref = new UserDeckPreference { UserId = TestUsers.UserA, DeckId = 1, Status = DeckStatus.Planning };
        userDb.UserDeckPreferences.Add(pref);
        await userDb.SaveChangesAsync();
        var first = pref.UpdatedAt;
        first.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        await Task.Delay(20);
        pref.Status = DeckStatus.Completed;
        await userDb.SaveChangesAsync();
        var upgraded = pref.UpdatedAt;
        upgraded.Should().BeAfter(first);

        await Task.Delay(20);
        pref.Status = DeckStatus.Planning;
        await userDb.SaveChangesAsync();
        pref.UpdatedAt.Should().Be(upgraded);

        await Task.Delay(20);
        pref.IsFavourite = true;
        await userDb.SaveChangesAsync();
        pref.UpdatedAt.Should().BeAfter(upgraded);

        var favourited = pref.UpdatedAt;
        await Task.Delay(20);
        pref.IsFavourite = false;
        await userDb.SaveChangesAsync();
        pref.UpdatedAt.Should().Be(favourited);
    }
}
