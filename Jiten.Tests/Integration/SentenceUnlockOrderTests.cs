using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Billing;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>
/// The sentence unlock order for media study decks (Jiten+). Deck frequency ranks words 1..8, while the seeded
/// sentences make word 5 unlock three sentences and word 6 two, so a working order pulls 5 and 6 first.
/// </summary>
public class SentenceUnlockOrderTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int MediaDeckId = 1;
    private const int WordCount = 8;
    private const int SentenceUnlock = 6;
    private const int DeckFrequency = 3;

    private static int _buildSeed;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        await ResetBilling();
        await Seed();
        await SetNewCardsPerDay(3);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AddingTheOrder_WithoutJitenPlus_IsRefused()
    {
        var response = await AddMediaDeck(SentenceUnlock);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jitenPlus").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task TheOrder_OnAWordList_IsRejected()
    {
        await MakeTrial(TestUsers.UserA);

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckType = 2, name = "List", order = SentenceUnlock }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task NewCards_ComeInSentenceUnlockOrder()
    {
        await MakeTrial(TestUsers.UserA);
        var added = await AddMediaDeck(SentenceUnlock);
        added.IsSuccessStatusCode.Should().BeTrue(await added.Content.ReadAsStringAsync());

        var newCards = await FetchNewCards();

        newCards.Take(2).Should().Equal(5, 6);
    }

    [Fact]
    public async Task VocabularyList_SortsInTheOrderNewCardsArrive()
    {
        await MakeTrial(TestUsers.UserA);
        var add = await AddMediaDeck(SentenceUnlock);
        add.IsSuccessStatusCode.Should().BeTrue(await add.Content.ReadAsStringAsync());
        var id = (await add.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userStudyDeckId").GetInt32();

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/srs/study-decks/{id}/vocabulary?sortBy=sentenceUnlock")
            .WithUser(TestUsers.UserA));
        response.EnsureSuccessStatusCode();
        var words = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray()
            .Select(w => w.GetProperty("wordId").GetInt32()).ToList();

        words.Take(2).Should().Equal(5, 6);
        words.Skip(2).Should().ContainInOrder(3, 4, 7, 8);
    }

    [Fact]
    public async Task NewCards_FallBackToDeckFrequency_WhenJitenPlusLapses()
    {
        await MakeTrial(TestUsers.UserA);
        var added = await AddMediaDeck(SentenceUnlock);
        added.IsSuccessStatusCode.Should().BeTrue(await added.Content.ReadAsStringAsync());
        await ResetBilling();

        var newCards = await FetchNewCards();

        newCards.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task EditingOtherFilters_AfterJitenPlusLapses_KeepsWorking()
    {
        await MakeTrial(TestUsers.UserA);
        var add = await AddMediaDeck(SentenceUnlock);
        add.IsSuccessStatusCode.Should().BeTrue(await add.Content.ReadAsStringAsync());
        var id = (await add.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userStudyDeckId").GetInt32();
        await ResetBilling();

        var update = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/srs/study-decks/{id}")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { downloadType = 1, order = SentenceUnlock, excludeKana = true }));

        update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SwitchingToTheOrder_WithoutJitenPlus_IsRefused()
    {
        var add = await AddMediaDeck(DeckFrequency);
        add.IsSuccessStatusCode.Should().BeTrue(await add.Content.ReadAsStringAsync());
        var id = (await add.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userStudyDeckId").GetInt32();

        var update = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/srs/study-decks/{id}")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { downloadType = 1, order = SentenceUnlock, excludeKana = false }));

        update.StatusCode.Should().Be(HttpStatusCode.Forbidden, await update.Content.ReadAsStringAsync());
    }

    private async Task Seed()
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        await userDb.UserStudyDeckWords.ExecuteDeleteAsync();
        await userDb.UserStudyDecks.ExecuteDeleteAsync();
        await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.FsrsCardArchives.ExecuteDeleteAsync();
        await userDb.UserReviewDailies.ExecuteDeleteAsync();
        await userDb.UserFsrsSettings.ExecuteDeleteAsync();

        await jitenDb.DeckSentenceProfiles.ExecuteDeleteAsync();
        await jitenDb.DeckWords.ExecuteDeleteAsync();
        await jitenDb.Definitions.ExecuteDeleteAsync();
        await jitenDb.WordForms.ExecuteDeleteAsync();
        await jitenDb.JMDictWords.ExecuteDeleteAsync();
        await jitenDb.Decks.ExecuteDeleteAsync();

        var deck = new Deck
        {
            DeckId = MediaDeckId,
            OriginalTitle = "Media",
            MediaType = MediaType.Anime,
            CreationDate = DateTime.UtcNow,
            CharacterCount = 1000,
            WordCount = 500,
            UniqueWordCount = 100,
        };
        jitenDb.Decks.Add(deck);
        await jitenDb.SaveChangesAsync();

        for (var i = 1; i <= WordCount; i++)
        {
            jitenDb.DeckWords.Add(new DeckWord { Deck = deck, WordId = i, ReadingIndex = 0, Occurrences = 100 - i });
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = i, PartsOfSpeech = ["noun"] });
            jitenDb.Definitions.Add(new JmDictDefinition { WordId = i, SenseIndex = 0, EnglishMeanings = [$"meaning{i}"], PartsOfSpeech = ["noun"] });
            jitenDb.WordForms.Add(new JmDictWordForm { WordId = i, ReadingIndex = 0, Text = $"word{i}", RubyText = $"word{i}", FormType = JmDictFormType.KanaForm });
        }

        var sentences = new List<IReadOnlyCollection<int>> { Key(5), Key(5), Key(5), Key(6), Key(6), Key(1, 2) };
        jitenDb.DeckSentenceProfiles.Add(new DeckSentenceProfile
        {
            DeckId = MediaDeckId,
            Profile = SentenceProfileCodec.Encode(sentences),
            SentenceCount = sentences.Count,
            Version = SentenceProfileCodec.FormatVersion,
            // The unlock order is cached per profile build, so each test gets its own.
            BuiltAt = DateTime.UtcNow.AddSeconds(Interlocked.Increment(ref _buildSeed))
        });
        await jitenDb.SaveChangesAsync();
    }

    private static int[] Key(params int[] wordIds) => wordIds.Select(id => ExampleSentenceTokens.WordKey(id, 0)).ToArray();

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

    private async Task MakeTrial(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var code = new PromoCode { Code = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(), DurationDays = 5, GrantsFullTier = false };
        userDb.PromoCodes.Add(code);
        await userDb.SaveChangesAsync();
        userDb.UserPromoCredits.Add(new UserPromoCredit
        {
            UserId = userId, PromoCodeId = code.CodeId, GrantsFullTier = false, RemainingDays = 5, GrantedAt = DateTime.UtcNow
        });
        await userDb.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<IJitenPlusService>().InvalidateTier(userId);
    }

    private Task<HttpResponseMessage> AddMediaDeck(int order) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckId = MediaDeckId, downloadType = 1, order, excludeKana = false }));

    private async Task SetNewCardsPerDay(int newCardsPerDay)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-settings")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new
            {
                newCardsPerDay,
                maxReviewsPerDay = 200,
                gradingButtons = 4,
                interleaving = "newFirst",
                reviewFrom = "allTracked",
                newCardGathering = "topDeck"
            }));
        response.EnsureSuccessStatusCode();
    }

    private async Task<List<int>> FetchNewCards()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-batch?limit=20").WithUser(TestUsers.UserA));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("cards").EnumerateArray()
            .Where(c => c.GetProperty("isNewCard").GetBoolean())
            .Select(c => c.GetProperty("wordId").GetInt32())
            .ToList();
    }
}
