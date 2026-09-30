using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class SentenceComprehensionTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int Known1 = 1000001, Known2 = 1000002, Unknown1 = 1000003, Unknown2 = 1000004;

    private static int K(int wordId) => ExampleSentenceTokens.WordKey(wordId, 0);

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.UserCoverageChunks.ExecuteDeleteAsync();
        await userDb.UserMetadatas.ExecuteDeleteAsync();
        foreach (var user in await userDb.Users.ToListAsync())
        {
            user.StripeSubscriptionActive = false;
            user.IsLifetime = false;
            // UserA holds Jiten+; UserB stays free so the gate can be exercised.
            user.AdminPremiumOverride = user.Id == TestUsers.UserA;
        }

        foreach (var wordId in new[] { Known1, Known2 })
        foreach (var userId in new[] { TestUsers.UserA, TestUsers.UserB })
            userDb.FsrsCards.Add(new FsrsCard(userId, wordId, 0) { State = FsrsState.Mastered });
        await userDb.SaveChangesAsync();

        var jitenPlus = scope.ServiceProvider.GetRequiredService<IJitenPlusService>();
        foreach (var id in new[] { TestUsers.UserA, TestUsers.UserB, TestUsers.Admin })
            jitenPlus.InvalidateTier(id);
        scope.ServiceProvider.GetRequiredService<SentenceStatsCache>().Cache.Clear();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedBrowseAsync(bool withSentenceMetrics = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        db.Decks.AddRange(
            new Deck { DeckId = 1, OriginalTitle = "Deck One", MediaType = MediaType.Novel, Difficulty = 2.0f },
            new Deck { DeckId = 2, OriginalTitle = "Deck Two", MediaType = MediaType.Novel, Difficulty = 2.0f },
            new Deck { DeckId = 3, OriginalTitle = "Deck Three", MediaType = MediaType.Novel, Difficulty = 2.0f });
        await db.SaveChangesAsync();

        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        foreach (var userId in new[] { TestUsers.UserA, TestUsers.UserB })
        {
            userDb.UserCoverageChunks.Add(Chunk(userId, UserCoverageMetric.MatureCoverage, 50, 60, 70));
            if (!withSentenceMetrics) continue;
            userDb.UserCoverageChunks.Add(Chunk(userId, UserCoverageMetric.ReadableSentences, 30, 10, SentenceCoverageWriter.NoData));
            userDb.UserCoverageChunks.Add(Chunk(userId, UserCoverageMetric.IPlusOneSentences, 5, 40, SentenceCoverageWriter.NoData));
        }

        await userDb.SaveChangesAsync();
    }

    private static UserCoverageChunk Chunk(string userId, UserCoverageMetric metric, float deck1, float deck2, float deck3)
    {
        var values = new short[1024];
        values[1] = Bp(deck1);
        values[2] = Bp(deck2);
        values[3] = Bp(deck3);
        return new UserCoverageChunk { UserId = userId, Metric = (short)metric, ChunkIndex = 0, Values = values };

        static short Bp(float percent) => percent < 0 ? SentenceCoverageWriter.NoData : (short)(percent * 100);
    }

    private async Task<JsonElement> GetDecksAsync(string query, string userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/media-deck/get-media-decks{query}").WithUser(userId);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<int> OrderedIds(JsonElement body) =>
        body.GetProperty("data").EnumerateArray().Select(d => d.GetProperty("deckId").GetInt32()).ToList();

    private static JsonElement DeckById(JsonElement body, int id) =>
        body.GetProperty("data").EnumerateArray().Single(d => d.GetProperty("deckId").GetInt32() == id);

    [Fact]
    public async Task Browse_JitenPlusSortsByReadableWithUnprofiledDecksLast()
    {
        await SeedBrowseAsync();

        var body = await GetDecksAsync("?sortBy=readable&sortOrder=1", TestUsers.UserA);

        OrderedIds(body).Should().Equal(1, 2, 3);
        DeckById(body, 1).GetProperty("readableSentences").GetSingle().Should().Be(30);
        DeckById(body, 3).GetProperty("readableSentences").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Browse_JitenPlusSortsByIPlusOne()
    {
        await SeedBrowseAsync();

        var body = await GetDecksAsync("?sortBy=iPlusOne&sortOrder=1", TestUsers.UserA);

        OrderedIds(body).Take(2).Should().Equal(2, 1);
        DeckById(body, 2).GetProperty("iPlusOneSentences").GetSingle().Should().Be(40);
    }

    [Fact]
    public async Task Browse_FreeUserGetsNoSentenceValues()
    {
        await SeedBrowseAsync();

        var body = await GetDecksAsync("?sortBy=readable&sortOrder=1", TestUsers.UserB);

        body.GetProperty("data").EnumerateArray().Should().OnlyContain(d =>
            d.GetProperty("readableSentences").ValueKind == JsonValueKind.Null
            && d.GetProperty("iPlusOneSentences").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Browse_JitenPlusWithoutSentenceMetricsMarksCoverageDirty()
    {
        await SeedBrowseAsync(withSentenceMetrics: false);

        await GetDecksAsync("", TestUsers.UserA);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await userDb.UserMetadatas.AnyAsync(m => m.UserId == TestUsers.UserA && m.CoverageDirty)).Should().BeTrue();
        (await userDb.UserMetadatas.AnyAsync(m => m.UserId == TestUsers.UserB && m.CoverageDirty)).Should().BeFalse();
    }

    /// <summary>Two episodes: episode 1 is readable, i+1, i+1; episode 2 is i+2 and readable.</summary>
    private async Task<int> SeedSeriesAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var parent = new Deck { OriginalTitle = "Series", MediaType = MediaType.Anime };
        db.Decks.Add(parent);
        await db.SaveChangesAsync();

        var episode1 = new Deck { OriginalTitle = "Episode 1", MediaType = MediaType.Anime, ParentDeckId = parent.DeckId, DeckOrder = 1 };
        var episode2 = new Deck { OriginalTitle = "Episode 2", MediaType = MediaType.Anime, ParentDeckId = parent.DeckId, DeckOrder = 2 };
        db.Decks.AddRange(episode1, episode2);
        await db.SaveChangesAsync();

        db.DeckSentenceProfiles.AddRange(
            new DeckSentenceProfile
            {
                DeckId = episode1.DeckId, Version = SentenceProfileCodec.FormatVersion, SentenceCount = 3,
                Profile = SentenceProfileCodec.Encode([new[] { K(Known1), K(Known2) }, new[] { K(Known1), K(Unknown1) }, new[] { K(Unknown1) }])
            },
            new DeckSentenceProfile
            {
                DeckId = episode2.DeckId, Version = SentenceProfileCodec.FormatVersion, SentenceCount = 2,
                Profile = SentenceProfileCodec.Encode([new[] { K(Unknown1), K(Unknown2) }, new[] { K(Known2) }])
            });

        db.ExampleSentences.AddRange(
            Sentence(episode1.DeckId, "知る未知", (Known1, false), (Unknown1, false)),
            Sentence(episode1.DeckId, "知る知る", (Known1, false), (Known2, false)),
            Sentence(episode2.DeckId, "未知未知", (Unknown1, false), (Unknown2, false)),
            Sentence(episode2.DeckId, "知るがの", (Known1, false), (Unknown2, true)));
        await db.SaveChangesAsync();
        return parent.DeckId;
    }

    private static ExampleSentence Sentence(int deckId, string text, params (int WordId, bool FunctionWord)[] words)
    {
        var tokens = words.Select((w, i) => new SentenceToken(w.WordId, 0, (byte)(i * 2), 2, IsTarget: i == 0, IsFunctionWord: w.FunctionWord))
                          .ToArray();
        return new ExampleSentence
        {
            DeckId = deckId, Text = text, Difficulty = 1,
            Tokens = ExampleSentenceTokens.Encode(tokens),
            WordKeys = ExampleSentenceTokens.WordKeys(tokens, 0)
        };
    }

    [Fact]
    public async Task SentenceStats_FreeUserIsRefused()
    {
        var deckId = await SeedSeriesAsync();

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/media-deck/{deckId}/sentence-stats").WithUser(TestUsers.UserB));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SentenceStats_CountsEveryEpisodeAndLearnsTheUnlockingWordFirst()
    {
        var deckId = await SeedSeriesAsync();

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/media-deck/{deckId}/sentence-stats").WithUser(TestUsers.UserA));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("hasData").GetBoolean().Should().BeTrue();
        body.GetProperty("total").GetInt32().Should().Be(5);
        body.GetProperty("readable").GetInt32().Should().Be(2);
        body.GetProperty("oneUnknown").GetInt32().Should().Be(2);
        body.GetProperty("twoUnknown").GetInt32().Should().Be(1);
        body.GetProperty("segmentsArePart").GetBoolean().Should().BeTrue();

        var segments = body.GetProperty("segments").EnumerateArray().ToList();
        segments.Should().HaveCount(2);
        segments[0].GetProperty("readable").GetInt32().Should().Be(1);
        segments[0].GetProperty("title").GetString().Should().Be("Episode 1");

        var first = body.GetProperty("learnNext").EnumerateArray().First();
        first.GetProperty("word").GetProperty("wordId").GetInt32().Should().Be(Unknown1);
        first.GetProperty("unlocked").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task SentenceSummary_SkipsTheLearnNextOrder()
    {
        var deckId = await SeedSeriesAsync();

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/media-deck/{deckId}/sentence-summary").WithUser(TestUsers.UserA));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("readable").GetInt32().Should().Be(2);
        body.GetProperty("learnNext").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task SentenceSummary_PicksUpProfilesBuiltAfterAnEarlierRequest()
    {
        var deckId = await SeedSeriesAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            await db.DeckSentenceProfiles.ExecuteUpdateAsync(p => p.SetProperty(x => x.Profile, (byte[]?)null));
        }

        var before = await GetSummaryAsync(deckId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            var profile = SentenceProfileCodec.Encode([new[] { K(Known1) }, new[] { K(Unknown1) }]);
            await db.DeckSentenceProfiles.ExecuteUpdateAsync(p => p.SetProperty(x => x.Profile, profile).SetProperty(x => x.BuiltAt, DateTime.UtcNow));
        }

        var after = await GetSummaryAsync(deckId);

        before.GetProperty("hasData").GetBoolean().Should().BeFalse();
        after.GetProperty("hasData").GetBoolean().Should().BeTrue();
        after.GetProperty("profiledParts").GetInt32().Should().Be(2);
    }

    private async Task<JsonElement> GetSummaryAsync(int deckId)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/media-deck/{deckId}/sentence-summary").WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<int> SeedLongNovelAsync(int sentences)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var novel = new Deck { OriginalTitle = "Long Novel", MediaType = MediaType.Novel };
        db.Decks.Add(novel);
        await db.SaveChangesAsync();

        // The first half is readable, so an evenly spread sample lands on 50%.
        var profile = SentenceProfileCodec.Encode(Enumerable.Range(0, sentences)
                                                            .Select(i => (IReadOnlyCollection<int>)(i < sentences / 2 ? [K(Known1)] : [K(Unknown1)]))
                                                            .ToList());
        db.DeckSentenceProfiles.Add(new DeckSentenceProfile
        {
            DeckId = novel.DeckId, Profile = profile, SentenceCount = sentences, Version = SentenceProfileCodec.FormatVersion
        });
        await db.SaveChangesAsync();
        return novel.DeckId;
    }

    [Fact]
    public async Task RebuildSample_StoresNoCopyForAShortTitle()
    {
        var seriesId = await SeedSeriesAsync();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<JitenDbContext>>();

        await Jiten.Core.Services.SentenceProfileService.RebuildSampleAsync(contextFactory, seriesId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        (await db.DeckSentenceProfiles.AnyAsync(p => p.DeckId == seriesId)).Should().BeFalse();
    }

    [Fact]
    public async Task RebuildSample_CapsALongTitleAtTheSampleSize()
    {
        var novelId = await SeedLongNovelAsync(6000);
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<JitenDbContext>>();

        await Jiten.Core.Services.SentenceProfileService.RebuildSampleAsync(contextFactory, novelId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var row = await db.DeckSentenceProfiles.SingleAsync(p => p.DeckId == novelId);
        row.SampleCount.Should().Be(Jiten.Core.Services.SentenceProfileService.SampleSize);
    }

    [Fact]
    public async Task CountByRoot_ReadsShortTitlesWholeAndLongOnesThroughTheirSample()
    {
        var seriesId = await SeedSeriesAsync();
        var novelId = await SeedLongNovelAsync(6000);
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<JitenDbContext>>();
        await Jiten.Core.Services.SentenceProfileService.RebuildSampleAsync(contextFactory, novelId);
        var known = new HashSet<int> { K(Known1), K(Known2) };

        var all = await SentenceCoverageWriter.CountByRootAsync(contextFactory, known);
        var onlyNovel = await SentenceCoverageWriter.CountByRootAsync(contextFactory, known, [novelId]);

        all[seriesId].Should().Be((5, 2, 2));
        all[novelId].Should().Be((5000, 2500, 2500));
        onlyNovel.Keys.Should().Equal(novelId);
    }

    [Fact]
    public async Task IPlusOneSentences_ListsOnlySentencesWithOneUnknownContentWord()
    {
        var deckId = await SeedSeriesAsync();

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/media-deck/{deckId}/iplusone-sentences").WithUser(TestUsers.UserA));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var sentences = body.GetProperty("sentences").EnumerateArray().ToList();
        // "知るがの" has one unknown token, but it is a particle; it is fully readable, not i+1.
        sentences.Select(s => s.GetProperty("sentence").GetProperty("text").GetString()).Should().Equal("知る未知");
        sentences[0].GetProperty("word").GetProperty("wordId").GetInt32().Should().Be(Unknown1);
        sentences[0].GetProperty("sentence").GetProperty("wordPosition").GetInt32().Should().Be(2);
    }
}
