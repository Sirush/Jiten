using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Jiten.Api.Dtos;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Jiten.Parser.Tests.Integration;

public class CardBatchTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private const int FirstWordId = 9501;
    private const int WordCount = 5;

    private readonly HttpClient _client = factory.CreateClient();

    private Mock<IBackgroundJobClient> Jobs => Mock.Get(factory.Services.GetRequiredService<IBackgroundJobClient>());

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        Jobs.Invocations.Clear();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.FsrsCardArchives.ExecuteDeleteAsync();
        await userDb.CardBatches.ExecuteDeleteAsync();
        await userDb.UserMetadatas.ExecuteDeleteAsync();

        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        if (!await jitenDb.WordForms.AnyAsync(wf => wf.WordId == FirstWordId))
        {
            string[] texts = ["本", "水", "山", "川", "空"];
            for (var i = 0; i < WordCount; i++)
            {
                var wordId = FirstWordId + i;
                jitenDb.JMDictWords.Add(new JmDictWord { WordId = wordId, PartsOfSpeech = ["noun"] });
                jitenDb.Definitions.Add(new JmDictDefinition { WordId = wordId, SenseIndex = 0, EnglishMeanings = ["thing"], PartsOfSpeech = ["noun"] });
                jitenDb.WordForms.Add(new JmDictWordForm { WordId = wordId, ReadingIndex = 0, Text = texts[i], RubyText = texts[i], FormType = JmDictFormType.KanjiForm });
                jitenDb.WordFormFrequencies.Add(new JmDictWordFormFrequency { WordId = wordId, ReadingIndex = 0, FrequencyRank = i + 1 });
            }

            await jitenDb.SaveChangesAsync();
        }

        scope.ServiceProvider.GetRequiredService<IWordFormSiblingCache>().Reload();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<JsonElement> ImportFrequency(int min, int max, string user = TestUsers.UserA)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/user/vocabulary/import-from-frequency/{min}/{max}")
                                               .WithUser(user));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<PaginatedResponse<List<CardBatchDto>>> ListBatches(int offset = 0, int limit = 20)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/user/vocabulary/batches?offset={offset}&limit={limit}")
                                               .WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PaginatedResponse<List<CardBatchDto>>>())!;
    }

    private Task<HttpResponseMessage> Undo(Guid batchId, string user = TestUsers.UserA)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/user/vocabulary/batches/{batchId}/undo").WithUser(user));

    private async Task<List<FsrsCard>> Cards()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.FsrsCards.AsNoTracking().Where(c => c.UserId == TestUsers.UserA).OrderBy(c => c.WordId).ToListAsync();
    }

    private async Task<List<CardBatch>> Batches()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.CardBatches.AsNoTracking().Where(b => b.UserId == TestUsers.UserA).ToListAsync();
    }

    private bool CoverageJobQueued() =>
        Jobs.Invocations
            .Where(i => i.Method.Name == nameof(IBackgroundJobClient.Create))
            .Select(i => (Job)i.Arguments[0])
            .Any(j => j.Type == typeof(ComputationJob) && j.Method.Name == nameof(ComputationJob.ComputeUserCoverage));

    [Fact]
    public async Task FrequencyImport_StampsSourceAndOneBatch_AndSkipsExistingCards()
    {
        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/user/vocabulary/add/{FirstWordId + 2}/0").WithUser(TestUsers.UserA)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await ImportFrequency(1, WordCount);
        result.GetProperty("forms").GetInt32().Should().Be(4);

        var cards = await Cards();
        cards.Should().HaveCount(WordCount);

        var manual = cards.Single(c => c.WordId == FirstWordId + 2);
        manual.Source.Should().Be(CardSource.ManualSingle);
        manual.SourceBatchId.Should().BeNull();

        var imported = cards.Where(c => c.WordId != FirstWordId + 2).ToList();
        imported.Should().OnlyContain(c => c.Source == CardSource.FrequencyImport && c.State == FsrsState.Mastered);
        var batchId = imported.Select(c => c.SourceBatchId).Distinct().Should().ContainSingle().Subject;
        batchId.Should().NotBeNull();
        result.GetProperty("batchId").GetGuid().Should().Be(batchId!.Value);

        var batch = (await Batches()).Should().ContainSingle().Subject;
        batch.BatchId.Should().Be(batchId.Value);
        batch.Source.Should().Be(CardSource.FrequencyImport);
        batch.CardCount.Should().Be(4);
        batch.CardState.Should().Be(FsrsState.Mastered);
        batch.Details.Should().Be(new CardBatchDetails { Kind = CardBatchKind.FrequencyRange, MinRank = 1, MaxRank = 5 });
        batch.Undone.Should().BeFalse();
    }

    [Fact]
    public async Task FrequencyImport_NothingNew_LeavesNoBatch()
    {
        await ImportFrequency(1, WordCount);
        var again = await ImportFrequency(1, WordCount);

        again.GetProperty("forms").GetInt32().Should().Be(0);
        again.GetProperty("batchId").ValueKind.Should().Be(JsonValueKind.Null);
        (await Batches()).Should().ContainSingle();
    }

    private async Task SeedWaniKaniForms()
    {
        var forms = WaniKaniVocabulary.FormsUpTo(2).ToList();
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        if (!await jitenDb.WordForms.AnyAsync(wf => wf.WordId == forms[0].WordId))
        {
            foreach (var wordId in forms.Select(f => f.WordId).Distinct())
                jitenDb.JMDictWords.Add(new JmDictWord { WordId = wordId, PartsOfSpeech = ["noun"] });
            foreach (var (wordId, readingIndex) in forms)
                jitenDb.WordForms.Add(new JmDictWordForm { WordId = wordId, ReadingIndex = readingIndex, Text = $"{wordId}-{readingIndex}", RubyText = "", FormType = JmDictFormType.KanjiForm });
            await jitenDb.SaveChangesAsync();
        }

        scope.ServiceProvider.GetRequiredService<IWordFormSiblingCache>().Reload();
    }

    [Fact]
    public async Task WaniKaniLevelImport_AddsOnlyLevelsUpToTheRequestedOne()
    {
        var levelOne = WaniKaniVocabulary.FormsUpTo(1).ToList();
        await SeedWaniKaniForms();

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import-from-wanikani-level/1").WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("forms").GetInt32().Should().Be(levelOne.Count);

        var cards = await Cards();
        cards.Select(c => (c.WordId, c.ReadingIndex)).Should().BeEquivalentTo(levelOne);
        cards.Should().OnlyContain(c => c.Source == CardSource.WaniKaniImport && c.State == FsrsState.Mastered);

        var batch = (await Batches()).Should().ContainSingle().Subject;
        batch.Source.Should().Be(CardSource.WaniKaniImport);
        batch.Details.Should().Be(new CardBatchDetails { Kind = CardBatchKind.WaniKaniLevels, MaxLevel = 1 });
    }

    [Fact]
    public async Task WaniKaniProgressImport_ResolvesWordsThroughTheLevelList()
    {
        await SeedWaniKaniForms();
        var nextReview = DateTime.UtcNow.Date.AddDays(3);
        var started = nextReview.AddDays(-200);
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import-from-wanikani")
                                               {
                                                   Content = JsonContent.Create(new
                                                                                {
                                                                                    words = new[]
                                                                                            {
                                                                                                new { characters = "大人", reading = "おとな", srsStage = 6, availableAt = (DateTime?)nextReview, startedAt = (DateTime?)started, burnedAt = (DateTime?)null, reviews = 9, incorrect = 3 },
                                                                                                new { characters = "〜才", reading = "さい", srsStage = 9, availableAt = (DateTime?)null, startedAt = (DateTime?)started, burnedAt = (DateTime?)started.AddDays(190), reviews = 8, incorrect = 0 },
                                                                                                new { characters = "〜", reading = "", srsStage = 5, availableAt = (DateTime?)null, startedAt = (DateTime?)null, burnedAt = (DateTime?)null, reviews = 0, incorrect = 0 },
                                                                                                new { characters = "二", reading = "に", srsStage = 0, availableAt = (DateTime?)null, startedAt = (DateTime?)null, burnedAt = (DateTime?)null, reviews = 0, incorrect = 0 }
                                                                                            }
                                                                                })
                                               }.WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        result.GetProperty("matched").GetInt32().Should().Be(2);
        result.GetProperty("unmatchedWords").EnumerateArray().Select(w => w.GetString()).Should().Equal("〜");
        WaniKaniVocabulary.TryResolve("大人", out var adult).Should().BeTrue();
        adult.WordId.Should().Be(1414170);
        WaniKaniVocabulary.TryResolve("〜才", out var yearsOld).Should().BeTrue();
        yearsOld.WordId.Should().Be(1294940);

        var cards = await Cards();
        cards.Select(c => (c.WordId, c.ReadingIndex)).Should().BeEquivalentTo([adult, yearsOld]);
        cards.Should().OnlyContain(c => c.Source == CardSource.WaniKaniImport);

        var guru = cards.Single(c => c.WordId == adult.WordId);
        guru.State.Should().Be(FsrsState.Review);
        guru.LastReview.Should().Be(nextReview.AddDays(-14));
        guru.Stability.Should().BePositive();
        guru.Lapses.Should().BePositive();
        var burned = cards.Single(c => c.WordId == yearsOld.WordId);
        burned.State.Should().Be(FsrsState.Mastered);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var guruLogs = await userDb.FsrsReviewLogs.AsNoTracking().Where(l => l.CardId == guru.CardId).OrderBy(l => l.ReviewDateTime).ToListAsync();
            guruLogs.Should().HaveCount(10);
            guruLogs.Count(l => l.Rating == FsrsRating.Again).Should().Be(2);
            guruLogs[0].ReviewDateTime.Should().Be(started);
            guruLogs[^1].ReviewDateTime.Should().Be(nextReview.AddDays(-14));
            guruLogs.Should().OnlyContain(l => l.State != null);
            (await userDb.FsrsReviewLogs.CountAsync(l => l.CardId == burned.CardId)).Should().Be(9);
        }

        var batch = (await Batches()).Should().ContainSingle().Subject;
        batch.Details.Kind.Should().Be(CardBatchKind.WaniKaniProgress);
        batch.CardState.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public async Task WaniKaniLevelImport_RejectsLevelsOutsideTheCourse(int level)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/user/vocabulary/import-from-wanikani-level/{level}").WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WaniKaniLevelCounts_CoverEveryLevelAndOnlyGrow()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/wanikani-levels").WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var counts = (await response.Content.ReadFromJsonAsync<List<int>>())!;

        counts.Should().HaveCount(WaniKaniVocabulary.MaxLevel).And.BeInAscendingOrder();
        counts[0].Should().BePositive();
    }

    [Fact]
    public async Task Undo_RemovesUntouchedCards_KeepsStudiedAndChanged_AndIsIdempotent()
    {
        await ImportFrequency(1, WordCount);
        var batchId = (await Batches()).Single().BatchId;

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var studied = await userDb.FsrsCards.SingleAsync(c => c.UserId == TestUsers.UserA && c.WordId == FirstWordId);
            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(studied.CardId, FsrsRating.Good, DateTime.UtcNow.AddSeconds(1)));
            await userDb.SaveChangesAsync();
        }

        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/user/vocabulary/blacklist/{FirstWordId + 1}/0").WithUser(TestUsers.UserA)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var listed = (await ListBatches()).Data.Should().ContainSingle().Subject;
        listed.RemovableCount.Should().Be(3);
        listed.CardCount.Should().Be(5);

        Jobs.Invocations.Clear();
        var response = await Undo(batchId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<CardBatchUndoResult>())!;
        result.Removed.Should().Be(3);
        result.Kept.Should().Be(2);
        result.AlreadyUndone.Should().BeFalse();
        CoverageJobQueued().Should().BeTrue();

        var remaining = await Cards();
        remaining.Select(c => c.WordId).Should().BeEquivalentTo([FirstWordId, FirstWordId + 1]);
        remaining.Single(c => c.WordId == FirstWordId + 1).State.Should().Be(FsrsState.Blacklisted);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            (await userDb.UserMetadatas.AnyAsync(m => m.UserId == TestUsers.UserA && m.CoverageDirty)).Should().BeTrue();
            (await userDb.FsrsCardArchives.AnyAsync(a => a.UserId == TestUsers.UserA)).Should().BeFalse();
        }

        var afterUndo = (await ListBatches()).Data.Single();
        afterUndo.Undone.Should().BeTrue();
        afterUndo.RemovableCount.Should().Be(0);

        var second = (await (await Undo(batchId)).Content.ReadFromJsonAsync<CardBatchUndoResult>())!;
        second.Removed.Should().Be(0);
        second.Kept.Should().Be(2);
        second.AlreadyUndone.Should().BeTrue();
        (await Cards()).Should().HaveCount(2);
    }

    [Fact]
    public async Task Undo_OtherUsersBatch_IsNotFound()
    {
        await ImportFrequency(1, 2, TestUsers.UserB);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var batchId = (await userDb.CardBatches.SingleAsync(b => b.UserId == TestUsers.UserB)).BatchId;

        (await Undo(batchId)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await userDb.FsrsCards.CountAsync(c => c.UserId == TestUsers.UserB)).Should().Be(2);
    }

    [Fact]
    public async Task JpdbReviewImport_UndoArchivesTheImportedHistory_AndReviewsKeepTheSource()
    {
        var past = DateTime.UtcNow.AddDays(-30);
        object JpdbCard(int wordId, string spelling) => new
        {
            wordId,
            spelling,
            reviews = Enumerable.Range(0, 3).Select(i => new { timestamp = new DateTimeOffset(past.AddDays(i * 5)).ToUnixTimeSeconds(), grade = "okay" })
        };

        var import = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import-jpdb-reviews")
                                             .WithUser(TestUsers.UserA)
                                             .WithJsonContent(new { cards = new[] { JpdbCard(FirstWordId, "本"), JpdbCard(FirstWordId + 1, "水") } }));
        import.StatusCode.Should().Be(HttpStatusCode.OK);

        var cards = await Cards();
        cards.Should().HaveCount(2).And.OnlyContain(c => c.Source == CardSource.JpdbImport && c.SourceBatchId != null);
        var batch = (await Batches()).Single();
        batch.Source.Should().Be(CardSource.JpdbImport);
        batch.CardState.Should().BeNull();

        var review = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/review")
                                             .WithUser(TestUsers.UserA)
                                             .WithJsonContent(new
                                             {
                                                 wordId = FirstWordId + 1, readingIndex = 0, rating = (int)FsrsRating.Good,
                                                 clientRequestId = Guid.NewGuid().ToString("N")
                                             }));
        review.EnsureSuccessStatusCode();

        var reviewed = (await Cards()).Single(c => c.WordId == FirstWordId + 1);
        reviewed.Source.Should().Be(CardSource.JpdbImport);
        reviewed.SourceBatchId.Should().Be(batch.BatchId);

        var result = (await (await Undo(batch.BatchId)).Content.ReadFromJsonAsync<CardBatchUndoResult>())!;
        result.Removed.Should().Be(1);
        result.Kept.Should().Be(1);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var archived = await userDb.FsrsCardArchives.SingleAsync(a => a.UserId == TestUsers.UserA);
        archived.WordId.Should().Be(FirstWordId);
        archived.Reason.Should().Be(CardArchiveReason.ImportUndo);
        archived.ReviewCount.Should().Be(3);
        archived.Source.Should().Be(CardSource.JpdbImport);

        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        await CardRestoreService.RestoreAsync(userDb, jitenDb, TestUsers.UserA, [(FirstWordId, (byte)0)]);
        await userDb.SaveChangesAsync();
        var restored = await userDb.FsrsCards.AsNoTracking().SingleAsync(c => c.UserId == TestUsers.UserA && c.WordId == FirstWordId);
        restored.Source.Should().Be(CardSource.JpdbImport);
        restored.SourceBatchId.Should().BeNull();
    }

    [Fact]
    public async Task ImportFromIds_RecordsTheDeclaredSource_AndRejectsOthers()
    {
        var rejected = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import-from-ids")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new { wordIds = new[] { FirstWordId }, source = (int)CardSource.PlacementTest }));
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var accepted = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import-from-ids")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new
                                               {
                                                   wordIds = new[] { FirstWordId },
                                                   blacklistedWordIds = new[] { FirstWordId + 1 },
                                                   source = (int)CardSource.JpdbImport
                                               }));
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);

        (await Cards()).Should().HaveCount(2).And.OnlyContain(c => c.Source == CardSource.JpdbImport && c.SourceBatchId != null);
        var batch = (await Batches()).Single();
        batch.CardCount.Should().Be(2);
        batch.CardState.Should().BeNull();
        batch.Details.Kind.Should().Be(CardBatchKind.JpdbKnownWords);
    }

    [Fact]
    public async Task BatchList_PagesNewestFirst()
    {
        await ImportFrequency(1, 1);
        await ImportFrequency(2, 2);
        await ImportFrequency(3, 4);

        var first = await ListBatches(0, 2);
        first.TotalItems.Should().Be(3);
        first.Data.Should().HaveCount(2);
        first.Data[0].Details.Should().Be(new CardBatchDetails { Kind = CardBatchKind.FrequencyRange, MinRank = 3, MaxRank = 4 });
        first.Data[0].RemovableCount.Should().Be(2);
        first.Data[1].Details.MinRank.Should().Be(2);

        var second = await ListBatches(2, 2);
        second.Data.Should().ContainSingle().Which.Details.MinRank.Should().Be(1);
    }

    [Fact]
    public async Task ClearKnownWords_DropsTheBatches()
    {
        await ImportFrequency(1, WordCount);

        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/user/vocabulary/known-ids/clear").WithUser(TestUsers.UserA)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await Batches()).Should().BeEmpty();
        (await ListBatches()).TotalItems.Should().Be(0);
    }

    [Fact]
    public async Task Backup_RoundTripsSourcesBatchesAndBatchIds()
    {
        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/user/vocabulary/add/{FirstWordId + 4}/0").WithUser(TestUsers.UserA)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await ImportFrequency(1, 3);

        var before = await Cards();
        var batchBefore = (await Batches()).Single();

        var exportResponse = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/export").WithUser(TestUsers.UserA));
        var export = (await exportResponse.Content.ReadFromJsonAsync<FsrsExportDto>())!;
        export.Batches.Should().ContainSingle().Which.BatchId.Should().Be(batchBefore.BatchId);

        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/user/vocabulary/known-ids/clear").WithUser(TestUsers.UserA)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await Cards()).Should().BeEmpty();

        var import = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import")
                                             .WithUser(TestUsers.UserA)
                                             .WithJsonContent(export));
        import.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await Cards();
        after.Select(c => (c.WordId, c.Source, c.SourceBatchId))
             .Should().BeEquivalentTo(before.Select(c => (c.WordId, c.Source, c.SourceBatchId)));

        var batchAfter = (await Batches()).Single();
        batchAfter.Should().BeEquivalentTo(batchBefore, o => o.Excluding(b => b.CreatedAt));
        batchAfter.CreatedAt.Should().BeCloseTo(batchBefore.CreatedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Backup_WithoutProvenance_LandsUnderOneRestoreBatch()
    {
        await ImportFrequency(1, 3);

        var exportResponse = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/export").WithUser(TestUsers.UserA));
        var export = (await exportResponse.Content.ReadFromJsonAsync<FsrsExportDto>())!;
        export.Batches = null;
        foreach (var card in export.Cards)
        {
            card.Source = null;
            card.SourceBatchId = null;
        }

        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/user/vocabulary/known-ids/clear").WithUser(TestUsers.UserA)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import")
                                 .WithUser(TestUsers.UserA)
                                 .WithJsonContent(export))).StatusCode.Should().Be(HttpStatusCode.OK);

        var batch = (await Batches()).Should().ContainSingle().Subject;
        batch.Source.Should().Be(CardSource.BackupRestore);
        batch.CardCount.Should().Be(3);
        (await Cards()).Should().HaveCount(3).And.OnlyContain(c => c.Source == CardSource.BackupRestore && c.SourceBatchId == batch.BatchId);
    }

    [Fact]
    public async Task Backup_BatchIdOwnedByAnotherAccount_IsRestoredUnderAFreshId()
    {
        await ImportFrequency(1, 2, TestUsers.UserB);

        Guid foreignId;
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            foreignId = (await userDb.CardBatches.SingleAsync(b => b.UserId == TestUsers.UserB)).BatchId;
        }

        var export = new FsrsExportDto
                     {
                         ExportDate = DateTime.UtcNow, UserId = TestUsers.UserB, TotalCards = 1, TotalReviews = 0,
                         Cards =
                         [
                             new FsrsCardExportDto
                             {
                                 WordId = FirstWordId, ReadingIndex = 0, State = FsrsState.Mastered,
                                 Due = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Source = CardSource.FrequencyImport, SourceBatchId = foreignId
                             }
                         ],
                         Batches = [new CardBatchExportDto { BatchId = foreignId, CardCount = 1, Details = new CardBatchDetails { Kind = CardBatchKind.FrequencyRange } }]
                     };

        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import")
                                 .WithUser(TestUsers.UserA)
                                 .WithJsonContent(export))).StatusCode.Should().Be(HttpStatusCode.OK);

        var batch = (await Batches()).Should().ContainSingle().Subject;
        batch.BatchId.Should().NotBe(foreignId);
        (await Cards()).Should().ContainSingle().Which.SourceBatchId.Should().Be(batch.BatchId);
    }
}
