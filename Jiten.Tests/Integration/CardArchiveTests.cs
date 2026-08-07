using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class CardArchiveTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private static readonly DateTime Base = new(2024, 5, 1, 10, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.FsrsCardArchives.ExecuteDeleteAsync();
        await userDb.UserReviewDailies.ExecuteDeleteAsync();
        await userDb.UserFsrsSettings.ExecuteDeleteAsync();
        await userDb.UserMetadatas.ExecuteDeleteAsync();

        await SeedWords();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// 飲む (kanji, index 0) with its kana degradation のむ (index 1), so the redundancy graph has an edge, plus
    /// two plain words for the paths that do not care about siblings.
    /// </summary>
    private async Task SeedWords()
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        if (!await jitenDb.WordForms.AnyAsync(wf => wf.WordId == 900))
        {
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = 900, PartsOfSpeech = ["verb"] });
            jitenDb.WordForms.Add(new JmDictWordForm { WordId = 900, ReadingIndex = 0, Text = "飲む", RubyText = "飲[の]む", FormType = JmDictFormType.KanjiForm });
            jitenDb.WordForms.Add(new JmDictWordForm { WordId = 900, ReadingIndex = 1, Text = "のむ", RubyText = "のむ", FormType = JmDictFormType.KanaForm });

            jitenDb.JMDictWords.Add(new JmDictWord { WordId = 901, PartsOfSpeech = ["noun"] });
            jitenDb.WordForms.Add(new JmDictWordForm { WordId = 901, ReadingIndex = 0, Text = "本", RubyText = "本[ほん]", FormType = JmDictFormType.KanjiForm });

            jitenDb.JMDictWords.Add(new JmDictWord { WordId = 902, PartsOfSpeech = ["noun"] });
            jitenDb.WordForms.Add(new JmDictWordForm { WordId = 902, ReadingIndex = 0, Text = "犬", RubyText = "犬[いぬ]", FormType = JmDictFormType.KanjiForm });

            await jitenDb.SaveChangesAsync();
        }

        scope.ServiceProvider.GetRequiredService<IWordFormSiblingCache>().Reload();
    }

    private async Task<FsrsCard> SeedCard(int wordId, byte readingIndex, FsrsState state, int reviewCount,
                                          string? userId = null)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var card = new FsrsCard(userId ?? TestUsers.UserA, wordId, readingIndex, state: state,
                                stability: 12, due: Base.AddDays(20), lastReview: Base.AddDays(reviewCount));
        card.Difficulty = 5.5;
        userDb.FsrsCards.Add(card);
        await userDb.SaveChangesAsync();

        for (var i = 0; i < reviewCount; i++)
            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(card.CardId, i % 3 == 0 ? FsrsRating.Again : FsrsRating.Good,
                                                        Base.AddDays(i), 3000));
        await userDb.SaveChangesAsync();

        return card;
    }

    private async Task<List<FsrsCardArchive>> Archives(string? userId = null)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.FsrsCardArchives.AsNoTracking()
                           .Where(a => a.UserId == (userId ?? TestUsers.UserA))
                           .OrderBy(a => a.WordId).ThenBy(a => a.ReadingIndex)
                           .ToListAsync();
    }

    private Task<HttpResponseMessage> SetState(int wordId, byte readingIndex, string state)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/set-vocabulary-state")
                             .WithUser(TestUsers.UserA)
                             .WithJsonContent(new { wordId, readingIndex, state }));

    [Fact]
    public async Task ForgetAdd_ArchivesTheCardAndItsHistory()
    {
        await SeedCard(901, 0, FsrsState.Review, 5);

        (await SetState(901, 0, "forget-add")).StatusCode.Should().Be(HttpStatusCode.OK);

        var archives = await Archives();
        archives.Should().ContainSingle();
        archives[0].Reason.Should().Be(CardArchiveReason.Forget);
        archives[0].ReviewCount.Should().Be(5);
        archives[0].State.Should().Be(FsrsState.Review);
        archives[0].Stability.Should().Be(12);
        archives[0].FirstReview.Should().Be(Base);
        ReviewLogPacker.Unpack(archives[0].Logs!, archives[0].FirstReview!.Value).Should().HaveCount(5);
    }

    [Fact]
    public async Task BulkForget_ArchivesEveryCardWithTheBulkReason()
    {
        await SeedCard(901, 0, FsrsState.Review, 3);
        await SeedCard(902, 0, FsrsState.Review, 2);

        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/srs/set-vocabulary-state-bulk")
                .WithUser(TestUsers.UserA)
                .WithJsonContent(new
                                 {
                                     state = "forget-add",
                                     items = new[]
                                             {
                                                 new { wordId = 901, readingIndex = 0 },
                                                 new { wordId = 902, readingIndex = 0 }
                                             }
                                 }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var archives = await Archives();
        archives.Should().HaveCount(2);
        archives.Should().OnlyContain(a => a.Reason == CardArchiveReason.BulkForget);
        archives.Select(a => a.ReviewCount).Should().BeEquivalentTo(new[] { 3, 2 });
    }

    [Fact]
    public async Task MassActionDelete_ArchivesBeforeDeleting()
    {
        await SeedCard(901, 0, FsrsState.Review, 4);
        await SeedCard(902, 0, FsrsState.Review, 1);

        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/srs/mass-action/execute")
                .WithUser(TestUsers.UserA)
                .WithJsonContent(new { action = "delete-cards", stateFilter = new[] { (int)FsrsState.Review } }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var archives = await Archives();
        archives.Should().HaveCount(2);
        archives.Should().OnlyContain(a => a.Reason == CardArchiveReason.MassAction);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await userDb.FsrsCards.CountAsync(c => c.UserId == TestUsers.UserA)).Should().Be(0);
    }

    [Fact]
    public async Task MarkingTheKanjiFormKnown_ArchivesTheKanaSiblingAsRedundant()
    {
        await SeedCard(900, 1, FsrsState.Review, 6);

        (await SetState(900, 0, "neverForget-add")).StatusCode.Should().Be(HttpStatusCode.OK);

        var archives = await Archives();
        archives.Should().ContainSingle();
        archives[0].ReadingIndex.Should().Be(1);
        archives[0].Reason.Should().Be(CardArchiveReason.KanaRedundancy);
        archives[0].CoveringReadingIndex.Should().Be(0);
        archives[0].ReviewCount.Should().Be(6);
    }

    [Fact]
    public async Task MarkingTheKanjiFormKnown_LeavesABlacklistedKanaSiblingAlone()
    {
        await SeedCard(900, 1, FsrsState.Blacklisted, 2);

        (await SetState(900, 0, "neverForget-add")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Archives()).Should().BeEmpty();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await userDb.FsrsCards.AnyAsync(c => c.UserId == TestUsers.UserA && c.WordId == 900 && c.ReadingIndex == 1))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ReAddingARedundancyRemovedForm_RestoresItsHistoryAutomatically()
    {
        await SeedCard(900, 1, FsrsState.Review, 6);
        await SetState(900, 0, "neverForget-add");
        (await Archives()).Should().ContainSingle();

        (await SetState(900, 1, "blacklist-add")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Archives()).Should().BeEmpty();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var card = await userDb.FsrsCards.Include(c => c.ReviewLogs)
                               .FirstAsync(c => c.UserId == TestUsers.UserA && c.WordId == 900 && c.ReadingIndex == 1);

        card.ReviewLogs.Should().HaveCount(6);
        card.State.Should().Be(FsrsState.Blacklisted);
        card.Stability.Should().Be(12);
    }

    [Fact]
    public async Task ReAddingAForgottenForm_DoesNotRestoreItsHistory()
    {
        await SeedCard(901, 0, FsrsState.Review, 4);
        await SetState(901, 0, "forget-add");

        (await SetState(901, 0, "blacklist-add")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Archives()).Should().ContainSingle();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var card = await userDb.FsrsCards.Include(c => c.ReviewLogs)
                               .FirstAsync(c => c.UserId == TestUsers.UserA && c.WordId == 901);
        card.ReviewLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task ReArchivingAForm_MergesIntoTheSameRow()
    {
        await SeedCard(901, 0, FsrsState.Review, 4);
        await SetState(901, 0, "forget-add");

        // Re-create the card with later history, then forget it again.
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var card = new FsrsCard(TestUsers.UserA, 901, 0, state: FsrsState.Review, due: Base.AddDays(40));
            userDb.FsrsCards.Add(card);
            await userDb.SaveChangesAsync();
            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(card.CardId, FsrsRating.Good, Base.AddDays(30), 1000));
            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(card.CardId, FsrsRating.Good, Base.AddDays(31), 1000));
            await userDb.SaveChangesAsync();
        }

        await SetState(901, 0, "forget-add");

        var archives = await Archives();
        archives.Should().ContainSingle();
        archives[0].ReviewCount.Should().Be(6);
        archives[0].FirstReview.Should().Be(Base);

        var unpacked = ReviewLogPacker.Unpack(archives[0].Logs!, archives[0].FirstReview!.Value);
        unpacked.Should().HaveCount(6);
        unpacked[^1].ReviewDateTime.Should().Be(Base.AddDays(31));
    }

    /// <summary>
    /// forget → re-add → review → forget again → restore. The unique key allows only one archive row, so what
    /// matters is that the second removal unions rather than replaces, and that the restore returns the union.
    /// </summary>
    [Fact]
    public async Task ForgetReviewForgetRestore_ReturnsOneCardWithBothLifetimes()
    {
        await SeedCard(901, 0, FsrsState.Review, 4);
        await SetState(901, 0, "forget-add");

        long secondCardId;
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var card = new FsrsCard(TestUsers.UserA, 901, 0, state: FsrsState.Learning, due: Base.AddDays(41))
                       { CreatedAt = Base.AddDays(29), Stability = 3, Difficulty = 7, Lapses = 1 };
            userDb.FsrsCards.Add(card);
            await userDb.SaveChangesAsync();
            secondCardId = card.CardId;

            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(card.CardId, FsrsRating.Again, Base.AddDays(30), 1000));
            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(card.CardId, FsrsRating.Good, Base.AddDays(31), 1000));
            await userDb.SaveChangesAsync();
        }

        await SetState(901, 0, "forget-add");

        var archives = await Archives();
        archives.Should().ContainSingle("the unique key permits only one row per form");
        archives[0].ReviewCount.Should().Be(6);
        archives[0].Stability.Should().Be(3, "the newer removal's schedule wins");
        archives[0].CardCreatedAt.Should().Be(Base.AddDays(29));

        (await Restore((901, 0))).StatusCode.Should().Be(HttpStatusCode.OK);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<UserDbContext>();
        var restored = await verifyDb.FsrsCards.Include(c => c.ReviewLogs)
                                     .FirstAsync(c => c.UserId == TestUsers.UserA && c.WordId == 901);

        restored.CardId.Should().NotBe(secondCardId, "a restore inserts a new card rather than resurrecting the old row");
        restored.ReviewLogs.Should().HaveCount(6, "both lifetimes come back");
        restored.ReviewLogs.Select(l => l.ReviewDateTime).Should().OnlyHaveUniqueItems();
        restored.ReviewLogs.Min(l => l.ReviewDateTime).Should().Be(Base);
        restored.ReviewLogs.Max(l => l.ReviewDateTime).Should().Be(Base.AddDays(31));

        // The stored schedule only ever saw the second lifetime, so a merged row is replayed on restore
        // rather than left describing two of its own six reviews.
        restored.Stability.Should().NotBe(3);
        restored.LastReview.Should().Be(Base.AddDays(31));
    }

    /// <summary>A row that never merged keeps the exact schedule it was archived with; no replay, no drift.</summary>
    [Fact]
    public async Task Restore_OfASingleLifetime_KeepsTheArchivedScheduleExactly()
    {
        await SeedCard(901, 0, FsrsState.Review, 3);
        await SetState(901, 0, "forget-add");

        (await Restore((901, 0))).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var restored = await userDb.FsrsCards.FirstAsync(c => c.UserId == TestUsers.UserA && c.WordId == 901);

        restored.Stability.Should().Be(12);
        restored.Difficulty.Should().Be(5.5);
        restored.Due.Should().Be(Base.AddDays(20));
    }

    [Fact]
    public async Task Restore_RecreatesTheCardAndItsHistory()
    {
        await SeedCard(901, 0, FsrsState.Review, 5);
        await SetState(901, 0, "forget-add");

        var response = await Restore((901, 0));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await Archives()).Should().BeEmpty();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var card = await userDb.FsrsCards.Include(c => c.ReviewLogs)
                               .FirstAsync(c => c.UserId == TestUsers.UserA && c.WordId == 901);

        card.ReviewLogs.Should().HaveCount(5);
        card.State.Should().Be(FsrsState.Review);
        card.Stability.Should().Be(12);
        card.Due.Should().Be(Base.AddDays(20));
    }

    /// <summary>
    /// The word menu paints the restored row from this, so a mature card must not come back reading as young.
    /// </summary>
    [Fact]
    public async Task Restore_ReportsTheTierTheCardCameBackAs()
    {
        var card = await SeedCard(901, 0, FsrsState.Review, 3);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var tracked = await userDb.FsrsCards.FirstAsync(c => c.CardId == card.CardId);
            tracked.LastReview = Base;
            tracked.Due = Base.AddDays(40);
            await userDb.SaveChangesAsync();
        }

        await SetState(901, 0, "forget-add");

        var response = await Restore((901, 0));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("results")[0].GetProperty("knownStates")
               .EnumerateArray().Select(s => s.GetInt32())
               .Should().Contain((int)KnownState.Mature);
    }

    [Fact]
    public async Task Restore_OntoALiveCard_MergesWithoutDuplicatingReviews()
    {
        await SeedCard(901, 0, FsrsState.Review, 5);
        await SetState(901, 0, "forget-add");

        // Re-created by hand with one review that overlaps the archived set and one that does not.
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var card = new FsrsCard(TestUsers.UserA, 901, 0, state: FsrsState.Review, due: Base.AddDays(50));
            userDb.FsrsCards.Add(card);
            await userDb.SaveChangesAsync();
            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(card.CardId, FsrsRating.Good, Base.AddDays(1), 1000));
            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(card.CardId, FsrsRating.Good, Base.AddDays(60), 1000));
            await userDb.SaveChangesAsync();
        }

        (await Restore((901, 0))).StatusCode.Should().Be(HttpStatusCode.OK);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<UserDbContext>();
        var merged = await verifyDb.FsrsCards.Include(c => c.ReviewLogs)
                                   .FirstAsync(c => c.UserId == TestUsers.UserA && c.WordId == 901);

        // 5 archived + 2 live, with day 1 present in both.
        merged.ReviewLogs.Should().HaveCount(6);
        merged.ReviewLogs.Select(l => l.ReviewDateTime).Should().OnlyHaveUniqueItems();
        merged.LastReview.Should().Be(Base.AddDays(60));
    }

    [Fact]
    public async Task Restore_OfAFormJmdictNoLongerHas_FailsPerRow()
    {
        await SeedCard(901, 0, FsrsState.Review, 2);
        await SetState(901, 0, "forget-add");

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var row = await userDb.FsrsCardArchives.FirstAsync(a => a.UserId == TestUsers.UserA);
            row.ReadingIndex = 7;
            await userDb.SaveChangesAsync();
        }

        var response = await Restore((901, 7));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("restored").GetInt32().Should().Be(0);
        payload.GetProperty("results")[0].GetProperty("error").GetString().Should().Contain("no longer exists");

        (await Archives()).Should().ContainSingle();
    }

    [Fact]
    public async Task Restore_OfACorruptHistory_ReportsItRatherThanRestoringNothingQuietly()
    {
        await SeedCard(901, 0, FsrsState.Review, 3);
        await SetState(901, 0, "forget-add");

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var row = await userDb.FsrsCardArchives.FirstAsync(a => a.UserId == TestUsers.UserA);
            var corrupted = row.Logs!.ToArray();
            corrupted[0] = 42;
            row.Logs = corrupted;
            await userDb.SaveChangesAsync();
        }

        var payload = await (await Restore((901, 0))).Content.ReadFromJsonAsync<JsonElement>();

        payload.GetProperty("restored").GetInt32().Should().Be(0);
        payload.GetProperty("results")[0].GetProperty("error").GetString().Should().Contain("could not be read");
        (await Archives()).Should().ContainSingle();
    }

    [Fact]
    public async Task ArchiveListing_ReportsTheReasonAndWhetherItAutoRestores()
    {
        await SeedCard(900, 1, FsrsState.Review, 3);
        await SetState(900, 0, "neverForget-add");

        var payload = await (await _client.SendAsync(
                                 new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/archive")
                                     .WithUser(TestUsers.UserA)))
                            .Content.ReadFromJsonAsync<JsonElement>();

        payload.GetProperty("totalItems").GetInt32().Should().Be(1);
        var item = payload.GetProperty("data")[0];
        item.GetProperty("wordId").GetInt32().Should().Be(900);
        item.GetProperty("reason").GetInt32().Should().Be((int)CardArchiveReason.KanaRedundancy);
        item.GetProperty("autoRestores").GetBoolean().Should().BeTrue();
        item.GetProperty("reviewCount").GetInt32().Should().Be(3);
        item.GetProperty("coveringReading").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ForgettingAnArchivedEntry_DropsItForGood()
    {
        await SeedCard(901, 0, FsrsState.Review, 2);
        await SetState(901, 0, "forget-add");

        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, "/api/user/vocabulary/archive")
                .WithUser(TestUsers.UserA)
                .WithJsonContent(new { forms = new[] { new { wordId = 901, readingIndex = 0 } } }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Archives()).Should().BeEmpty();
    }

    [Fact]
    public async Task ArchivedRowsBelongToTheirOwner()
    {
        await SeedCard(901, 0, FsrsState.Review, 2, TestUsers.UserB);
        await SeedCard(902, 0, FsrsState.Review, 2);

        await SetState(902, 0, "forget-add");

        (await Archives()).Should().ContainSingle();
        (await Archives(TestUsers.UserB)).Should().BeEmpty();
    }

    [Fact]
    public async Task ArchivingACardWithNoReviews_KeepsTheScheduleWithoutAHistory()
    {
        await SeedCard(901, 0, FsrsState.Mastered, 0);

        await SetState(901, 0, "forget-add");

        var archives = await Archives();
        archives.Should().ContainSingle();
        archives[0].ReviewCount.Should().Be(0);
        archives[0].Logs.Should().BeNull();
        archives[0].FirstReview.Should().BeNull();
        archives[0].State.Should().Be(FsrsState.Mastered);
    }

    [Fact]
    public async Task ForgettingAFormWithNoCard_ArchivesNothing()
    {
        (await SetState(901, 0, "forget-add")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Archives()).Should().BeEmpty();
    }

    [Fact]
    public async Task BackupRoundTrip_KeepsArchivedHistory()
    {
        await SeedCard(901, 0, FsrsState.Review, 4);
        await SeedCard(902, 0, FsrsState.Review, 1);
        await SetState(901, 0, "forget-add");

        var export = await (await _client.SendAsync(
                                new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/export")
                                    .WithUser(TestUsers.UserA)))
                           .Content.ReadFromJsonAsync<FsrsExportDto>();

        export!.Archive.Should().ContainSingle();
        export.Archive![0].ReviewLogs.Should().HaveCount(4);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            await userDb.FsrsCardArchives.ExecuteDeleteAsync();
        }

        var import = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import")
                .WithUser(TestUsers.UserA)
                .WithJsonContent(export));

        import.StatusCode.Should().Be(HttpStatusCode.OK);

        var archives = await Archives();
        archives.Should().ContainSingle();
        archives[0].ReviewCount.Should().Be(4);
    }

    [Fact]
    public async Task BackupWithoutAnArchiveSection_StillImports()
    {
        await SeedCard(901, 0, FsrsState.Review, 2);

        var export = await (await _client.SendAsync(
                                new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/export")
                                    .WithUser(TestUsers.UserA)))
                           .Content.ReadFromJsonAsync<FsrsExportDto>();

        export!.Archive = null;

        var import = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import")
                .WithUser(TestUsers.UserA)
                .WithJsonContent(export));

        import.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BackupWithWordText_CarriesTheSurfaceAndReadingOfEveryForm()
    {
        await SeedCard(900, 1, FsrsState.Review, 1);
        await SeedCard(902, 0, FsrsState.Review, 1);
        await SeedCard(901, 0, FsrsState.Review, 2);
        await SetState(901, 0, "forget-add");

        var export = await (await _client.SendAsync(
                                new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/export?includeWordText=true")
                                    .WithUser(TestUsers.UserA)))
                           .Content.ReadFromJsonAsync<FsrsExportDto>();

        var kana = export!.Cards.Single(c => c.WordId == 900);
        kana.Text.Should().Be("のむ");
        kana.Reading.Should().Be("のむ");

        var kanji = export.Cards.Single(c => c.WordId == 902);
        kanji.Text.Should().Be("犬");
        kanji.Reading.Should().Be("いぬ");

        export.Archive.Should().ContainSingle();
        export.Archive![0].Text.Should().Be("本");
        export.Archive[0].Reading.Should().Be("ほん");
    }

    [Fact]
    public async Task BackupWithoutWordText_LeavesTheSurfaceOut()
    {
        await SeedCard(902, 0, FsrsState.Review, 1);

        var export = await (await _client.SendAsync(
                                new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/export")
                                    .WithUser(TestUsers.UserA)))
                           .Content.ReadFromJsonAsync<FsrsExportDto>();

        var card = export!.Cards.Should().ContainSingle().Subject;
        card.Text.Should().BeNull();
        card.Reading.Should().BeNull();
    }

    [Fact]
    public async Task BackupWithWordText_StillImports()
    {
        await SeedCard(902, 0, FsrsState.Review, 1);

        var export = await (await _client.SendAsync(
                                new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/export?includeWordText=true")
                                    .WithUser(TestUsers.UserA)))
                           .Content.ReadFromJsonAsync<FsrsExportDto>();

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
            await userDb.FsrsCards.ExecuteDeleteAsync();
        }

        var import = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import")
                .WithUser(TestUsers.UserA)
                .WithJsonContent(export!));

        import.StatusCode.Should().Be(HttpStatusCode.OK);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await verifyDb.FsrsCards.CountAsync(c => c.UserId == TestUsers.UserA && c.WordId == 902)).Should().Be(1);
    }

    [Fact]
    public async Task SingleFormLookup_TellsTheWordMenuWhetherThereIsAnythingToRestore()
    {
        await SeedCard(901, 0, FsrsState.Review, 4);

        var before = await (await _client.SendAsync(
                                new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/archive/901/0").WithUser(TestUsers.UserA)))
                           .Content.ReadFromJsonAsync<JsonElement>();
        before.GetProperty("found").GetBoolean().Should().BeFalse();

        await SetState(901, 0, "forget-add");

        var after = await (await _client.SendAsync(
                               new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/archive/901/0").WithUser(TestUsers.UserA)))
                          .Content.ReadFromJsonAsync<JsonElement>();

        after.GetProperty("found").GetBoolean().Should().BeTrue();
        after.GetProperty("reviewCount").GetInt32().Should().Be(4);
        after.GetProperty("state").GetInt32().Should().Be((int)FsrsState.Review);
        after.GetProperty("autoRestores").GetBoolean().Should().BeFalse();
        after.GetProperty("historyTruncated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task SingleFormLookup_IsScopedToTheCaller()
    {
        await SeedCard(901, 0, FsrsState.Review, 2, TestUsers.UserB);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var card = await userDb.FsrsCards.FirstAsync(c => c.UserId == TestUsers.UserB);
            await CardArchiveService.ArchiveCardsAsync(userDb, TestUsers.UserB, [card], CardArchiveReason.Forget);
            userDb.FsrsCards.Remove(card);
            await userDb.SaveChangesAsync();
        }

        var payload = await (await _client.SendAsync(
                                 new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/archive/901/0").WithUser(TestUsers.UserA)))
                            .Content.ReadFromJsonAsync<JsonElement>();

        payload.GetProperty("found").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task RestoringEverythingMatching_NeedsNoExplicitFormList()
    {
        await SeedCard(901, 0, FsrsState.Review, 2);
        await SeedCard(902, 0, FsrsState.Review, 3);
        await SetState(901, 0, "forget-add");
        await SetState(902, 0, "forget-add");
        (await Archives()).Should().HaveCount(2);

        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/archive/restore")
                .WithUser(TestUsers.UserA)
                .WithJsonContent(new { all = true }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("restored").GetInt32().Should().Be(2);
        payload.GetProperty("remaining").GetInt32().Should().Be(0);

        (await Archives()).Should().BeEmpty();
    }

    [Fact]
    public async Task RestoringEverythingMatching_HonoursTheReasonFilter()
    {
        await SeedCard(900, 1, FsrsState.Review, 2);
        await SetState(900, 0, "neverForget-add");
        await SeedCard(901, 0, FsrsState.Review, 2);
        await SetState(901, 0, "forget-add");
        (await Archives()).Should().HaveCount(2);

        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/archive/restore")
                .WithUser(TestUsers.UserA)
                .WithJsonContent(new { all = true, reason = (int)CardArchiveReason.Forget }));

        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("restored").GetInt32().Should().Be(1);

        var left = await Archives();
        left.Should().ContainSingle();
        left[0].Reason.Should().Be(CardArchiveReason.KanaRedundancy);
    }

    [Fact]
    public async Task ForgettingEverythingOfAReason_LeavesTheOthersAlone()
    {
        await SeedCard(900, 1, FsrsState.Review, 2);
        await SetState(900, 0, "neverForget-add");
        await SeedCard(901, 0, FsrsState.Review, 2);
        await SetState(901, 0, "forget-add");

        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, "/api/user/vocabulary/archive")
                .WithUser(TestUsers.UserA)
                .WithJsonContent(new { forms = Array.Empty<object>(), reason = (int)CardArchiveReason.KanaRedundancy }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removed").GetInt32().Should().Be(1);

        var left = await Archives();
        left.Should().ContainSingle();
        left[0].Reason.Should().Be(CardArchiveReason.Forget);
    }

    [Fact]
    public async Task DeletingTheAccount_TakesTheArchiveAndRollupWithIt()
    {
        var throwawayId = Guid.NewGuid().ToString();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        userDb.Users.Add(new Jiten.Core.Data.Authentication.User
                         {
                             Id = throwawayId, UserName = throwawayId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                         });
        userDb.FsrsCardArchives.Add(new FsrsCardArchive
                                    {
                                        UserId = throwawayId, WordId = 901, ReadingIndex = 0,
                                        ArchivedAt = Base, Reason = CardArchiveReason.Forget,
                                        State = FsrsState.Review, Due = Base, CardCreatedAt = Base
                                    });
        userDb.UserReviewDailies.Add(new UserReviewDaily
                                     {
                                         UserId = throwawayId, LocalDate = DateOnly.FromDateTime(Base), ReviewCount = 2
                                     });
        await userDb.SaveChangesAsync();

        userDb.Users.Remove(await userDb.Users.FirstAsync(u => u.Id == throwawayId));
        await userDb.SaveChangesAsync();

        (await userDb.FsrsCardArchives.CountAsync(a => a.UserId == throwawayId)).Should().Be(0);
        (await userDb.UserReviewDailies.CountAsync(d => d.UserId == throwawayId)).Should().Be(0);
    }

    [Fact]
    public async Task JpdbImport_ArchivesTheHistoryOfAFormItSkipsAsRedundant()
    {
        var response = await ImportJpdb(("飲む", 2), ("のむ", 3));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("archivedRedundant").GetInt32().Should().Be(1);
        payload.GetProperty("skipped").GetInt32().Should().Be(1);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var live = await userDb.FsrsCards.Where(c => c.UserId == TestUsers.UserA).ToListAsync();
        live.Should().ContainSingle().Which.ReadingIndex.Should().Be(0);

        var archives = await Archives();
        archives.Should().ContainSingle();
        archives[0].ReadingIndex.Should().Be(1);
        archives[0].Reason.Should().Be(CardArchiveReason.KanaRedundancy);
        archives[0].CoveringReadingIndex.Should().Be(0);
        archives[0].ReviewCount.Should().Be(3);
        archives[0].LastReview.Should().NotBeNull("the archived schedule is replayed from the imported reviews");
        ReviewLogPacker.Unpack(archives[0].Logs!, archives[0].FirstReview!.Value).Should().HaveCount(3);
    }

    [Fact]
    public async Task JpdbImport_ReimportingTheSameFileLeavesTheArchivedHistoryUnchanged()
    {
        await ImportJpdb(("飲む", 2), ("のむ", 3));
        var first = (await Archives()).Single();

        (await ImportJpdb(("飲む", 2), ("のむ", 3))).StatusCode.Should().Be(HttpStatusCode.OK);

        var archives = await Archives();
        archives.Should().ContainSingle();
        archives[0].ReviewCount.Should().Be(3, "the same timestamps are the same reviews");
        archives[0].Logs.Should().BeEquivalentTo(first.Logs);
        archives[0].FirstReview.Should().Be(first.FirstReview);
        archives[0].HistoryMerged.Should().BeFalse("the stored schedule still describes everything the row holds");
        ReviewLogPacker.Unpack(archives[0].Logs!, archives[0].FirstReview!.Value).Should().HaveCount(3);
    }

    [Fact]
    public async Task JpdbImport_ASecondFileWithLaterReviewsGrowsTheSameArchiveRow()
    {
        await ImportJpdb(("飲む", 2), ("のむ", 3));

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import-jpdb-reviews")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new
                                                                {
                                                                    cards = new[]
                                                                            {
                                                                                JpdbCard("飲む", 0, 2),
                                                                                JpdbCard("のむ", 0, 3),
                                                                                JpdbCard("のむ", 10, 2)
                                                                            }
                                                                }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var archives = await Archives();
        archives.Should().ContainSingle();
        archives[0].ReviewCount.Should().Be(5, "the two new days join the three already stored");
    }

    /// <summary>
    /// The archived history comes back on the import that re-creates the form, rather than being counted once as a
    /// live review and once as an archived one.
    /// </summary>
    [Fact]
    public async Task JpdbImport_RestoresTheArchivedHistoryWhenTheFormIsNoLongerRedundant()
    {
        await ImportJpdb(("飲む", 2), ("のむ", 3));

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            await userDb.FsrsCards.Where(c => c.UserId == TestUsers.UserA && c.WordId == 900 && c.ReadingIndex == 0)
                        .ExecuteDeleteAsync();
        }

        (await ImportJpdb(("のむ", 3))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Archives()).Should().BeEmpty();

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<UserDbContext>();
        var card = await verifyDb.FsrsCards.Include(c => c.ReviewLogs)
                                 .SingleAsync(c => c.UserId == TestUsers.UserA && c.WordId == 900);
        card.ReadingIndex.Should().Be(1);
        card.ReviewLogs.Should().HaveCount(3, "the restored reviews are the same ones the file carries");
    }

    [Fact]
    public async Task BackupImport_ArchivesTheHistoryOfAFormTheFileItselfMakesRedundant()
    {
        await SeedCard(900, 0, FsrsState.Review, 2);
        await SeedCard(900, 1, FsrsState.Review, 5);

        var export = await (await _client.SendAsync(
                                new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/export")
                                    .WithUser(TestUsers.UserA)))
                           .Content.ReadFromJsonAsync<FsrsExportDto>();

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
            await userDb.FsrsCards.ExecuteDeleteAsync();
            await userDb.FsrsCardArchives.ExecuteDeleteAsync();
        }

        var import = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import")
                .WithUser(TestUsers.UserA)
                .WithJsonContent(export!));
        import.StatusCode.Should().Be(HttpStatusCode.OK);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<UserDbContext>();
        var live = await verifyDb.FsrsCards.Where(c => c.UserId == TestUsers.UserA).ToListAsync();
        live.Should().ContainSingle().Which.ReadingIndex.Should().Be(0);

        var archives = await Archives();
        archives.Should().ContainSingle();
        archives[0].ReadingIndex.Should().Be(1);
        archives[0].Reason.Should().Be(CardArchiveReason.KanaRedundancy);
        archives[0].CoveringReadingIndex.Should().Be(0);
        archives[0].ReviewCount.Should().Be(5);
    }

    [Fact]
    public async Task ArchivingCardsAnImportNeverInserted_SkipsTheOnesWithNoHistory()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var archived = await CardArchiveService.ArchiveUninsertedCardsAsync(
            userDb, TestUsers.UserA,
            [
                (new FsrsCard(TestUsers.UserA, 901, 0, state: FsrsState.Mastered), []),
                (new FsrsCard(TestUsers.UserA, 902, 0, state: FsrsState.Review),
                    [new PackedReview(FsrsRating.Good, Base, 1000)])
            ],
            CardArchiveReason.KanaRedundancy);
        await userDb.SaveChangesAsync();

        archived.Should().Be(1);
        (await Archives()).Should().ContainSingle().Which.WordId.Should().Be(902);
    }

    [Fact]
    public async Task ImportingKnownWords_ArchivesAPrunedFormOnlyWhenItHasHistory()
    {
        await SeedCard(900, 1, FsrsState.Review, 4);

        (await ImportKnownWords(900)).StatusCode.Should().Be(HttpStatusCode.OK);

        var archives = await Archives();
        archives.Should().ContainSingle();
        archives[0].ReadingIndex.Should().Be(1);
        archives[0].Reason.Should().Be(CardArchiveReason.FormPrune);
        archives[0].ReviewCount.Should().Be(4);
    }

    [Fact]
    public async Task ImportingKnownWords_DropsAReviewlessPrunedFormWithoutAnArchiveRow()
    {
        await SeedCard(900, 1, FsrsState.Mastered, 0);

        (await ImportKnownWords(900)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Archives()).Should().BeEmpty("a form the user never reviewed leaves nothing worth restoring");

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var live = await userDb.FsrsCards.Where(c => c.UserId == TestUsers.UserA).ToListAsync();
        live.Should().ContainSingle().Which.ReadingIndex.Should().Be(0);
    }

    /// <summary>
    /// The same policy the removal applies to a live kana card: mastered alongside its kanji sibling in one
    /// request, the kana form is not created at all rather than left behind as a redundant live card.
    /// </summary>
    [Fact]
    public async Task BulkMasteringKanjiAndKanaTogether_CreatesOnlyTheKanjiCard()
    {
        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/srs/set-vocabulary-state-bulk")
                .WithUser(TestUsers.UserA)
                .WithJsonContent(new
                                 {
                                     state = "neverForget-add",
                                     items = new[]
                                             {
                                                 new { wordId = 900, readingIndex = 0 },
                                                 new { wordId = 900, readingIndex = 1 }
                                             }
                                 }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var live = await userDb.FsrsCards.Where(c => c.UserId == TestUsers.UserA).ToListAsync();
        live.Should().ContainSingle().Which.ReadingIndex.Should().Be(0);

        (await Archives()).Should().BeEmpty("a card that was never created has no history to keep");
    }

    [Fact]
    public async Task BulkMasteringKanjiAndKanaTogether_ArchivesALiveKanaCardsHistory()
    {
        await SeedCard(900, 1, FsrsState.Review, 6);

        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/srs/set-vocabulary-state-bulk")
                .WithUser(TestUsers.UserA)
                .WithJsonContent(new
                                 {
                                     state = "neverForget-add",
                                     items = new[]
                                             {
                                                 new { wordId = 900, readingIndex = 0 },
                                                 new { wordId = 900, readingIndex = 1 }
                                             }
                                 }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var live = await userDb.FsrsCards.Where(c => c.UserId == TestUsers.UserA).ToListAsync();
        live.Should().ContainSingle().Which.ReadingIndex.Should().Be(0);

        var archives = await Archives();
        archives.Should().ContainSingle();
        archives[0].ReadingIndex.Should().Be(1);
        archives[0].Reason.Should().Be(CardArchiveReason.KanaRedundancy);
        archives[0].ReviewCount.Should().Be(6);
    }

    /// <summary>
    /// A forgotten form's archive row keeps its reviews — until an import puts those same reviews back on a
    /// live card, at which point keeping the copy would double every whole-history statistic.
    /// </summary>
    [Fact]
    public async Task JpdbImport_ReimportingAForgottenForm_DoesNotCountItsReviewsTwice()
    {
        await ImportJpdb(("飲む", 3));
        await SetState(900, 0, "forget-add");
        (await Archives()).Single().ReviewCount.Should().Be(3);

        (await ImportJpdb(("飲む", 3))).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var card = await userDb.FsrsCards.Include(c => c.ReviewLogs)
                               .SingleAsync(c => c.UserId == TestUsers.UserA && c.WordId == 900);
        card.ReviewLogs.Should().HaveCount(3);

        // The row survives (Forget never auto-restores), but its copy of the now-live reviews is gone.
        var row = (await Archives()).Single();
        row.Reason.Should().Be(CardArchiveReason.Forget);
        row.ReviewCount.Should().Be(0);
        row.Logs.Should().BeNull();

        var settings = await (await _client.SendAsync(
                                  new HttpRequestMessage(HttpMethod.Get, "/api/srs/settings").WithUser(TestUsers.UserA)))
                             .Content.ReadFromJsonAsync<JsonElement>();
        settings.GetProperty("reviewCount").GetInt32().Should().Be(3, "each review counts once, live or archived");
    }

    [Fact]
    public async Task ArchivingTheSameFormTwiceInOneUnitOfWork_MergesIntoOneRow()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        await CardArchiveService.ArchiveCardsAsync(userDb, TestUsers.UserA,
                                                   [new FsrsCard(TestUsers.UserA, 901, 0, state: FsrsState.Review)],
                                                   CardArchiveReason.Forget);
        await CardArchiveService.ArchiveCardsAsync(userDb, TestUsers.UserA,
                                                   [new FsrsCard(TestUsers.UserA, 901, 0, state: FsrsState.Mastered)],
                                                   CardArchiveReason.MassAction);
        await userDb.SaveChangesAsync();

        var archives = await Archives();
        archives.Should().ContainSingle("the second archive call must see the row the first added but never saved");
        archives[0].Reason.Should().Be(CardArchiveReason.MassAction);
        archives[0].State.Should().Be(FsrsState.Mastered);
    }

    /// <summary>Word 900 with one card per spelling, each carrying <c>reviews</c> daily reviews from <see cref="Base"/>.</summary>
    private Task<HttpResponseMessage> ImportJpdb(params (string Spelling, int Reviews)[] cards)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import-jpdb-reviews")
                             .WithUser(TestUsers.UserA)
                             .WithJsonContent(new { cards = cards.Select(c => JpdbCard(c.Spelling, 0, c.Reviews)) }));

    private static object JpdbCard(string spelling, int fromDay, int reviews)
        => new
           {
               wordId = 900,
               spelling,
               reviews = Enumerable.Range(fromDay, reviews)
                                   .Select(i => new
                                                {
                                                    timestamp = new DateTimeOffset(Base.AddDays(i)).ToUnixTimeSeconds(),
                                                    grade = "okay"
                                                })
           };

    private Task<HttpResponseMessage> ImportKnownWords(params int[] wordIds)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import-from-ids")
                             .WithUser(TestUsers.UserA)
                             .WithJsonContent(new { wordIds }));

    private Task<HttpResponseMessage> Restore(params (int WordId, byte ReadingIndex)[] forms)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/archive/restore")
                             .WithUser(TestUsers.UserA)
                             .WithJsonContent(new { forms = forms.Select(f => new { wordId = f.WordId, readingIndex = f.ReadingIndex }) }));
}
