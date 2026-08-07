using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Billing;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Services;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jiten.Parser.Tests.Integration;

/// <summary>
/// Learning roadmaps (Jiten+). Seeds a small catalogue where deck readability is controlled precisely: word 1
/// is the "known filler" that carries coverage, and the remaining words are what each deck can teach.
/// </summary>
public class RoadmapTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int KnownFillerWordId = 1;
    private readonly Dictionary<string, int> _deckIds = new();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        await ResetBilling();
        await ClearRoadmaps();
        await SeedCatalogue();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Seeding ------------------------------------------------------------

    private async Task ResetBilling()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserPromoCredits.RemoveRange(userDb.UserPromoCredits);
        userDb.PromoCodes.RemoveRange(userDb.PromoCodes);
        foreach (var user in await userDb.Users.ToListAsync())
        {
            user.StripeSubscriptionActive = false;
            user.IsLifetime = false;
            user.AdminPremiumOverride = false;
        }

        await userDb.SaveChangesAsync();

        var jitenPlus = scope.ServiceProvider.GetRequiredService<IJitenPlusService>();
        foreach (var id in new[] { TestUsers.UserA, TestUsers.UserB, TestUsers.Admin })
            jitenPlus.InvalidateTier(id);
    }

    private async Task ClearRoadmaps()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserRoadmaps.RemoveRange(userDb.UserRoadmaps);
        userDb.FsrsCards.RemoveRange(userDb.FsrsCards);
        userDb.FsrsCardArchives.RemoveRange(userDb.FsrsCardArchives);
        userDb.UserReviewDailies.RemoveRange(userDb.UserReviewDailies);
        userDb.UserDeckPreferences.RemoveRange(userDb.UserDeckPreferences);
        userDb.UserCoverageChunks.RemoveRange(userDb.UserCoverageChunks);
        await userDb.SaveChangesAsync();
    }

    /// <summary>
    /// Every deck is 9,000 occurrences of the known filler plus its own teachable words, so each sits far
    /// above a 90% comprehension floor once the filler is known and readability is never accidentally the
    /// variable. Sizes clear <see cref="RoadmapDataLoader.MinDeckWordCount"/>, which excludes stub decks.
    /// </summary>
    private async Task SeedCatalogue()
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        await jitenDb.DeckWords.ExecuteDeleteAsync();
        await jitenDb.WordForms.ExecuteDeleteAsync();
        await jitenDb.JMDictWords.ExecuteDeleteAsync();

        for (var i = 1; i <= 40; i++)
        {
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = i, PartsOfSpeech = ["noun"] });
            jitenDb.WordForms.Add(new JmDictWordForm
            {
                WordId = i, ReadingIndex = 0, Text = $"語{i}", RubyText = $"ご{i}",
                FormType = JmDictFormType.KanaForm
            });
        }

        await jitenDb.SaveChangesAsync();

        // "teaches1" is deliberately the weakest option; "teaches3" the strongest.
        await AddDeck(jitenDb, "teaches1", MediaType.Anime, 2.0f, [10]);
        await AddDeck(jitenDb, "teaches2", MediaType.Anime, 2.5f, [11, 12]);
        await AddDeck(jitenDb, "teaches3", MediaType.Anime, 3.0f, [13, 14, 15]);
        await AddDeck(jitenDb, "novel", MediaType.Novel, 4.5f, [20, 21]);
        await AddDeck(jitenDb, "adult", MediaType.Anime, 2.0f, [30, 31], Genre.AdultOnly);

        // Unreadable without more vocabulary: the filler is only a third of it.
        await AddDeck(jitenDb, "hard", MediaType.Anime, 5.0f, [], hardWords: [35, 36, 37]);
    }

    private async Task AddDeck(JitenDbContext db, string key, MediaType mediaType, float difficulty,
                               int[] teachableWords, Genre? genre = null, int[]? hardWords = null)
    {
        var deck = new Deck
        {
            OriginalTitle = key,
            MediaType = mediaType,
            Difficulty = difficulty,
            DifficultyOverride = -1,
            ReleaseDate = new DateOnly(2020, 1, 1),
            CharacterCount = 1000
        };

        if (genre.HasValue)
            deck.DeckGenres = new List<DeckGenre> { new() { Genre = genre.Value } };

        db.Decks.Add(deck);
        await db.SaveChangesAsync();
        _deckIds[key] = deck.DeckId;

        var words = new List<DeckWord>();
        var fillerOccurrences = hardWords is { Length: > 0 } ? 3000 : 9000;

        words.Add(new DeckWord { Deck = deck, WordId = KnownFillerWordId, ReadingIndex = 0, Occurrences = fillerOccurrences });

        foreach (var wordId in teachableWords)
            words.Add(new DeckWord { Deck = deck, WordId = wordId, ReadingIndex = 0, Occurrences = 100 });

        foreach (var wordId in hardWords ?? [])
            words.Add(new DeckWord { Deck = deck, WordId = wordId, ReadingIndex = 0, Occurrences = 2000 });

        db.DeckWords.AddRange(words);

        var total = words.Sum(w => w.Occurrences);
        deck.WordCount = total;
        deck.UniqueWordCount = words.Count;
        await db.SaveChangesAsync();
    }

    /// <summary>Marks the filler word mature so every normal deck clears the comprehension floor.</summary>
    private async Task GiveKnownVocabulary(string userId, params int[] extraWordIds)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        foreach (var wordId in new[] { KnownFillerWordId }.Concat(extraWordIds))
        {
            userDb.FsrsCards.Add(new FsrsCard
            {
                UserId = userId,
                WordId = wordId,
                ReadingIndex = 0,
                State = FsrsState.Mastered,
                Due = DateTime.UtcNow.AddDays(30),
                CreatedAt = DateTime.UtcNow
            });
        }

        await userDb.SaveChangesAsync();
    }

    private async Task SetDeckStatus(string userId, int deckId, DeckStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserDeckPreferences.Add(new UserDeckPreference { UserId = userId, DeckId = deckId, Status = status });
        await userDb.SaveChangesAsync();
    }

    private async Task MakeFull(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var user = await userDb.Users.FirstAsync(u => u.Id == userId);
        user.AdminPremiumOverride = true;
        await userDb.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<IJitenPlusService>().InvalidateTier(userId);
    }

    private async Task MakeTrial(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var code = new PromoCode
        {
            Code = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(), DurationDays = 5, GrantsFullTier = false
        };
        userDb.PromoCodes.Add(code);
        await userDb.SaveChangesAsync();
        userDb.UserPromoCredits.Add(new UserPromoCredit
        {
            UserId = userId, PromoCodeId = code.CodeId, GrantsFullTier = false,
            RemainingDays = 5, GrantedAt = DateTime.UtcNow
        });
        await userDb.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<IJitenPlusService>().InvalidateTier(userId);
    }

    // ---- Request helpers ----------------------------------------------------

    private object Definition(object? overrides = null)
    {
        var json = JsonSerializer.Serialize(new
        {
            mediaTypes = Array.Empty<int>(),
            comprehensionFloor = 0.90,
            acquisitionThreshold = 5,
            steps = 3,
            preference = "volume",
            candidateMode = "catalogwide",
            contentSimilarity = 0.0
        });

        var baseline = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        if (overrides is not null)
        {
            var extra = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(overrides))!;
            foreach (var (key, value) in extra)
                baseline[key] = value;
        }

        return baseline;
    }

    private async Task<long> CreateOk(string userId, object? definitionOverrides = null,
                                      string mode = "discovery", int? goalDeckId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/roadmaps")
                      .WithUser(userId)
                      .WithJsonContent(new
                      {
                          name = "Test roadmap",
                          mode,
                          goalDeckId,
                          definition = Definition(definitionOverrides)
                      });

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        return dto.GetProperty("id").GetInt64();
    }

    private async Task RunGeneration(long roadmapId)
    {
        using var scope = factory.Services.CreateScope();
        var job = scope.ServiceProvider.GetRequiredService<RoadmapJob>();
        await job.Generate(roadmapId);
    }

    private async Task<JsonElement> GetRoadmap(long id, string userId)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/roadmaps/{id}").WithUser(userId));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<int> StepDeckIds(JsonElement roadmap) =>
        roadmap.GetProperty("payload").GetProperty("steps").EnumerateArray()
               .Select(s => s.GetProperty("deckId").GetInt32())
               .ToList();

    // ---- Tier gating --------------------------------------------------------

    [Fact]
    public async Task Create_WithoutJitenPlus_Returns403()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/roadmaps")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new { name = "Nope", mode = "discovery", definition = Definition() });

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_OnTrial_IsAllowed()
    {
        await MakeTrial(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);

        var id = await CreateOk(TestUsers.UserA);

        id.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task LapsedUser_CanListAndDelete_ButNotCreateOrEdit()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);
        var id = await CreateOk(TestUsers.UserA);
        await RunGeneration(id);

        await ResetBilling();   // subscription lapses

        var list = await _client.SendAsync(
                       new HttpRequestMessage(HttpMethod.Get, "/api/roadmaps").WithUser(TestUsers.UserA));
        list.StatusCode.Should().Be(HttpStatusCode.OK);

        var create = new HttpRequestMessage(HttpMethod.Post, "/api/roadmaps")
                     .WithUser(TestUsers.UserA)
                     .WithJsonContent(new { name = "After lapse", mode = "discovery", definition = Definition() });
        (await _client.SendAsync(create)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var regenerate = await _client.SendAsync(
                             new HttpRequestMessage(HttpMethod.Post, $"/api/roadmaps/{id}/regenerate").WithUser(TestUsers.UserA));
        regenerate.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var delete = await _client.SendAsync(
                         new HttpRequestMessage(HttpMethod.Delete, $"/api/roadmaps/{id}").WithUser(TestUsers.UserA));
        delete.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Pins <see cref="RoadmapDataLoader.LoadKnownWordsAsync"/> to the exact known-word definition that
    /// <c>CoverageComputeService.CreateKnownWordsTempTablesAsync</c> expresses in raw Postgres SQL. The two are
    /// separate implementations (the loader is LINQ so it runs on the SQLite test provider and spans two
    /// DbContexts), and the plan makes their agreement non-negotiable — a roadmap must quote the same coverage
    /// a deck page does. The coverage SQL can't run on SQLite to compare directly, so this encodes every branch
    /// of the rule; if either side drifts, this fails and points at the divergence.
    /// </summary>
    [Fact]
    public async Task LoadKnownWords_MatchesCoverageDefinition_AcrossStatesKanaAndSets()
    {
        var now = DateTime.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

            // FSRS tables are not guaranteed cleared between tests; start from a known slate for this user.
            userDb.FsrsCards.RemoveRange(userDb.FsrsCards.Where(c => c.UserId == TestUsers.UserA));
            userDb.UserWordSetStates.RemoveRange(userDb.UserWordSetStates.Where(s => s.UserId == TestUsers.UserA));
            await userDb.SaveChangesAsync();

            userDb.FsrsCards.AddRange(
                // Mature by state.
                new FsrsCard { UserId = TestUsers.UserA, WordId = 100, ReadingIndex = 0, State = FsrsState.Mastered, Due = now.AddDays(30), CreatedAt = now },
                // Mature by interval (>= 21 days) despite a non-mature state.
                new FsrsCard { UserId = TestUsers.UserA, WordId = 101, ReadingIndex = 0, State = FsrsState.Review, LastReview = now.AddDays(-1), Due = now.AddDays(25), CreatedAt = now },
                // Young: being learned, reviewed at least once, interval < 21 days.
                new FsrsCard { UserId = TestUsers.UserA, WordId = 102, ReadingIndex = 0, State = FsrsState.Learning, LastReview = now.AddDays(-1), Due = now.AddDays(5), CreatedAt = now },
                // Brand new, never reviewed: known by neither basis.
                new FsrsCard { UserId = TestUsers.UserA, WordId = 103, ReadingIndex = 0, State = FsrsState.New, Due = now.AddDays(1), CreatedAt = now },
                // Mature on a kanji form: its kana sibling is implied known.
                new FsrsCard { UserId = TestUsers.UserA, WordId = 104, ReadingIndex = 0, State = FsrsState.Mastered, Due = now.AddDays(30), CreatedAt = now },
                // Word-set member 201 also has a brand-new card, so the card governs it (excluded from known).
                new FsrsCard { UserId = TestUsers.UserA, WordId = 201, ReadingIndex = 0, State = FsrsState.New, Due = now.AddDays(1), CreatedAt = now });
            await userDb.SaveChangesAsync();

            // WordForms FK to the jmdict Words table, so the parent entry must exist first.
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = 104 });
            await jitenDb.SaveChangesAsync();

            jitenDb.WordForms.AddRange(
                new JmDictWordForm { WordId = 104, ReadingIndex = 0, Text = "漢字", FormType = JmDictFormType.KanjiForm },
                new JmDictWordForm { WordId = 104, ReadingIndex = 1, Text = "かんじ", FormType = JmDictFormType.KanaForm });

            var set = new WordSet { Slug = "known-set", Name = "Known set" };
            jitenDb.WordSets.Add(set);
            await jitenDb.SaveChangesAsync();

            jitenDb.WordSetMembers.AddRange(
                // No card: known purely via set membership.
                new WordSetMember { SetId = set.SetId, WordId = 200, ReadingIndex = 0, Position = 0 },
                // Has a (new) card: governed by the card, so not counted as known outright.
                new WordSetMember { SetId = set.SetId, WordId = 201, ReadingIndex = 0, Position = 1 });
            await jitenDb.SaveChangesAsync();

            userDb.UserWordSetStates.Add(new UserWordSetState { UserId = TestUsers.UserA, SetId = set.SetId });
            await userDb.SaveChangesAsync();
        }

        using var readScope = factory.Services.CreateScope();
        var loader = readScope.ServiceProvider.GetRequiredService<IRoadmapDataLoader>();

        long Key(int wordId, int readingIndex) => RoadmapEngine.PackKey(wordId, readingIndex);

        var withYoung = await loader.LoadKnownWordsAsync(TestUsers.UserA, includeLearningWords: true);
        withYoung.Should().BeEquivalentTo(new[]
        {
            Key(100, 0), Key(101, 0), Key(102, 0), Key(104, 0), Key(104, 1), Key(200, 0)
        });

        var matureOnly = await loader.LoadKnownWordsAsync(TestUsers.UserA, includeLearningWords: false);
        matureOnly.Should().BeEquivalentTo(new[]
        {
            Key(100, 0), Key(101, 0), Key(104, 0), Key(104, 1), Key(200, 0)
        });
    }

    [Fact]
    public async Task Read_AfterAccessLapses_StillWorks()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);
        var id = await CreateOk(TestUsers.UserA);
        await RunGeneration(id);

        // Access lapses; the roadmap they already generated must stay readable.
        await ResetBilling();

        var response = await _client.SendAsync(
                           new HttpRequestMessage(HttpMethod.Get, $"/api/roadmaps/{id}").WithUser(TestUsers.UserA));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Roadmap_OfAnotherUser_IsNotReadable()
    {
        await MakeFull(TestUsers.UserA);
        await MakeFull(TestUsers.UserB);
        await GiveKnownVocabulary(TestUsers.UserA);
        var id = await CreateOk(TestUsers.UserA);

        var response = await _client.SendAsync(
                           new HttpRequestMessage(HttpMethod.Get, $"/api/roadmaps/{id}").WithUser(TestUsers.UserB));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Generation ---------------------------------------------------------

    [Fact]
    public async Task Generation_OrdersStepsByHowMuchTheyTeach()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);

        var id = await CreateOk(TestUsers.UserA, new { mediaTypes = new[] { (int)MediaType.Anime } });
        await RunGeneration(id);

        var roadmap = await GetRoadmap(id, TestUsers.UserA);
        roadmap.GetProperty("status").GetString().Should().Be("ready");

        var steps = StepDeckIds(roadmap);
        steps.Should().HaveCount(3);
        steps[0].Should().Be(_deckIds["teaches3"], "it teaches the most new words");
        steps[1].Should().Be(_deckIds["teaches2"]);
        steps[2].Should().Be(_deckIds["teaches1"]);
    }

    [Fact]
    public async Task Generation_ExcludesUnreadableDecks_AndOffersADrillStep()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);

        var id = await CreateOk(TestUsers.UserA, new { mediaTypes = new[] { (int)MediaType.Anime }, steps = 10 });
        await RunGeneration(id);

        var roadmap = await GetRoadmap(id, TestUsers.UserA);
        var payload = roadmap.GetProperty("payload");

        StepDeckIds(roadmap).Should().NotContain(_deckIds["hard"]);

        var drill = payload.GetProperty("drill");
        drill.ValueKind.Should().NotBe(JsonValueKind.Null);
        drill.GetProperty("deckId").GetInt32().Should().Be(_deckIds["hard"]);
        drill.GetProperty("wordsNeeded").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task StepWords_ResolvesStoredKeys_ForTheOwner()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);

        var id = await CreateOk(TestUsers.UserA, new { mediaTypes = new[] { (int)MediaType.Anime } });
        await RunGeneration(id);

        var roadmap = await GetRoadmap(id, TestUsers.UserA);
        var firstStep = roadmap.GetProperty("payload").GetProperty("steps").EnumerateArray().First();
        var index = firstStep.GetProperty("index").GetInt32();
        var newWords = firstStep.GetProperty("newWords").GetInt32();
        newWords.Should().BeGreaterThan(0);

        // The stored step carries packed numeric keys only — no per-word objects, no text/reading.
        var storedWord = firstStep.GetProperty("words").EnumerateArray().First();
        storedWord.ValueKind.Should().Be(JsonValueKind.Number);

        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/roadmaps/{id}/steps/{index}/words").WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var words = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("words");
        words.GetArrayLength().Should().Be(newWords, "small test steps stay under the stored cap");
        words.EnumerateArray().Should().OnlyContain(w => w.GetProperty("wordId").GetInt32() > 0);
    }

    [Fact]
    public async Task StepWords_ForAnotherUsersRoadmap_Returns404()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);
        var id = await CreateOk(TestUsers.UserA, new { mediaTypes = new[] { (int)MediaType.Anime } });
        await RunGeneration(id);

        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/roadmaps/{id}/steps/1/words").WithUser(TestUsers.UserB));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Generation_WithNoKnownVocabulary_ReturnsDrillStepRatherThanNothing()
    {
        // A brand-new subscriber must not open the feature to a blank page.
        await MakeFull(TestUsers.UserA);

        var id = await CreateOk(TestUsers.UserA);
        await RunGeneration(id);

        var roadmap = await GetRoadmap(id, TestUsers.UserA);
        roadmap.GetProperty("status").GetString().Should().Be("ready");

        var payload = roadmap.GetProperty("payload");
        payload.GetProperty("steps").GetArrayLength().Should().Be(0);
        payload.GetProperty("drill").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Generation_ExcludesAdultOnlyByDefault()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);

        var id = await CreateOk(TestUsers.UserA, new { mediaTypes = new[] { (int)MediaType.Anime }, steps = 10 });
        await RunGeneration(id);

        StepDeckIds(await GetRoadmap(id, TestUsers.UserA)).Should().NotContain(_deckIds["adult"]);
    }

    [Fact]
    public async Task Generation_IncludesAdultOnly_WhenOptedIn()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);

        var id = await CreateOk(TestUsers.UserA, new
        {
            mediaTypes = new[] { (int)MediaType.Anime },
            steps = 10,
            includeAdultOnly = true
        });
        await RunGeneration(id);

        StepDeckIds(await GetRoadmap(id, TestUsers.UserA)).Should().Contain(_deckIds["adult"]);
    }

    [Fact]
    public async Task Generation_SkipsDecksTheUserCompleted()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);
        await SetDeckStatus(TestUsers.UserA, _deckIds["teaches3"], DeckStatus.Completed);

        var id = await CreateOk(TestUsers.UserA, new { mediaTypes = new[] { (int)MediaType.Anime }, steps = 10 });
        await RunGeneration(id);

        StepDeckIds(await GetRoadmap(id, TestUsers.UserA)).Should().NotContain(_deckIds["teaches3"]);
    }

    [Fact]
    public async Task Generation_SkipsDroppedDecks()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);
        await SetDeckStatus(TestUsers.UserA, _deckIds["teaches3"], DeckStatus.Dropped);

        var id = await CreateOk(TestUsers.UserA, new { mediaTypes = new[] { (int)MediaType.Anime }, steps = 10 });
        await RunGeneration(id);

        StepDeckIds(await GetRoadmap(id, TestUsers.UserA)).Should().NotContain(_deckIds["teaches3"]);
    }

    [Fact]
    public async Task Generation_SkipsDecksTheUserIsPartWayThrough()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);
        await SetDeckStatus(TestUsers.UserA, _deckIds["teaches3"], DeckStatus.Ongoing);

        var id = await CreateOk(TestUsers.UserA, new { mediaTypes = new[] { (int)MediaType.Anime }, steps = 10 });
        await RunGeneration(id);

        StepDeckIds(await GetRoadmap(id, TestUsers.UserA)).Should().NotContain(_deckIds["teaches3"]);
    }

    [Fact]
    public async Task Generation_SeededMode_TreatsOngoingTitlesAsTaste()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);
        await SetDeckStatus(TestUsers.UserA, _deckIds["teaches1"], DeckStatus.Ongoing);

        // Seeded mode with nothing on the Planning list: without an Ongoing title to measure similarity
        // against there is no seed at all, and the loader falls back to the unnarrowed filter set.
        var id = await CreateOk(TestUsers.UserA, new { candidateMode = "seeded", steps = 10 });
        await RunGeneration(id);

        var roadmap = await GetRoadmap(id, TestUsers.UserA);
        roadmap.GetProperty("candidateCount").GetInt32().Should().BeGreaterThan(0);
        StepDeckIds(roadmap).Should().NotContain(_deckIds["teaches1"]);
    }

    [Fact]
    public async Task Generation_HonoursTheMediaTypeFilter()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);

        var id = await CreateOk(TestUsers.UserA, new { mediaTypes = new[] { (int)MediaType.Novel }, steps = 10 });
        await RunGeneration(id);

        var steps = StepDeckIds(await GetRoadmap(id, TestUsers.UserA));
        steps.Should().ContainSingle().And.Contain(_deckIds["novel"]);
    }

    [Fact]
    public async Task Generation_DifficultyBandsAreIndependentPerModelFamily()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);

        // A band that only admits the easy anime decks must not also exclude the harder novel.
        var id = await CreateOk(TestUsers.UserA, new
        {
            steps = 10,
            showsDifficultyMin = 0.0,
            showsDifficultyMax = 2.2,
            novelsDifficultyMin = 4.0,
            novelsDifficultyMax = 5.0
        });
        await RunGeneration(id);

        var steps = StepDeckIds(await GetRoadmap(id, TestUsers.UserA));
        steps.Should().Contain(_deckIds["teaches1"], "difficulty 2.0 is inside the shows band");
        steps.Should().Contain(_deckIds["novel"], "difficulty 4.5 is inside the separate novels band");
        steps.Should().NotContain(_deckIds["teaches3"], "difficulty 3.0 is outside the shows band");
    }

    [Fact]
    public async Task Generation_HigherFloor_ProducesGentlerRoute()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);

        // "hard" needs far more vocabulary; at a 99% floor nothing qualifies at all.
        var id = await CreateOk(TestUsers.UserA, new
        {
            mediaTypes = new[] { (int)MediaType.Anime },
            comprehensionFloor = 0.99,
            steps = 10
        });
        await RunGeneration(id);

        var payload = (await GetRoadmap(id, TestUsers.UserA)).GetProperty("payload");
        payload.GetProperty("steps").GetArrayLength().Should().Be(0);
    }

    // ---- Goal mode ----------------------------------------------------------

    [Fact]
    public async Task GoalMode_UnknownDeck_IsRejected()
    {
        await MakeFull(TestUsers.UserA);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/roadmaps")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new
                      {
                          name = "Bad goal", mode = "goal", goalDeckId = 999999, definition = Definition()
                      });

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GoalMode_WithoutGoalDeck_IsRejected()
    {
        await MakeFull(TestUsers.UserA);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/roadmaps")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new { name = "No goal", mode = "goal", definition = Definition() });

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GoalMode_AlreadyReadableGoal_ReportsReachedWithNoSteps()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);

        var id = await CreateOk(TestUsers.UserA, mode: "goal", goalDeckId: _deckIds["teaches1"]);
        await RunGeneration(id);

        var payload = (await GetRoadmap(id, TestUsers.UserA)).GetProperty("payload");
        payload.GetProperty("goalReached").GetBoolean().Should().BeTrue();
        payload.GetProperty("steps").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GoalMode_UnreachableGoal_ReportsRemainingWordsInsteadOfAFakePath()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);

        var id = await CreateOk(TestUsers.UserA, mode: "goal", goalDeckId: _deckIds["hard"]);
        await RunGeneration(id);

        var payload = (await GetRoadmap(id, TestUsers.UserA)).GetProperty("payload");
        payload.GetProperty("goalReached").GetBoolean().Should().BeFalse();
        payload.GetProperty("goalWordsRemaining").GetInt32().Should().BeGreaterThan(0);
    }

    // ---- Prerequisite ordering ----------------------------------------------

    /// <summary>
    /// Goes through the real loader rather than hand-feeding the engine a prerequisite map — the engine-level
    /// tests cannot catch the relationship rows being read in the wrong direction.
    /// Only primary types are stored (<c>DeckRelationship.IsPrimaryRelationship</c>), and
    /// <c>source --Sequel--> target</c> means the source is the sequel, so the target must be scheduled first.
    /// </summary>
    [Fact]
    public async Task Generation_SchedulesAPrequelBeforeItsSequel()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);

        using (var scope = factory.Services.CreateScope())
        {
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            // "teaches3" teaches the most and would otherwise be step 1; make it the sequel of "teaches1".
            jitenDb.DeckRelationships.Add(new DeckRelationship
            {
                SourceDeckId = _deckIds["teaches3"],
                TargetDeckId = _deckIds["teaches1"],
                RelationshipType = DeckRelationshipType.Sequel
            });
            await jitenDb.SaveChangesAsync();
        }

        var id = await CreateOk(TestUsers.UserA, new { mediaTypes = new[] { (int)MediaType.Anime } });
        await RunGeneration(id);

        var steps = StepDeckIds(await GetRoadmap(id, TestUsers.UserA));

        steps.IndexOf(_deckIds["teaches1"]).Should()
             .BeLessThan(steps.IndexOf(_deckIds["teaches3"]),
                         "the prequel must come first even though the sequel scores higher");
    }

    // ---- Swap ---------------------------------------------------------------

    [Fact]
    public async Task Swap_ReplacesTheStepAndKeepsEarlierStepsIntact()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);

        var id = await CreateOk(TestUsers.UserA, new { mediaTypes = new[] { (int)MediaType.Anime } });
        await RunGeneration(id);

        var before = StepDeckIds(await GetRoadmap(id, TestUsers.UserA));
        before.Should().HaveCount(3);

        // Swap step 2; step 1 must survive and the rejected deck must disappear.
        var swap = await _client.SendAsync(
                       new HttpRequestMessage(HttpMethod.Post, $"/api/roadmaps/{id}/steps/2/swap").WithUser(TestUsers.UserA));
        swap.StatusCode.Should().Be(HttpStatusCode.OK);
        await RunGeneration(id);

        var after = StepDeckIds(await GetRoadmap(id, TestUsers.UserA));
        after[0].Should().Be(before[0], "the accepted prefix is pinned");
        after.Should().NotContain(before[1], "the rejected title is barred from this roadmap");
    }

    [Fact]
    public async Task Swap_UnknownStep_IsRejected()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);
        var id = await CreateOk(TestUsers.UserA);
        await RunGeneration(id);

        var response = await _client.SendAsync(
                           new HttpRequestMessage(HttpMethod.Post, $"/api/roadmaps/{id}/steps/99/swap").WithUser(TestUsers.UserA));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResetSwaps_RestoresThePreviouslyRejectedTitle()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);

        var id = await CreateOk(TestUsers.UserA, new { mediaTypes = new[] { (int)MediaType.Anime } });
        await RunGeneration(id);
        var original = StepDeckIds(await GetRoadmap(id, TestUsers.UserA));

        await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/roadmaps/{id}/steps/1/swap")
                                    .WithUser(TestUsers.UserA));
        await RunGeneration(id);
        StepDeckIds(await GetRoadmap(id, TestUsers.UserA)).Should().NotContain(original[0]);

        await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/roadmaps/{id}/reset-swaps")
                                    .WithUser(TestUsers.UserA));
        await RunGeneration(id);

        StepDeckIds(await GetRoadmap(id, TestUsers.UserA)).Should().Equal(original);
    }

    // ---- Lifecycle ----------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesTheRoadmap()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);
        var id = await CreateOk(TestUsers.UserA);

        var deleted = await _client.SendAsync(
                          new HttpRequestMessage(HttpMethod.Delete, $"/api/roadmaps/{id}").WithUser(TestUsers.UserA));
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);

        var fetch = await _client.SendAsync(
                        new HttpRequestMessage(HttpMethod.Get, $"/api/roadmaps/{id}").WithUser(TestUsers.UserA));
        fetch.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Roadmaps_AreCapped()
    {
        await MakeFull(TestUsers.UserA);
        await GiveKnownVocabulary(TestUsers.UserA);

        int cap;
        using (var scope = factory.Services.CreateScope())
            cap = scope.ServiceProvider.GetRequiredService<IOptions<JitenPlusLimitsOptions>>().Value.Roadmaps.Plus;

        for (var i = 0; i < cap; i++)
            await CreateOk(TestUsers.UserA);

        var overflow = new HttpRequestMessage(HttpMethod.Post, "/api/roadmaps")
                       .WithUser(TestUsers.UserA)
                       .WithJsonContent(new { name = "One too many", mode = "discovery", definition = Definition() });

        (await _client.SendAsync(overflow)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Defaults_SuggestBandsPerFamily_FromCompletedMedia()
    {
        await MakeFull(TestUsers.UserA);
        await SetDeckStatus(TestUsers.UserA, _deckIds["teaches1"], DeckStatus.Completed);   // Anime, 2.0
        await SetDeckStatus(TestUsers.UserA, _deckIds["novel"], DeckStatus.Completed);      // Novel, 4.5

        var response = await _client.SendAsync(
                           new HttpRequestMessage(HttpMethod.Get, "/api/roadmaps/defaults").WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        dto.GetProperty("hasBands").GetBoolean().Should().BeTrue();
        dto.GetProperty("showsDifficultyMax").GetDouble().Should().BeApproximately(3.0, 0.01);
        dto.GetProperty("novelsDifficultyMax").GetDouble().Should().BeApproximately(5.0, 0.01);
        dto.GetProperty("novelsDifficultyMin").GetDouble().Should()
           .BeGreaterThan(dto.GetProperty("showsDifficultyMin").GetDouble(),
                          "a novel history must not drag the anime band upward, or vice versa");
    }

    [Fact]
    public async Task Defaults_WithNoCompletedMedia_SuggestNoBands()
    {
        await MakeFull(TestUsers.UserA);

        var response = await _client.SendAsync(
                           new HttpRequestMessage(HttpMethod.Get, "/api/roadmaps/defaults").WithUser(TestUsers.UserA));

        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        dto.GetProperty("hasBands").GetBoolean().Should().BeFalse();
    }

    // ---- Preview ------------------------------------------------------------

    /// <summary>Writes the builder's preview source: per-deck coverage as basis points in 1024-deck chunks.</summary>
    private async Task SetStoredCoverage(string userId, UserCoverageMetric metric, params (string Deck, double Percent)[] entries)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var chunks = new Dictionary<int, short[]>();
        foreach (var (deck, percent) in entries)
        {
            var deckId = _deckIds[deck];
            var chunkIndex = deckId / 1024;
            if (!chunks.TryGetValue(chunkIndex, out var values))
                chunks[chunkIndex] = values = new short[1024];
            values[deckId % 1024] = (short)Math.Round(percent * 100);
        }

        foreach (var (chunkIndex, values) in chunks)
        {
            userDb.UserCoverageChunks.Add(new UserCoverageChunk
            {
                UserId = userId, Metric = (short)metric, ChunkIndex = chunkIndex, Values = values
            });
        }

        await userDb.SaveChangesAsync();
    }

    private async Task<JsonElement> Preview(string userId, object? definitionOverrides = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/roadmaps/preview")
                      .WithUser(userId)
                      .WithJsonContent(Definition(definitionOverrides));

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Preview_WithoutJitenPlus_Returns403()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/roadmaps/preview")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(Definition());

        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Preview_CountsTitlesClearingTheFloorAndTheComfortTarget()
    {
        await MakeFull(TestUsers.UserA);
        await SetStoredCoverage(TestUsers.UserA, UserCoverageMetric.MatureCoverage,
                                ("teaches1", 96), ("teaches2", 92), ("teaches3", 40));

        var dto = await Preview(TestUsers.UserA, new { comprehensionFloor = 0.90, comfortTarget = 0.95 });

        dto.GetProperty("hasCoverageData").GetBoolean().Should().BeTrue();
        dto.GetProperty("aboveFloor").GetInt32().Should().Be(2);
        dto.GetProperty("aboveComfort").GetInt32().Should().Be(1);
        dto.GetProperty("candidates").GetInt32().Should().BeGreaterThan(2);
    }

    [Fact]
    public async Task Preview_CountsYoungCoverage_OnlyWhenLearningWordsCount()
    {
        await MakeFull(TestUsers.UserA);
        await SetStoredCoverage(TestUsers.UserA, UserCoverageMetric.MatureCoverage, ("teaches1", 60));
        await SetStoredCoverage(TestUsers.UserA, UserCoverageMetric.YoungCoverage, ("teaches1", 35));

        var withYoung = await Preview(TestUsers.UserA, new { comprehensionFloor = 0.90, includeLearningWords = true });
        var matureOnly = await Preview(TestUsers.UserA, new { comprehensionFloor = 0.90, includeLearningWords = false });

        withYoung.GetProperty("aboveFloor").GetInt32().Should().Be(1);
        matureOnly.GetProperty("aboveFloor").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Preview_WithNoStoredCoverage_ReportsUnknownRatherThanNothingReadable()
    {
        await MakeFull(TestUsers.UserA);

        var dto = await Preview(TestUsers.UserA);

        dto.GetProperty("hasCoverageData").GetBoolean().Should().BeFalse();
        dto.GetProperty("candidates").GetInt32().Should().BeGreaterThan(0);
        dto.GetProperty("aboveFloor").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Preview_ExcludesTitlesTheUserCannotBeOffered()
    {
        await MakeFull(TestUsers.UserA);

        var before = await Preview(TestUsers.UserA);
        await SetDeckStatus(TestUsers.UserA, _deckIds["teaches1"], DeckStatus.Completed);
        await SetDeckStatus(TestUsers.UserA, _deckIds["teaches2"], DeckStatus.Dropped);
        await SetDeckStatus(TestUsers.UserA, _deckIds["teaches3"], DeckStatus.Ongoing);
        var after = await Preview(TestUsers.UserA);

        after.GetProperty("matchingFilters").GetInt32().Should().Be(before.GetProperty("matchingFilters").GetInt32() - 3);
        after.GetProperty("candidates").GetInt32().Should().Be(before.GetProperty("candidates").GetInt32() - 3);
    }

    [Fact]
    public async Task Preview_HonoursTheMediaTypeFilter()
    {
        await MakeFull(TestUsers.UserA);

        var dto = await Preview(TestUsers.UserA, new { mediaTypes = new[] { (int)MediaType.Novel } });

        dto.GetProperty("matchingFilters").GetInt32().Should().Be(1);
    }
}
