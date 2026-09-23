using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class ReschedulePreviewTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int OverdueAfterReplayWordId = 515001;
    private const int NotDueAfterReplayWordId = 515002;
    private const int MasteredWordId = 515003;
    private const int NewWordId = 515004;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsReviewLogs.Where(l => l.Card.UserId == TestUsers.UserA).ExecuteDeleteAsync();
        await userDb.FsrsCards.Where(c => c.UserId == TestUsers.UserA).ExecuteDeleteAsync();
        await userDb.UserFsrsSettings.Where(s => s.UserId == TestUsers.UserA).ExecuteDeleteAsync();
        await userDb.UserStudyDeckWords.Where(w => w.StudyDeck.UserId == TestUsers.UserA).ExecuteDeleteAsync();
        await userDb.UserStudyDecks.Where(d => d.UserId == TestUsers.UserA).ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Preview_ProjectsReplayedDueDatesWithoutSaving()
    {
        await SeedCards();
        var before = await LoadDues();

        var body = await PostOk("/api/srs/settings/reschedule-preview", new { desiredRetentions = new[] { 0.8, 0.7 } });

        body.GetProperty("currentDue").GetInt32().Should().Be(1);
        var options = body.GetProperty("options").EnumerateArray().ToList();
        options.Select(o => o.GetProperty("desiredRetention").GetDouble()).Should().Equal(0.9, 0.8, 0.7);
        options.Select(o => o.GetProperty("due").GetInt32()).Should().AllBeEquivalentTo(1);

        (await LoadDues()).Should().Equal(before);
    }

    [Fact]
    public async Task Preview_RejectsInvalidRetentions()
    {
        var tooMany = await Post("/api/srs/settings/reschedule-preview", new { desiredRetentions = new[] { 0.9, 0.85, 0.8, 0.75, 0.7, 0.65, 0.6 } });
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var outOfRange = await Post("/api/srs/settings/reschedule-preview", new { desiredRetentions = new[] { 1.2 } });
        outOfRange.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ApplyWithReschedule_MatchesThePreview()
    {
        await SeedCards();

        await PostOk("/api/srs/settings/apply", new { desiredRetention = 0.8, reschedule = true });

        var dues = await LoadDues();
        var now = DateTime.UtcNow;
        dues[OverdueAfterReplayWordId].Should().BeBefore(now);
        dues[NotDueAfterReplayWordId].Should().BeAfter(now);
        (await StoredRetention()).Should().BeApproximately(0.8, 1e-9);
    }

    [Fact]
    public async Task ApplyWithoutReschedule_SavesRetentionOnly()
    {
        await SeedCards();
        var before = await LoadDues();

        var body = await PostOk("/api/srs/settings/apply", new { desiredRetention = 0.85, reschedule = false });

        body.GetProperty("rescheduled").GetBoolean().Should().BeFalse();
        (await StoredRetention()).Should().BeApproximately(0.85, 1e-9);
        (await LoadDues()).Should().Equal(before);
    }

    [Fact]
    public async Task Apply_RejectsParametersForAnotherModel()
    {
        var response = await Post("/api/srs/settings/apply",
                                  new { parameters = FsrsConstants.DefaultParametersV7, desiredRetention = 0.9, reschedule = false });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task OptimizePreview_SavesNothingUntilApplied()
    {
        await SeedTrainingHistory();

        var preview = await PostOk("/api/srs/settings/optimize/preview", new { desiredRetentions = new[] { 0.85 } });

        var values = preview.GetProperty("parameterValues").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        values.Should().HaveCount(FsrsConstants.DefaultParameters.Length);
        preview.GetProperty("preview").GetProperty("options").GetArrayLength().Should().Be(2);
        (await StoredParametersJson()).Should().BeNull();

        await PostOk("/api/srs/settings/apply", new { parameters = values, desiredRetention = 0.9, reschedule = false });

        JsonSerializer.Deserialize<double[]>((await StoredParametersJson())!).Should().Equal(values);
    }

    [Fact]
    public async Task StudyDecksOnly_DueNowMatchesTheStudyPage()
    {
        await SeedJmDictWords(1, 3);
        var createDeck = await Post("/api/srs/study-decks",
                                    new { deckType = 2, name = "Preview scope", downloadType = 1, order = 4, minFrequency = 0, maxFrequency = 0 });
        createDeck.StatusCode.Should().Be(HttpStatusCode.OK);
        var deckId = (await createDeck.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userStudyDeckId").GetInt32();
        var words = new[] { new { wordId = 1, readingIndex = 0, occurrences = 1 }, new { wordId = 3, readingIndex = 0, occurrences = 1 } };
        (await Post($"/api/srs/study-decks/{deckId}/words/batch", new { words })).StatusCode.Should().Be(HttpStatusCode.OK);

        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.FsrsCards.AddRange(
                Card(1, FsrsState.Review, now.AddDays(-1), now.AddDays(-120), now.AddDays(-119)),
                Card(2, FsrsState.Review, now.AddDays(-1), now.AddDays(-120), now.AddDays(-119)),
                Card(3, FsrsState.Review, now.AddDays(30), now.AddDays(-300), now.AddDays(-297), now.AddDays(-285), now.AddDays(-250),
                     now.AddDays(-160), now.AddDays(-1)));
            await userDb.SaveChangesAsync();
        }

        var settings = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-settings")
                                                .WithUser(TestUsers.UserA)
                                                .WithJsonContent(new { newCardsPerDay = 0, maxReviewsPerDay = 200, gradingButtons = 4, interleaving = "mixed", reviewFrom = "studyDecksOnly" }));
        settings.StatusCode.Should().Be(HttpStatusCode.OK);

        var summary = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/srs/due-summary").WithUser(TestUsers.UserA));
        summary.StatusCode.Should().Be(HttpStatusCode.OK);
        var reviewsDue = (await summary.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reviewsDue").GetInt32();

        var preview = await PostOk("/api/srs/settings/reschedule-preview", new { desiredRetentions = Array.Empty<double>() });

        reviewsDue.Should().Be(1);
        preview.GetProperty("currentDue").GetInt32().Should().Be(reviewsDue);
        preview.GetProperty("options")[0].GetProperty("due").GetInt32().Should().Be(1, "word 2 is overdue after replay but outside every study deck");
    }

    private async Task SeedJmDictWords(int from, int to)
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        for (var i = from; i <= to; i++)
        {
            if (!await jitenDb.JMDictWords.AnyAsync(w => w.WordId == i))
                jitenDb.JMDictWords.Add(new JmDictWord { WordId = i, PartsOfSpeech = ["noun"] });
            if (!await jitenDb.WordForms.AnyAsync(wf => wf.WordId == i))
                jitenDb.WordForms.Add(new JmDictWordForm { WordId = i, ReadingIndex = 0, Text = $"word{i}", RubyText = $"word{i}", FormType = JmDictFormType.KanaForm });
        }
        await jitenDb.SaveChangesAsync();
    }

    private async Task SeedCards()
    {
        var now = DateTime.UtcNow;
        var future = now.AddDays(30);
        var past = now.AddDays(-3);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.FsrsCards.AddRange(
            // Two early Goods give days of stability, so replay puts it months overdue.
            Card(OverdueAfterReplayWordId, FsrsState.Review, future, now.AddDays(-120), now.AddDays(-119)),
            // A long run of Goods ending yesterday lands well past today at any retention.
            Card(NotDueAfterReplayWordId, FsrsState.Review, past,
                 now.AddDays(-300), now.AddDays(-297), now.AddDays(-285), now.AddDays(-250), now.AddDays(-160), now.AddDays(-1)),
            Card(MasteredWordId, FsrsState.Mastered, past, now.AddDays(-120), now.AddDays(-119)),
            new FsrsCard(TestUsers.UserA, NewWordId, 0, state: FsrsState.New, due: past));
        await userDb.SaveChangesAsync();
    }

    /// <summary>Enough two-review cards to clear the optimiser's minimum.</summary>
    private async Task SeedTrainingHistory()
    {
        var start = DateTime.UtcNow.AddDays(-60);
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        for (var i = 0; i < FsrsOptimizer.MinimumReviews / 2 + 1; i++)
        {
            var first = start.AddHours(i);
            var card = Card(600000 + i, FsrsState.Review, DateTime.UtcNow.AddDays(5), first, first.AddDays(3 + i % 7));
            card.ReviewLogs.Last().Rating = i % 4 == 0 ? FsrsRating.Again : FsrsRating.Good;
            userDb.FsrsCards.Add(card);
        }
        await userDb.SaveChangesAsync();
    }

    private static FsrsCard Card(int wordId, FsrsState state, DateTime due, params DateTime[] goodReviews)
        => new(TestUsers.UserA, wordId, 0, state: state, due: due, lastReview: goodReviews[^1])
           {
               Stability = 10, Difficulty = 5,
               ReviewLogs = goodReviews.Select(t => new FsrsReviewLog { Rating = FsrsRating.Good, ReviewDateTime = t }).ToList()
           };

    private async Task<Dictionary<int, DateTime>> LoadDues()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.FsrsCards.AsNoTracking().Where(c => c.UserId == TestUsers.UserA).ToDictionaryAsync(c => c.WordId, c => c.Due);
    }

    private async Task<double?> StoredRetention()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.UserFsrsSettings.Where(s => s.UserId == TestUsers.UserA).Select(s => s.DesiredRetention).SingleOrDefaultAsync();
    }

    private async Task<string?> StoredParametersJson()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.UserFsrsSettings.Where(s => s.UserId == TestUsers.UserA).Select(s => s.ParametersJson).SingleOrDefaultAsync();
    }

    private Task<HttpResponseMessage> Post(string url, object body)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, url).WithUser(TestUsers.UserA).WithJsonContent(body));

    private async Task<JsonElement> PostOk(string url, object body)
    {
        var response = await Post(url, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
