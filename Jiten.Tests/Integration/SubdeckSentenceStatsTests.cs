using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Billing;
using Jiten.Core.Data.FSRS;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>
/// Subdecks on a title's detail page carry exact readable and i+1 shares of their own text for Jiten+ users.
/// UserA masters word 1; episode 1 is [1], [1, 2] and episode 2 is [2], [3].
/// </summary>
public class SubdeckSentenceStatsTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int ParentId = 1;
    private const int Episode1 = 2;
    private const int Episode2 = 3;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        await ResetBilling();
        await Seed();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Subdecks_CarryTheirOwnSentenceShares_ForJitenPlus()
    {
        await MakeTrial();

        var subDecks = await FetchSubdecks();

        subDecks[Episode1].GetProperty("readableSentences").GetSingle().Should().BeApproximately(50f, 0.01f);
        subDecks[Episode1].GetProperty("iPlusOneSentences").GetSingle().Should().BeApproximately(50f, 0.01f);
        subDecks[Episode2].GetProperty("readableSentences").GetSingle().Should().Be(0f);
        subDecks[Episode2].GetProperty("iPlusOneSentences").GetSingle().Should().BeApproximately(100f, 0.01f);
    }

    [Fact]
    public async Task Subdecks_OmitSentenceShares_WithoutJitenPlus()
    {
        var subDecks = await FetchSubdecks();

        subDecks[Episode1].TryGetProperty("readableSentences", out var readable).Should().BeTrue();
        readable.ValueKind.Should().Be(JsonValueKind.Null);
    }

    private async Task<Dictionary<int, JsonElement>> FetchSubdecks()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/media-deck/{ParentId}/detail").WithUser(TestUsers.UserA));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("subDecks").EnumerateArray().ToDictionary(d => d.GetProperty("deckId").GetInt32());
    }

    private async Task Seed()
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await jitenDb.DeckSentenceProfiles.ExecuteDeleteAsync();
        await jitenDb.DeckWords.ExecuteDeleteAsync();
        await jitenDb.Decks.ExecuteDeleteAsync();

        var parent = new Deck { DeckId = ParentId, OriginalTitle = "Show", MediaType = MediaType.Anime, CreationDate = DateTime.UtcNow };
        jitenDb.Decks.Add(parent);
        await jitenDb.SaveChangesAsync();
        jitenDb.Decks.Add(new Deck { DeckId = Episode1, ParentDeckId = ParentId, DeckOrder = 1, OriginalTitle = "Episode 1", MediaType = MediaType.Anime, CreationDate = DateTime.UtcNow });
        jitenDb.Decks.Add(new Deck { DeckId = Episode2, ParentDeckId = ParentId, DeckOrder = 2, OriginalTitle = "Episode 2", MediaType = MediaType.Anime, CreationDate = DateTime.UtcNow });

        AddProfile(jitenDb, Episode1, [Key(1)], [Key(1), Key(2)]);
        AddProfile(jitenDb, Episode2, [Key(2)], [Key(3)]);
        await jitenDb.SaveChangesAsync();

        userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, 1, 0) { State = FsrsState.Mastered, LastReview = DateTime.UtcNow.AddDays(-1), Due = DateTime.UtcNow.AddDays(100) });
        await userDb.SaveChangesAsync();
    }

    private static void AddProfile(JitenDbContext db, int deckId, params int[][] sentences)
    {
        db.DeckSentenceProfiles.Add(new DeckSentenceProfile
        {
            DeckId = deckId,
            Profile = SentenceProfileCodec.Encode(sentences),
            SentenceCount = sentences.Length,
            Version = SentenceProfileCodec.FormatVersion,
            BuiltAt = DateTime.UtcNow
        });
    }

    private static int Key(int wordId) => ExampleSentenceTokens.WordKey(wordId, 0);

    private async Task ResetBilling()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserPromoCredits.RemoveRange(userDb.UserPromoCredits);
        userDb.PromoCodes.RemoveRange(userDb.PromoCodes);
        foreach (var user in await userDb.Users.ToListAsync())
        {
            user.StripeSubscriptionActive = false;
            user.SubscriptionPeriodEnd = null;
            user.SubscriptionPlan = null;
            user.IsLifetime = false;
            user.LifetimeSource = null;
            user.AdminPremiumOverride = false;
        }

        await userDb.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<IJitenPlusService>().InvalidateTier(TestUsers.UserA);
    }

    private async Task MakeTrial()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var code = new PromoCode { Code = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(), DurationDays = 5, GrantsFullTier = false };
        userDb.PromoCodes.Add(code);
        await userDb.SaveChangesAsync();
        userDb.UserPromoCredits.Add(new UserPromoCredit
        {
            UserId = TestUsers.UserA, PromoCodeId = code.CodeId, GrantsFullTier = false, RemainingDays = 5, GrantedAt = DateTime.UtcNow
        });
        await userDb.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<IJitenPlusService>().InvalidateTier(TestUsers.UserA);
    }
}
