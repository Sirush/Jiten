using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class ReviewRollupTests(JitenWebApplicationFactory factory)
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

        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        if (!await jitenDb.WordForms.AnyAsync(wf => wf.WordId == 910))
        {
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = 910, PartsOfSpeech = ["noun"] });
            jitenDb.WordForms.Add(new JmDictWordForm { WordId = 910, ReadingIndex = 0, Text = "山", RubyText = "山[やま]", FormType = JmDictFormType.KanjiForm });
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = 911, PartsOfSpeech = ["noun"] });
            jitenDb.WordForms.Add(new JmDictWordForm { WordId = 911, ReadingIndex = 0, Text = "川", RubyText = "川[かわ]", FormType = JmDictFormType.KanjiForm });
            await jitenDb.SaveChangesAsync();
        }

        scope.ServiceProvider.GetRequiredService<IWordFormSiblingCache>().Reload();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<List<UserReviewDaily>> Rollup()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.UserReviewDailies.AsNoTracking()
                           .Where(d => d.UserId == TestUsers.UserA)
                           .OrderBy(d => d.LocalDate)
                           .ToListAsync();
    }

    private Task<HttpResponseMessage> Review(int wordId, FsrsRating rating, int? durationMs = null)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/review")
                             .WithUser(TestUsers.UserA)
                             .WithJsonContent(new { wordId, readingIndex = 0, rating = (int)rating, reviewDuration = durationMs }));

    [Fact]
    public async Task Reviewing_IncrementsTodaysCounters()
    {
        (await Review(910, FsrsRating.Good, 2500)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Review(911, FsrsRating.Again, 1500)).StatusCode.Should().Be(HttpStatusCode.OK);

        var rollup = await Rollup();
        rollup.Should().ContainSingle();
        rollup[0].LocalDate.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow));
        rollup[0].ReviewCount.Should().Be(2);
        rollup[0].CorrectCount.Should().Be(1);
        rollup[0].NewCardCount.Should().Be(2);
        rollup[0].TotalDurationMs.Should().Be(4000);
    }

    [Fact]
    public async Task ReviewingTheSameCardTwice_CountsOneNewCard()
    {
        await Review(910, FsrsRating.Good);
        await Review(910, FsrsRating.Good);

        var rollup = await Rollup();
        rollup[0].ReviewCount.Should().Be(2);
        rollup[0].NewCardCount.Should().Be(1);
    }

    [Fact]
    public async Task UndoingAReview_DecrementsTheDay()
    {
        await Review(910, FsrsRating.Good, 2000);
        await Review(910, FsrsRating.Good, 2000);

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/undo-review")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new { wordId = 910, readingIndex = 0 }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var rollup = await Rollup();
        rollup[0].ReviewCount.Should().Be(1);
        rollup[0].CorrectCount.Should().Be(1);
        rollup[0].TotalDurationMs.Should().Be(2000);
    }

    [Fact]
    public async Task DeletingACard_LeavesTheActivityRecordAlone()
    {
        await Review(910, FsrsRating.Good, 2000);

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/set-vocabulary-state")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new { wordId = 910, readingIndex = 0, state = "forget-add" }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var rollup = await Rollup();
        rollup.Should().ContainSingle();
        rollup[0].ReviewCount.Should().Be(1);
    }

    [Fact]
    public async Task Rebuild_ReproducesCountsFromLiveLogsAndArchivedHistory()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

            var live = new FsrsCard(TestUsers.UserA, 910, 0, state: FsrsState.Review, due: Base.AddDays(30));
            userDb.FsrsCards.Add(live);
            await userDb.SaveChangesAsync();

            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(live.CardId, FsrsRating.Good, Base, 1000));
            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(live.CardId, FsrsRating.Again, Base.AddHours(2), 2000));
            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(live.CardId, FsrsRating.Good, Base.AddDays(1), 3000));

            var packed = ReviewLogPacker.Pack(new List<PackedReview>
                                              {
                                                  new(FsrsRating.Good, Base, 500),
                                                  new(FsrsRating.Easy, Base.AddDays(2), 700)
                                              });

            userDb.FsrsCardArchives.Add(new FsrsCardArchive
                                        {
                                            UserId = TestUsers.UserA, WordId = 911, ReadingIndex = 0,
                                            ArchivedAt = Base.AddDays(5), Reason = CardArchiveReason.Forget,
                                            State = FsrsState.Review, Due = Base.AddDays(10),
                                            CardCreatedAt = Base, ReviewCount = packed.ReviewCount,
                                            FirstReview = packed.FirstReview, Logs = packed.Logs
                                        });

            await userDb.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ReviewRollupJob>().RebuildForUser(TestUsers.UserA);
        }

        var rollup = await Rollup();
        rollup.Should().HaveCount(3);

        // Day 0: two live reviews (one Again) plus one archived review, two of them each a card's first.
        rollup[0].LocalDate.Should().Be(DateOnly.FromDateTime(Base));
        rollup[0].ReviewCount.Should().Be(3);
        rollup[0].CorrectCount.Should().Be(2);
        rollup[0].NewCardCount.Should().Be(2);
        rollup[0].TotalDurationMs.Should().Be(3500);

        rollup[1].LocalDate.Should().Be(DateOnly.FromDateTime(Base.AddDays(1)));
        rollup[1].ReviewCount.Should().Be(1);
        rollup[1].NewCardCount.Should().Be(0);

        rollup[2].LocalDate.Should().Be(DateOnly.FromDateTime(Base.AddDays(2)));
        rollup[2].ReviewCount.Should().Be(1);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<UserDbContext>();
        var metadata = await verifyDb.UserMetadatas.AsNoTracking().FirstAsync(m => m.UserId == TestUsers.UserA);
        metadata.ReviewRollupDirty.Should().BeFalse();
        metadata.ReviewRollupRebuiltAt.Should().NotBeNull();
    }

    [Fact]
    public async Task PurgingReviewHistory_DeletesLogsAndClearsArchivedHistoryButKeepsTheEntry()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

            var live = new FsrsCard(TestUsers.UserA, 910, 0, state: FsrsState.Review, due: Base.AddDays(30));
            userDb.FsrsCards.Add(live);
            await userDb.SaveChangesAsync();

            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(live.CardId, FsrsRating.Good, Base, 1000));
            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(live.CardId, FsrsRating.Good, Base.AddDays(100), 1000));

            var packed = ReviewLogPacker.Pack(new List<PackedReview>
                                              {
                                                  new(FsrsRating.Good, Base.AddDays(1), 500),
                                                  new(FsrsRating.Good, Base.AddDays(101), 500)
                                              });

            userDb.FsrsCardArchives.Add(new FsrsCardArchive
                                        {
                                            UserId = TestUsers.UserA, WordId = 911, ReadingIndex = 0,
                                            ArchivedAt = Base.AddDays(120), Reason = CardArchiveReason.Forget,
                                            State = FsrsState.Review, Due = Base.AddDays(130), CardCreatedAt = Base,
                                            ReviewCount = packed.ReviewCount, FirstReview = packed.FirstReview, Logs = packed.Logs
                                        });

            await userDb.SaveChangesAsync();
        }

        var until = Base.AddDays(50).ToString("O");
        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"/api/user/vocabulary/review-history?until={Uri.EscapeDataString(until)}")
                .WithUser(TestUsers.UserA));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        payload.GetProperty("deletedLogs").GetInt32().Should().Be(1);
        payload.GetProperty("clearedArchives").GetInt32().Should().Be(1);
        payload.GetProperty("deletedCards").GetInt32().Should().Be(0);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<UserDbContext>();

        (await verifyDb.FsrsReviewLogs.CountAsync()).Should().Be(1);
        (await verifyDb.FsrsCards.CountAsync(c => c.UserId == TestUsers.UserA)).Should().Be(1);

        var archive = await verifyDb.FsrsCardArchives.AsNoTracking().SingleAsync();
        archive.ReviewCount.Should().Be(1);
        ReviewLogPacker.Unpack(archive.Logs!, archive.FirstReview!.Value).Single().ReviewDateTime.Should().Be(Base.AddDays(101));
    }

    [Fact]
    public async Task PurgingEverything_RemovesTheCardsLeftWithNoHistory()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

            var studied = new FsrsCard(TestUsers.UserA, 910, 0, state: FsrsState.Review, due: Base.AddDays(30));
            var learning = new FsrsCard(TestUsers.UserA, 911, 0, state: FsrsState.Learning, due: Base.AddDays(1));
            userDb.FsrsCards.AddRange(studied, learning);
            await userDb.SaveChangesAsync();

            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(studied.CardId, FsrsRating.Good, Base, 1000));
            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(learning.CardId, FsrsRating.Again, Base.AddDays(2), 1000));
            await userDb.SaveChangesAsync();
        }

        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, "/api/user/vocabulary/review-history").WithUser(TestUsers.UserA));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        payload.GetProperty("deletedLogs").GetInt32().Should().Be(2);
        payload.GetProperty("deletedCards").GetInt32().Should().Be(2);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await verifyDb.FsrsCards.CountAsync(c => c.UserId == TestUsers.UserA)).Should().Be(0);
    }

    /// <summary>
    /// The states a user sets by hand, and the ones an import writes without any review behind them, are not
    /// schedules the erased history was holding up.
    /// </summary>
    [Fact]
    public async Task PurgingEverything_KeepsMarkedKnownBlacklistedAndSuspendedForms()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

            userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, 910, 0, due: Base, lastReview: Base, state: FsrsState.Mastered));
            userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, 911, 0, state: FsrsState.Blacklisted));
            userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, 912, 0, state: FsrsState.Suspended));

            var reviewedThenMastered = new FsrsCard(TestUsers.UserA, 913, 0, due: Base, lastReview: Base, state: FsrsState.Mastered);
            userDb.FsrsCards.Add(reviewedThenMastered);
            await userDb.SaveChangesAsync();

            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(reviewedThenMastered.CardId, FsrsRating.Good, Base, 1000));
            await userDb.SaveChangesAsync();
        }

        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, "/api/user/vocabulary/review-history").WithUser(TestUsers.UserA));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        payload.GetProperty("deletedLogs").GetInt32().Should().Be(1);
        payload.GetProperty("deletedCards").GetInt32().Should().Be(0);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await verifyDb.FsrsCards.CountAsync(c => c.UserId == TestUsers.UserA)).Should().Be(4);
    }

    /// <summary>
    /// Two reviews a day apart on a card that has since been removed. The second one is the first with a
    /// gap of at least a day, so it is the one retention counts.
    /// </summary>
    private async Task SeedArchivedCard(int wordId, FsrsRating secondRating, int? durationMs = 4000)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var packed = ReviewLogPacker.Pack(new List<PackedReview>
                                          {
                                              new(FsrsRating.Good, DateTime.UtcNow.AddDays(-9), durationMs),
                                              new(secondRating, DateTime.UtcNow.AddDays(-8), durationMs)
                                          });

        userDb.FsrsCardArchives.Add(new FsrsCardArchive
                                    {
                                        UserId = TestUsers.UserA, WordId = wordId, ReadingIndex = 0,
                                        ArchivedAt = DateTime.UtcNow, Reason = CardArchiveReason.Forget,
                                        State = FsrsState.Review, Due = DateTime.UtcNow, CardCreatedAt = DateTime.UtcNow.AddDays(-9),
                                        ReviewCount = packed.ReviewCount, FirstReview = packed.FirstReview, Logs = packed.Logs
                                    });
        await userDb.SaveChangesAsync();
    }

    [Fact]
    public async Task Retention_CountsReviewsFromRemovedCards()
    {
        await SeedArchivedCard(910, FsrsRating.Good);
        await SeedArchivedCard(911, FsrsRating.Again);

        var payload = await (await _client.SendAsync(
                                 new HttpRequestMessage(HttpMethod.Get, "/api/srs/retention").WithUser(TestUsers.UserA)))
                            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        var all = payload.GetProperty("windows").GetProperty("all").GetProperty("overall");
        all.GetProperty("total").GetInt32().Should().Be(2);
        all.GetProperty("passed").GetInt32().Should().Be(1);

        payload.GetProperty("monthly").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RemovingACard_DoesNotMoveRetention()
    {
        await SeedArchivedCard(910, FsrsRating.Good);

        var before = await (await _client.SendAsync(
                                new HttpRequestMessage(HttpMethod.Get, "/api/srs/retention").WithUser(TestUsers.UserA)))
                           .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        // Restore it, then remove it again: the corpus must be the same whichever side of the fence it sits on.
        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/archive/restore")
                                 .WithUser(TestUsers.UserA)
                                 .WithJsonContent(new { forms = new[] { new { wordId = 910, readingIndex = 0 } } })))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var afterRestore = await (await _client.SendAsync(
                                      new HttpRequestMessage(HttpMethod.Get, "/api/srs/retention").WithUser(TestUsers.UserA)))
                                 .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        afterRestore.GetProperty("windows").GetProperty("all").GetProperty("overall").GetProperty("total").GetInt32()
                    .Should().Be(before.GetProperty("windows").GetProperty("all").GetProperty("overall").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task FsrsReviewCount_IncludesArchivedHistory()
    {
        await SeedArchivedCard(910, FsrsRating.Good);
        await Review(911, FsrsRating.Good);

        var payload = await (await _client.SendAsync(
                                 new HttpRequestMessage(HttpMethod.Get, "/api/srs/settings").WithUser(TestUsers.UserA)))
                            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        payload.GetProperty("reviewCount").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task SrsHealth_CountsArchivedRatings()
    {
        await SeedArchivedCard(910, FsrsRating.Again);

        var payload = await (await _client.SendAsync(
                                 new HttpRequestMessage(HttpMethod.Get, "/api/srs/settings/health").WithUser(TestUsers.UserA)))
                            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        payload.GetProperty("totalReviews").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task RemappingHardReviews_RewritesArchivedHistoryToo()
    {
        await SeedArchivedCard(910, FsrsRating.Hard);

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/settings/remap-hard")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new { reschedule = false }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("remapped").GetInt32().Should().Be(1);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var row = await userDb.FsrsCardArchives.AsNoTracking().SingleAsync();
        var reviews = ReviewLogPacker.Unpack(row.Logs!, row.FirstReview!.Value);

        reviews.Should().NotContain(r => r.Rating == FsrsRating.Hard);
        reviews[1].Rating.Should().Be(FsrsRating.Again);
    }

    [Fact]
    public async Task BackupRoundTrip_CarriesTheActivityCountersAndTheyWinOverARebuild()
    {
        await Review(910, FsrsRating.Good, 2500);
        await Review(911, FsrsRating.Again, 1500);

        var export = await (await _client.SendAsync(
                                new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/export").WithUser(TestUsers.UserA)))
                           .Content.ReadFromJsonAsync<FsrsExportDto>();

        export!.ReviewActivity.Should().ContainSingle();
        export.ReviewActivity![0].ReviewCount.Should().Be(2);
        export.ReviewActivity[0].CorrectCount.Should().Be(1);
        export.ReviewActivity[0].TotalDurationMs.Should().Be(4000);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            await userDb.UserReviewDailies.ExecuteDeleteAsync();
        }

        var import = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import?overwrite=true")
                .WithUser(TestUsers.UserA)
                .WithJsonContent(export));
        import.StatusCode.Should().Be(HttpStatusCode.OK);

        var rollup = await Rollup();
        rollup.Should().ContainSingle();
        rollup[0].ReviewCount.Should().Be(2);
        rollup[0].TotalDurationMs.Should().Be(4000);

        // A carried section must not leave the account flagged for a rebuild that would overwrite it.
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<UserDbContext>();
        var metadata = await verifyDb.UserMetadatas.AsNoTracking().FirstAsync(m => m.UserId == TestUsers.UserA);
        metadata.ReviewRollupDirty.Should().BeFalse();
        metadata.ReviewRollupRebuiltAt.Should().NotBeNull();
    }

    [Fact]
    public async Task BackupWithoutAnActivitySection_FallsBackToARebuild()
    {
        await Review(910, FsrsRating.Good, 2500);

        var export = await (await _client.SendAsync(
                                new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/export").WithUser(TestUsers.UserA)))
                           .Content.ReadFromJsonAsync<FsrsExportDto>();
        export!.ReviewActivity = null;

        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import?overwrite=true")
                                 .WithUser(TestUsers.UserA)
                                 .WithJsonContent(export)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await userDb.UserMetadatas.AsNoTracking().FirstAsync(m => m.UserId == TestUsers.UserA))
            .ReviewRollupDirty.Should().BeTrue();
    }

    [Fact]
    public async Task ResettingActivityHistory_ClearsTheCountersAndFlagsARebuild()
    {
        await Review(910, FsrsRating.Good, 2500);
        (await Rollup()).Should().ContainSingle();

        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/review-activity/rebuild").WithUser(TestUsers.UserA));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("clearedDays").GetInt32().Should().Be(1);
        (await Rollup()).Should().BeEmpty();

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            (await userDb.UserMetadatas.AsNoTracking().FirstAsync(m => m.UserId == TestUsers.UserA))
                .ReviewRollupDirty.Should().BeTrue();

            await scope.ServiceProvider.GetRequiredService<ReviewRollupJob>().RebuildForUser(TestUsers.UserA);
        }

        // The counters come back because they are derived, which is the whole point of offering the reset.
        var rebuilt = await Rollup();
        rebuilt.Should().ContainSingle();
        rebuilt[0].ReviewCount.Should().Be(1);
    }

    /// <summary>
    /// Each review is bucketed at the offset in force when it happened, so the same history rebuilds to the
    /// same days whatever time of year the rebuild runs. Both timestamps are 22:30 UTC and land either side
    /// of local midnight precisely because Paris is +1 in January and +2 in July.
    /// </summary>
    [Fact]
    public async Task RebuildingAcrossADstBoundary_BucketsEachReviewAtItsOwnOffset()
    {
        var winter = new DateTime(2024, 1, 15, 22, 30, 0, DateTimeKind.Utc);
        var summer = new DateTime(2024, 7, 15, 22, 30, 0, DateTimeKind.Utc);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

            userDb.UserFsrsSettings.Add(new Jiten.Core.Data.User.UserFsrsSettings
                                        {
                                            UserId = TestUsers.UserA,
                                            SettingsJson = System.Text.Json.JsonSerializer.Serialize(
                                                new Jiten.Api.Dtos.StudySettingsDto { Timezone = "Europe/Paris" })
                                        });

            var live = new FsrsCard(TestUsers.UserA, 910, 0, state: FsrsState.Review, due: summer.AddDays(30));
            userDb.FsrsCards.Add(live);
            await userDb.SaveChangesAsync();

            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(live.CardId, FsrsRating.Good, winter, 1000));
            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(live.CardId, FsrsRating.Good, summer, 1000));
            await userDb.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ReviewRollupJob>().RebuildForUser(TestUsers.UserA);
        }

        var rollup = await Rollup();
        rollup.Should().HaveCount(2);
        rollup[0].LocalDate.Should().Be(new DateOnly(2024, 1, 15));
        rollup[1].LocalDate.Should().Be(new DateOnly(2024, 7, 16));
    }

    /// <summary>
    /// The fallback the heatmap uses before a user's rebuild lands must bucket reviews at each instant's own
    /// offset, exactly as the rollup will, or the backfill visibly moves days near DST transitions.
    /// </summary>
    [Fact]
    public async Task HeatmapFallback_BucketsDstReviewsExactlyAsTheRollupWill()
    {
        var winter = new DateTime(2024, 1, 15, 22, 30, 0, DateTimeKind.Utc);
        var summer = new DateTime(2024, 7, 15, 22, 30, 0, DateTimeKind.Utc);

        string userName;
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

            userDb.UserFsrsSettings.Add(new Jiten.Core.Data.User.UserFsrsSettings
                                        {
                                            UserId = TestUsers.UserA,
                                            SettingsJson = System.Text.Json.JsonSerializer.Serialize(
                                                new Jiten.Api.Dtos.StudySettingsDto { Timezone = "Europe/Paris" })
                                        });

            var live = new FsrsCard(TestUsers.UserA, 910, 0, state: FsrsState.Review, due: summer.AddDays(30));
            userDb.FsrsCards.Add(live);
            await userDb.SaveChangesAsync();

            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(live.CardId, FsrsRating.Good, winter, 1000));
            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(live.CardId, FsrsRating.Good, summer, 1000));

            var user = await userDb.Users.FirstAsync(u => u.Id == TestUsers.UserA);
            userName = user.UserName!;
            user.NormalizedUserName = userName.ToUpperInvariant();
            await userDb.SaveChangesAsync();
        }

        async Task<List<string>> HeatmapDays()
        {
            var response = await _client.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, $"/api/user/profile/{userName}/study-heatmap?year=2024")
                    .WithUser(TestUsers.UserA));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var payload = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            return payload.GetProperty("days").EnumerateArray()
                          .Select(d => d.GetProperty("date").GetString()!)
                          .OrderBy(d => d)
                          .ToList();
        }

        var fallbackDays = await HeatmapDays();
        fallbackDays.Should().Equal("2024-01-15", "2024-07-16");

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ReviewRollupJob>().RebuildForUser(TestUsers.UserA);
        }

        (await HeatmapDays()).Should().Equal(fallbackDays);
    }

    [Fact]
    public async Task Heatmap_ReadsFromTheRollupOnceItIsBuilt()
    {
        await Review(910, FsrsRating.Good, 1000);
        await Review(911, FsrsRating.Good, 1000);

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ReviewRollupJob>().RebuildForUser(TestUsers.UserA);
        }

        string userName;
        using (var seedScope = factory.Services.CreateScope())
        {
            var userDb = seedScope.ServiceProvider.GetRequiredService<UserDbContext>();
            var user = await userDb.Users.FirstAsync(u => u.Id == TestUsers.UserA);
            userName = user.UserName!;
            user.NormalizedUserName = userName.ToUpperInvariant();
            await userDb.SaveChangesAsync();
        }

        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/user/profile/{userName}/study-heatmap")
                .WithUser(TestUsers.UserA));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        payload.GetProperty("totalReviews").GetInt32().Should().Be(2);
        payload.GetProperty("currentStreak").GetInt32().Should().Be(1);
    }
}
