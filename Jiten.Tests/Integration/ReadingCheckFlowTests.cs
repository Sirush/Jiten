using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Api.Services.ReadingCheck;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class ReadingCheckFlowTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private const int FirstWordId = 120_000;
    private const int Words = 2400;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, int> OccByText = [];
    private static List<int> _deckIds = [];

    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.CardBatches.ExecuteDeleteAsync();
        await userDb.UserSettings.ExecuteDeleteAsync();
        await userDb.ReadingCheckRuns.ExecuteDeleteAsync();

        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var seedWords = !await jitenDb.WordForms.AnyAsync(wf => wf.WordId == FirstWordId);

        var decks = Enumerable.Range(1, 3).Select(i => new Deck { OriginalTitle = $"Novel {i}", MediaType = MediaType.Novel }).ToList();
        jitenDb.Decks.AddRange(decks);
        await jitenDb.SaveChangesAsync();
        _deckIds = decks.Select(d => d.DeckId).ToList();

        var random = new Random(7);
        for (var i = 0; i < Words; i++)
        {
            var wordId = FirstWordId + i;
            var text = $"{(char)(0x5000 + i)}{(char)(0x7000 + i)}";
            var perDeck = Math.Max(2, (int)(400 * Math.Pow(0.997, i)) / 3);
            OccByText[text] = perDeck * 3;
            if (seedWords)
            {
                jitenDb.JMDictWords.Add(new JmDictWord { WordId = wordId, PartsOfSpeech = ["n"] });
                jitenDb.Definitions.Add(new JmDictDefinition { WordId = wordId, SenseIndex = 0, EnglishMeanings = ["meaning"], PartsOfSpeech = ["n"] });
                jitenDb.WordForms.Add(new JmDictWordForm
                {
                    WordId = wordId, ReadingIndex = 0, Text = text, RubyText = $"{text[0]}[か]{text[1]}[な]", FormType = JmDictFormType.KanjiForm
                });
                jitenDb.WordFormFrequencies.Add(new JmDictWordFormFrequency
                {
                    WordId = wordId, ReadingIndex = 0, FrequencyRank = 11000 + random.Next(30000)
                });
            }

            foreach (var deck in decks)
                jitenDb.DeckWords.Add(new DeckWord { DeckId = deck.DeckId, WordId = wordId, ReadingIndex = 0, Occurrences = perDeck });
        }

        await jitenDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<T> Post<T>(string url, object body)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, url).WithUser(TestUsers.UserA).WithJsonContent(body));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    [Fact]
    public async Task FullRun_SeedsFrequentWordsAndListsTheRestForStudy()
    {
        var count = await Post<ReadingCountDto>("/api/reading-check/count", new { deckIds = _deckIds });
        count.Units.Should().Be(3);
        count.Candidates.Should().BeGreaterThanOrEqualTo(ReadingCheckEngine.MinCandidates);

        var step = await Post<ReadingStepDto>("/api/reading-check/start", new { deckIds = _deckIds });
        while (step.Phase == "probe")
        {
            step.Items.Should().OnlyContain(i => i.Reading == "かな" && i.Meanings.Count > 0);
            var answers = step.Items.Select(i => new
            {
                id = i.Id, answer = OccByText[i.Text] >= 30 ? ReadingCheckAnswer.Knew : ReadingCheckAnswer.DidntKnow
            });
            step = await Post<ReadingStepDto>("/api/reading-check/answer", new { sessionId = step.SessionId, answers });
        }

        step.Phase.Should().Be("fit");
        step.Levels.Should().HaveCount(5);
        step.Levels.Single(l => l.Level == step.Level).Known.Should().BeGreaterThan(0);
        step.Levels.Select(l => l.Known).Should().BeInAscendingOrder();

        var verify = await Post<ReadingStepDto>("/api/reading-check/verify", new { sessionId = step.SessionId, level = 2 });
        verify.Phase.Should().Be("verify");
        verify.Items.Should().HaveCount(ReadingCheckEngine.ReviewPageSize);
        verify.Known.Should().BeGreaterThan(0);

        var unknown = verify.Items.Take(2).ToList();
        var second = await Post<ReadingStepDto>("/api/reading-check/review",
                                                new { sessionId = step.SessionId, unknownIds = unknown.Select(i => i.Id) });
        second.Phase.Should().Be("verify");
        second.Items.Should().HaveCount(ReadingCheckEngine.ReviewPageSize);
        second.Items.Select(i => i.Text).Should().NotIntersectWith(verify.Items.Select(i => i.Text));

        var result = await Post<ReadingResultDto>("/api/reading-check/confirm",
                                                  new { sessionId = step.SessionId, unknownIds = Array.Empty<int>() });

        result.Seeded.Should().BeGreaterThan(0);
        result.BatchId.Should().NotBeNull();
        result.StudyList.Should().NotBeEmpty().And.BeInDescendingOrder(w => w.Occurrences);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var seeded = await userDb.FsrsCards.AsNoTracking().Where(c => c.UserId == TestUsers.UserA && c.SourceBatchId == result.BatchId).ToListAsync();
        seeded.Should().HaveCount(result.Seeded).And.OnlyContain(c => c.Source == CardSource.ReadingCalibration && c.State == FsrsState.Mastered);

        var reviewedTexts = verify.Items.Concat(second.Items).ToDictionary(i => i.Text, i => unknown.Any(u => u.Id == i.Id));
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var reviewedIds = await jitenDb.WordForms.AsNoTracking().Where(wf => reviewedTexts.Keys.Contains(wf.Text))
                                       .ToDictionaryAsync(wf => wf.WordId, wf => reviewedTexts[wf.Text]);
        var seededIds = seeded.Select(c => c.WordId).ToHashSet();
        reviewedIds.Where(r => r.Value).Should().OnlyContain(r => !seededIds.Contains(r.Key));
        reviewedIds.Where(r => !r.Value).Should().OnlyContain(r => seededIds.Contains(r.Key));

        var onboarding = await scope.ServiceProvider.GetRequiredService<IOnboardingStore>().GetStateAsync(TestUsers.UserA);
        onboarding.ReadingCheck.Should().NotBeNull();
        onboarding.ReadingCheck!.Units.Should().Be(3);

        var run = await userDb.ReadingCheckRuns.AsNoTracking().SingleAsync(r => r.UserId == TestUsers.UserA);
        run.BatchId.Should().Be(result.BatchId);
        var logged = JsonSerializer.Deserialize<int[][]>(run.AnswersJson)!;
        logged.Should().NotBeEmpty().And.OnlyContain(a => a.Length == 8 && a[0] >= FirstWordId);
        logged.Count(a => a[6] == 1).Should().Be(verify.Items.Count + second.Items.Count);
    }

    [Fact]
    public async Task Writer_MarksWordsAsMasteredUnderOneBatch()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.UserMetadatas.ExecuteUpdateAsync(s => s.SetProperty(m => m.CoverageDirty, false));

        var writer = scope.ServiceProvider.GetRequiredService<ReadingCheckWriter>();
        var (seeded, batchId) = await writer.SeedAsync(TestUsers.UserA, [(FirstWordId, 0), (FirstWordId + 1, 0)], units: 2);

        seeded.Should().Be(2);
        var cards = await userDb.FsrsCards.AsNoTracking().Where(c => c.UserId == TestUsers.UserA).ToListAsync();
        cards.Should().HaveCount(2).And.OnlyContain(c => c.State == FsrsState.Mastered && c.SourceBatchId == batchId
                                                                                         && c.Source == CardSource.ReadingCalibration);
        var batch = await userDb.CardBatches.AsNoTracking().SingleAsync(b => b.BatchId == batchId);
        batch.CardState.Should().Be(FsrsState.Mastered);
        batch.Details.Should().Be(new CardBatchDetails { Kind = CardBatchKind.ReadingCheck, Units = 2 });
        (await userDb.UserMetadatas.AnyAsync(m => m.UserId == TestUsers.UserA && m.CoverageDirty)).Should().BeTrue();
    }
}
