using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hangfire.Common;
using Hangfire.States;
using Hangfire;
using Jiten.Api.Jobs;
using Jiten.Api.Services.SmartDeck;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.User;
using Jiten.Core.Data;
using Jiten.Core.Services.SmartDeck;
using Jiten.Core;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Jiten.Parser.Tests.Integration;

public class SmartDeckEndpointTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.FsrsCards.RemoveRange(userDb.FsrsCards.Where(c => c.UserId == TestUsers.UserA));
        userDb.UserSettings.RemoveRange(userDb.UserSettings.Where(s => s.UserId == TestUsers.UserA));
        await userDb.SaveChangesAsync();
        await SetPlus(true);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SetPlus(bool plus)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var user = await userDb.Users.FirstAsync(u => u.Id == TestUsers.UserA);
        user.AdminPremiumOverride = plus;
        await userDb.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<Jiten.Api.Services.IJitenPlusService>().InvalidateTier(TestUsers.UserA);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path).WithUser(TestUsers.UserA);
        if (body != null) request.WithJsonContent(body);
        return await _client.SendAsync(request);
    }

    private async Task<(int ParentId, int Ep1, int Ep2)> SeedSeries()
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        var parent = new Deck { OriginalTitle = "Series", MediaType = MediaType.Anime };
        jitenDb.Decks.Add(parent);
        await jitenDb.SaveChangesAsync();
        var ep1 = new Deck { OriginalTitle = "Ep 1", MediaType = MediaType.Anime, ParentDeckId = parent.DeckId, DeckOrder = 0 };
        var ep2 = new Deck { OriginalTitle = "Ep 2", MediaType = MediaType.Anime, ParentDeckId = parent.DeckId, DeckOrder = 1 };
        jitenDb.Decks.AddRange(ep1, ep2);
        await jitenDb.SaveChangesAsync();

        if (!await jitenDb.JMDictWords.AnyAsync(w => w.WordId == 7001))
        {
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = 7001, PartsOfSpeech = ["n"] });
            jitenDb.WordForms.Add(new JmDictWordForm { WordId = 7001, ReadingIndex = 0, Text = "言葉", RubyText = "言葉[ことば]", FormType = JmDictFormType.KanjiForm });
            await jitenDb.SaveChangesAsync();
        }

        jitenDb.DeckWords.AddRange(
            new DeckWord { Deck = parent, WordId = 7001, ReadingIndex = 0, Occurrences = 12 },
            new DeckWord { Deck = ep2, WordId = 7001, ReadingIndex = 0, Occurrences = 4 });
        await jitenDb.SaveChangesAsync();

        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserDeckPreferences.AddRange(
            new UserDeckPreference { UserId = TestUsers.UserA, DeckId = parent.DeckId, Status = DeckStatus.Ongoing },
            new UserDeckPreference { UserId = TestUsers.UserA, DeckId = ep1.DeckId, Status = DeckStatus.Completed });
        await userDb.SaveChangesAsync();

        return (parent.DeckId, ep1.DeckId, ep2.DeckId);
    }

    [Fact]
    public async Task Get_FreeUser_IsLocked_ButShowsTitles()
    {
        var (parentId, ep1, ep2) = await SeedSeries();
        await SetPlus(false);

        var response = await Send(HttpMethod.Get, "/api/srs/smart-deck");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("locked").GetBoolean().Should().BeTrue();
        body.GetProperty("exists").GetBoolean().Should().BeFalse();
        var titles = body.GetProperty("titles").EnumerateArray().ToList();
        titles.Should().HaveCount(1);
        titles[0].GetProperty("deckId").GetInt32().Should().Be(parentId);
        titles[0].GetProperty("boosted").GetBoolean().Should().BeTrue();
        titles[0].GetProperty("cursorDeckId").GetInt32().Should().Be(ep1);
        titles[0].GetProperty("window").EnumerateArray().Single().GetProperty("deckId").GetInt32().Should().Be(ep2);
    }

    [Fact]
    public async Task Get_PreviewsLookaheadAndSequenceOverrides_WithoutSaving()
    {
        var (parentId, _, ep2) = await SeedSeries();
        using (var scope = factory.Services.CreateScope())
        {
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            jitenDb.Decks.Add(new Deck { OriginalTitle = "Ep 3", MediaType = MediaType.Anime, ParentDeckId = parentId, DeckOrder = 2 });
            await jitenDb.SaveChangesAsync();
        }
        await SetPlus(true);

        var wide = await (await Send(HttpMethod.Get, "/api/srs/smart-deck?lookaheadUnits=2")).Content.ReadFromJsonAsync<JsonElement>();
        wide.GetProperty("titles").EnumerateArray().Single().GetProperty("window").GetArrayLength().Should().Be(2);

        var unordered = await (await Send(HttpMethod.Get, $"/api/srs/smart-deck?sequenceOverrides={{\"{parentId}\":false}}")).Content.ReadFromJsonAsync<JsonElement>();
        unordered.GetProperty("titles").EnumerateArray().Single().GetProperty("window").GetArrayLength().Should().Be(0);

        var stored = await (await Send(HttpMethod.Get, "/api/srs/smart-deck")).Content.ReadFromJsonAsync<JsonElement>();
        stored.GetProperty("titles").EnumerateArray().Single().GetProperty("window").EnumerateArray().Single().GetProperty("deckId").GetInt32().Should().Be(ep2);

        (await Send(HttpMethod.Get, "/api/srs/smart-deck?sequenceOverrides=nope")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_WithPreviewValues_ReturnsTheResultingCount()
    {
        await SeedSeries();
        await SetPlus(true);

        var plain = await (await Send(HttpMethod.Get, "/api/srs/smart-deck")).Content.ReadFromJsonAsync<JsonElement>();
        plain.GetProperty("preview").ValueKind.Should().Be(JsonValueKind.Null);

        var previewed = await (await Send(HttpMethod.Get, "/api/srs/smart-deck?targetPercentage=100")).Content.ReadFromJsonAsync<JsonElement>();
        var preview = previewed.GetProperty("preview");
        preview.GetProperty("words").GetInt32().Should().Be(1);
        preview.GetProperty("newWords").GetInt32().Should().Be(1);
        preview.GetProperty("windowWords").GetInt32().Should().Be(1, "the one word also appears in the upcoming episode");

        var saved = await (await Send(HttpMethod.Put, "/api/srs/smart-deck/settings", new { targetPercentage = 80 })).Content.ReadFromJsonAsync<SmartDeckSettings>();
        saved!.TargetPercentage.Should().Be(80);
    }

    [Fact]
    public async Task WriteEndpoints_AreGatedBehindJitenPlus()
    {
        await SetPlus(false);

        (await Send(HttpMethod.Put, "/api/srs/smart-deck/settings", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Post, "/api/srs/smart-deck/sources/1", new { action = "pin" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Post, "/api/srs/smart-deck/rebuild")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Settings_CreatesDeck_NormalisesAndMarksDirty()
    {
        var recorder = factory.Services.GetRequiredService<RecordingSmartDeckDirtyService>();
        recorder.Marks.Clear();

        var response = await Send(HttpMethod.Put, "/api/srs/smart-deck/settings", new
        {
            lookaheadUnits = 9, recencyHalfLifeDays = 11,
            pinnedDeckIds = new[] { 1, 2, 3, 4 }, excludedDeckIds = new[] { 4 }, excludeKana = true,
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var settings = await response.Content.ReadFromJsonAsync<SmartDeckSettings>();
        settings!.LookaheadUnits.Should().Be(3);
        settings.RecencyHalfLifeDays.Should().Be(14);
        settings.PinnedDeckIds.Should().Equal(1, 2, 3);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var deck = await userDb.UserStudyDecks.SingleAsync(sd => sd.UserId == TestUsers.UserA && sd.DeckType == StudyDeckType.Smart);
        deck.ExcludeKana.Should().BeTrue();
        deck.Name.Should().Be("Smart Deck");
        recorder.Marks.Should().Contain(TestUsers.UserA);
        (await recorder.IsEnabled(TestUsers.UserA)).Should().BeTrue();

        var status = await (await Send(HttpMethod.Get, "/api/srs/smart-deck")).Content.ReadFromJsonAsync<JsonElement>();
        status.GetProperty("exists").GetBoolean().Should().BeTrue();
        status.GetProperty("locked").GetBoolean().Should().BeFalse();
        deck.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Save_EnqueuesDirectly_WhenTheMarkCannotSchedule()
    {
        var recorder = factory.Services.GetRequiredService<RecordingSmartDeckDirtyService>();
        var jobs = Mock.Get(factory.Services.GetRequiredService<IBackgroundJobClient>());
        jobs.Invocations.Clear();
        recorder.MarkSucceeds = false;
        try
        {
            (await Send(HttpMethod.Put, "/api/srs/smart-deck/settings", new { })).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            recorder.MarkSucceeds = true;
        }

        jobs.Verify(j => j.Create(It.Is<Job>(job => job.Method.Name == nameof(SmartDeckJob.Rebuild)), It.IsAny<EnqueuedState>()), Times.Once);
    }

    [Fact]
    public async Task Status_ReportsBuilding_WhileARebuildIsPending()
    {
        var recorder = factory.Services.GetRequiredService<RecordingSmartDeckDirtyService>();
        await Send(HttpMethod.Put, "/api/srs/smart-deck/settings", new { });
        recorder.Pending = true;
        try
        {
            var status = await (await Send(HttpMethod.Get, "/api/srs/smart-deck")).Content.ReadFromJsonAsync<JsonElement>();
            status.GetProperty("building").GetBoolean().Should().BeTrue();
            status.GetProperty("wordCount").GetInt32().Should().Be(0);
        }
        finally
        {
            recorder.Pending = false;
        }
    }

    [Fact]
    public async Task Remove_DeletesTheDeck_AndResetsSettings()
    {
        var recorder = factory.Services.GetRequiredService<RecordingSmartDeckDirtyService>();
        await Send(HttpMethod.Put, "/api/srs/smart-deck/settings", new { pinnedDeckIds = new[] { 1 }, excludeKana = true });
        var status = await (await Send(HttpMethod.Get, "/api/srs/smart-deck")).Content.ReadFromJsonAsync<JsonElement>();
        var id = status.GetProperty("userStudyDeckId").GetInt32();

        (await Send(HttpMethod.Delete, $"/api/srs/study-decks/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await userDb.UserStudyDecks.AnyAsync(sd => sd.UserId == TestUsers.UserA && sd.DeckType == StudyDeckType.Smart)).Should().BeFalse();
        (await recorder.IsEnabled(TestUsers.UserA)).Should().BeFalse();

        status = await (await Send(HttpMethod.Get, "/api/srs/smart-deck")).Content.ReadFromJsonAsync<JsonElement>();
        status.GetProperty("exists").GetBoolean().Should().BeFalse();
        status.GetProperty("settings").GetProperty("pinnedDeckIds").GetArrayLength().Should().Be(0);
        status.GetProperty("excludeKana").GetBoolean().Should().BeFalse();
        (await PromoDismissed()).Should().BeTrue();
    }

    [Fact]
    public async Task Promo_StartsVisible_DismissPersists_EvenForFreeUsers()
    {
        await SetPlus(false);
        (await PromoDismissed()).Should().BeFalse();

        (await Send(HttpMethod.Post, "/api/srs/smart-deck/promo/dismiss")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PromoDismissed()).Should().BeTrue();

        await SetPlus(true);
        await Send(HttpMethod.Put, "/api/srs/smart-deck/settings", new { });
        (await PromoDismissed()).Should().BeTrue();
    }

    private async Task<bool> PromoDismissed()
    {
        var promo = await (await Send(HttpMethod.Get, "/api/srs/smart-deck/promo")).Content.ReadFromJsonAsync<JsonElement>();
        return promo.GetProperty("dismissed").GetBoolean();
    }

    [Fact]
    public async Task GenericDeckEndpoints_RejectTheSmartDeck()
    {
        await Send(HttpMethod.Put, "/api/srs/smart-deck/settings", new { });
        var status = await (await Send(HttpMethod.Get, "/api/srs/smart-deck")).Content.ReadFromJsonAsync<JsonElement>();
        var id = status.GetProperty("userStudyDeckId").GetInt32();

        (await Send(HttpMethod.Put, $"/api/srs/study-decks/{id}", new { order = 0 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"/api/srs/study-decks/{id}/words", new { wordId = 7001, readingIndex = 0 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SourceActions_PinIncludeExcludeClear()
    {
        var (parentId, _, ep2) = await SeedSeries();
        await Send(HttpMethod.Put, "/api/srs/smart-deck/settings", new { });

        (await Send(HttpMethod.Post, $"/api/srs/smart-deck/sources/{ep2}", new { action = "pin" })).StatusCode.Should().Be(HttpStatusCode.NotFound, "children cannot be sources");
        (await Send(HttpMethod.Post, $"/api/srs/smart-deck/sources/{parentId}", new { action = "dance" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var pinned = await (await Send(HttpMethod.Post, $"/api/srs/smart-deck/sources/{parentId}", new { action = "pin" })).Content.ReadFromJsonAsync<SmartDeckSettings>();
        pinned!.PinnedDeckIds.Should().Equal(parentId);

        var excluded = await (await Send(HttpMethod.Post, $"/api/srs/smart-deck/sources/{parentId}", new { action = "exclude" })).Content.ReadFromJsonAsync<SmartDeckSettings>();
        excluded!.PinnedDeckIds.Should().BeEmpty("an exclusion wins over a pin");
        excluded.ExcludedDeckIds.Should().Equal(parentId);

        var status = await (await Send(HttpMethod.Get, "/api/srs/smart-deck")).Content.ReadFromJsonAsync<JsonElement>();
        status.GetProperty("titles").GetArrayLength().Should().Be(0);

        var cleared = await (await Send(HttpMethod.Post, $"/api/srs/smart-deck/sources/{parentId}", new { action = "clear" })).Content.ReadFromJsonAsync<SmartDeckSettings>();
        cleared!.ExcludedDeckIds.Should().BeEmpty();
        cleared.IncludedDeckIds.Should().BeEmpty();
    }

    [Fact]
    public async Task StudyBatch_IntroducedCard_CarriesSmartReason()
    {
        var (parentId, _, ep2) = await SeedSeries();
        await Send(HttpMethod.Put, "/api/srs/smart-deck/settings", new { });
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ISmartDeckBuilder>().Rebuild(TestUsers.UserA);

        var response = await Send(HttpMethod.Get, "/api/srs/study-batch?limit=20");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var card = body.GetProperty("cards").EnumerateArray().Single(c => c.GetProperty("wordId").GetInt32() == 7001);

        card.GetProperty("sourceDeckName").GetString().Should().Be("Smart Deck");
        var reason = card.GetProperty("smartReason");
        reason.GetProperty("deckId").GetInt32().Should().Be(parentId);
        reason.GetProperty("unitDeckId").GetInt32().Should().Be(ep2);
    }

    [Fact]
    public async Task UnitReport_BucketsCardsByProgress()
    {
        var (parentId, ep1, _) = await SeedSeries();
        using (var scope = factory.Services.CreateScope())
        {
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            foreach (var wordId in new[] { 7002, 7003, 7004 })
            {
                if (!await jitenDb.JMDictWords.AnyAsync(w => w.WordId == wordId))
                {
                    jitenDb.JMDictWords.Add(new JmDictWord { WordId = wordId, PartsOfSpeech = ["n"] });
                    jitenDb.WordForms.Add(new JmDictWordForm { WordId = wordId, ReadingIndex = 0, Text = $"w{wordId}", RubyText = $"w{wordId}", FormType = JmDictFormType.KanjiForm });
                }
                jitenDb.DeckWords.Add(new DeckWord { DeckId = ep1, WordId = wordId, ReadingIndex = 0, Occurrences = wordId - 7000 });
            }
            await jitenDb.SaveChangesAsync();

            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var now = DateTime.UtcNow;
            userDb.FsrsCards.AddRange(
                new FsrsCard { UserId = TestUsers.UserA, WordId = 7002, ReadingIndex = 0, State = FsrsState.Learning, CreatedAt = now.AddDays(-1), Due = now },
                new FsrsCard { UserId = TestUsers.UserA, WordId = 7003, ReadingIndex = 0, State = FsrsState.Review, CreatedAt = now.AddDays(-60), LastReview = now.AddDays(-30), Due = now.AddDays(10) },
                new FsrsCard { UserId = TestUsers.UserA, WordId = 7004, ReadingIndex = 0, State = FsrsState.Review, CreatedAt = now.AddDays(-60), LastReview = now.AddDays(-2), Due = now.AddDays(3) });
            await userDb.SaveChangesAsync();
        }

        var response = await Send(HttpMethod.Get, $"/api/srs/smart-deck/unit-report/{ep1}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("parentDeckId").GetInt32().Should().Be(parentId);
        body.GetProperty("totalWords").GetInt32().Should().Be(3);
        body.GetProperty("trackedWords").GetInt32().Should().Be(3);
        body.GetProperty("learnedLast7Days").GetInt32().Should().Be(1);
        body.GetProperty("learning").GetInt32().Should().Be(1);
        body.GetProperty("mature").GetInt32().Should().Be(1);
        body.GetProperty("young").GetInt32().Should().Be(1);
        body.GetProperty("notYetStudied").GetInt32().Should().Be(0);
        body.GetProperty("matureExamples").EnumerateArray().Single().GetProperty("wordId").GetInt32().Should().Be(7003);

        await SetPlus(false);
        (await Send(HttpMethod.Get, $"/api/srs/smart-deck/unit-report/{ep1}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Rebuild_RequiresEnabled_ThenAccepts()
    {
        (await Send(HttpMethod.Post, "/api/srs/smart-deck/rebuild")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await Send(HttpMethod.Put, "/api/srs/smart-deck/settings", new { });
        (await Send(HttpMethod.Post, "/api/srs/smart-deck/rebuild")).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }
}
