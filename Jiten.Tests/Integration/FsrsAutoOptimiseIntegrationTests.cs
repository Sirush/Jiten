using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Jobs;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class FsrsAutoOptimiseIntegrationTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();
    private const int SeedCards = 400;
    private const int FirstWordId = 610_000;
    private static readonly double[] Gaps = [8, 25, 70, 160];

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsReviewLogs.Where(l => l.Card.UserId == TestUsers.UserA).ExecuteDeleteAsync();
        await userDb.FsrsCards.Where(c => c.UserId == TestUsers.UserA).ExecuteDeleteAsync();
        await userDb.FsrsParameterHistories.Where(h => h.UserId == TestUsers.UserA).ExecuteDeleteAsync();
        await userDb.UserReviewDailies.Where(d => d.UserId == TestUsers.UserA).ExecuteDeleteAsync();
        await userDb.UserFsrsSettings.Where(s => s.UserId == TestUsers.UserA).ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Get_ServesProvenanceDefaults()
    {
        var body = await GetSettings();

        body.GetProperty("source").GetString().Should().Be("default");
        body.GetProperty("autoOptimise").GetBoolean().Should().BeTrue();
        body.GetProperty("undoHistoryId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task DryRun_ReportsTheDecisionAndSavesNothing()
    {
        await SeedHistory();

        var decisions = await Job().Sweep(dryRun: true, limit: 10, TestUsers.UserA);

        decisions.Should().ContainSingle().Which.Outcome.Should().Be(FsrsAutoOptimiseOutcome.Accepted);
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await userDb.UserFsrsSettings.AnyAsync(s => s.UserId == TestUsers.UserA)).Should().BeFalse();
        (await userDb.FsrsParameterHistories.AnyAsync(h => h.UserId == TestUsers.UserA)).Should().BeFalse();
    }

    [Fact]
    public async Task Run_AppliesTheFitWithoutMovingDueDates_AndUndoRestoresTheDefaults()
    {
        await SeedHistory();
        var before = await LoadCards();

        var decision = (await Job().Sweep(dryRun: false, limit: 10, TestUsers.UserA)).Should().ContainSingle().Subject;

        decision.Outcome.Should().Be(FsrsAutoOptimiseOutcome.Accepted);
        decision.Saved.Should().BeTrue();
        decision.NewLoss.Should().BeLessThan(decision.CurrentLoss!.Value);
        var after = await LoadCards();
        after.Select(c => c.Due).Should().Equal(before.Select(c => c.Due));
        after.Select(c => c.Stability).Should().NotEqual(before.Select(c => c.Stability));

        var settings = await GetSettings();
        settings.GetProperty("source").GetString().Should().Be("auto");
        settings.GetProperty("fittedReviewCount").GetInt32().Should().Be(SeedCards * (Gaps.Length + 1));
        (await StoredSettings())!.AutoOptimiseLastOutcome.Should().Be(FsrsAutoOptimiseOutcome.Accepted);
        var undoId = settings.GetProperty("undoHistoryId").GetInt64();

        (await Job().Sweep(dryRun: false, limit: 10, TestUsers.UserA)).Should().BeEmpty();

        var restore = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/srs/settings/history/{undoId}/restore").WithUser(TestUsers.UserA));
        restore.StatusCode.Should().Be(HttpStatusCode.OK);
        var restored = await restore.Content.ReadFromJsonAsync<JsonElement>();
        restored.GetProperty("source").GetString().Should().Be("default");
        restored.GetProperty("isDefault").GetBoolean().Should().BeTrue();
        (await StoredSettings())!.ParametersJson.Should().Be("[]");
        (await LoadCards()).Select(c => c.Due).Should().Equal(before.Select(c => c.Due));

        var history = await GetHistory();
        history.Should().HaveCount(2);
        history[0].GetProperty("source").GetString().Should().Be("auto");
        history[0].GetProperty("revertedAtUtc").ValueKind.Should().NotBe(JsonValueKind.Null);
        history[1].GetProperty("isCurrent").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task OptedOutAndEditedUsers_AreNotCandidates()
    {
        await SeedHistory();

        (await Put(new { autoOptimise = false })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Job().SelectCandidates(DateTime.UtcNow, 10, TestUsers.UserA)).Should().BeEmpty();

        var custom = FsrsConstants.DefaultParametersV7.ToArray();
        custom[0] = 0.5;
        (await Put(new { parameters = Csv(custom), autoOptimise = true })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredSettings())!.ParametersSource.Should().Be(FsrsParametersSource.Edited);
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            await userDb.UserFsrsSettings.Where(s => s.UserId == TestUsers.UserA)
                        .ExecuteUpdateAsync(u => u.SetProperty(s => s.AutoOptimiseAttemptedAtUtc, (DateTime?)null)
                                                  .SetProperty(s => s.AutoOptimiseAttemptedReviewCount, 0));
        }
        (await Job().SelectCandidates(DateTime.UtcNow, 10, TestUsers.UserA)).Should().BeEmpty();
    }

    [Fact]
    public async Task AutoOptimiseChoice_SurvivesASaveThatOmitsIt()
    {
        (await Put(new { autoOptimise = false })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Put(new { desiredRetention = 0.85 })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetSettings()).GetProperty("autoOptimise").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task SavingRetentionWithTheServedCsv_KeepsParametersAndProvenance()
    {
        var fitted = FsrsConstants.DefaultParametersV7.Select(v => v * 1.0123456789).ToArray();
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.UserFsrsSettings.Add(new UserFsrsSettings
                                        {
                                            UserId = TestUsers.UserA, Parameters = fitted, ParametersSource = FsrsParametersSource.Auto
                                        });
            await userDb.SaveChangesAsync();
        }
        var servedCsv = (await GetSettings()).GetProperty("parameters").GetString();

        (await Put(new { parameters = servedCsv, desiredRetention = 0.86 })).StatusCode.Should().Be(HttpStatusCode.OK);

        var stored = await StoredSettings();
        stored!.ParametersSource.Should().Be(FsrsParametersSource.Auto);
        stored.GetParametersOnce().Should().Equal(fitted);
        stored.DesiredRetention.Should().BeApproximately(0.86, 1e-9);
        (await GetHistory()).Should().BeEmpty();
    }

    [Fact]
    public async Task EditingParameters_RecordsTheReplacedSet_AndUndoBringsItBack()
    {
        var custom = FsrsConstants.DefaultParametersV7.ToArray();
        custom[0] = 0.5;

        var edited = await Put(new { parameters = Csv(custom) });
        var body = await edited.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("source").GetString().Should().Be("edited");

        var history = await GetHistory();
        history.Should().HaveCount(2);
        history[1].GetProperty("source").GetString().Should().Be("default");

        var undo = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post,
                                                                  $"/api/srs/settings/history/{body.GetProperty("undoHistoryId").GetInt64()}/restore")
                                               .WithUser(TestUsers.UserA));
        undo.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredSettings())!.ParametersSource.Should().Be(FsrsParametersSource.Default);
        (await GetSettings()).GetProperty("undoHistoryId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task RestoringTheCurrentRow_IsRejected()
    {
        var custom = FsrsConstants.DefaultParametersV7.ToArray();
        custom[0] = 0.5;
        await Put(new { parameters = Csv(custom) });
        var currentId = (await GetHistory())[0].GetProperty("id").GetInt64();

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/srs/settings/history/{currentId}/restore").WithUser(TestUsers.UserA));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task History_KeepsTheLastTenSets()
    {
        for (var i = 0; i < 12; i++)
        {
            var custom = FsrsConstants.DefaultParametersV7.ToArray();
            custom[0] = 0.3 + i * 0.01;
            (await Put(new { parameters = Csv(custom) })).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var history = await GetHistory();
        history.Should().HaveCount(10);
        history[0].GetProperty("parameters").GetString().Should().StartWith("0.41,");
    }

    [Fact]
    public async Task OtherUsersHistory_CannotBeRestored()
    {
        var custom = FsrsConstants.DefaultParametersV7.ToArray();
        custom[0] = 0.5;
        var body = await (await Put(new { parameters = Csv(custom) })).Content.ReadFromJsonAsync<JsonElement>();

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post,
                                                                      $"/api/srs/settings/history/{body.GetProperty("undoHistoryId").GetInt64()}/restore")
                                                   .WithUser(TestUsers.UserB));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Fsrs6ApplyWithoutReschedule_RebuildsMemoryStatesAndKeepsDueDates()
    {
        await SeedHistory();
        var toV6 = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/settings/model")
                                           .WithUser(TestUsers.UserA).WithJsonContent(new { version = 6 }));
        toV6.StatusCode.Should().Be(HttpStatusCode.OK);
        var before = await LoadCards();

        var custom = FsrsConstants.DefaultParameters.ToArray();
        custom[2] *= 3;
        var apply = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/settings/apply")
                                            .WithUser(TestUsers.UserA)
                                            .WithJsonContent(new { parameters = custom, desiredRetention = 0.9, reschedule = false }));
        apply.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await LoadCards();
        after.Select(c => c.Due).Should().Equal(before.Select(c => c.Due));
        after.Select(c => c.Stability).Should().NotEqual(before.Select(c => c.Stability));
        (await StoredSettings())!.ParametersSource.Should().Be(FsrsParametersSource.Optimised);
    }

    private FsrsAutoOptimiseJob Job() => factory.Services.CreateScope().ServiceProvider.GetRequiredService<FsrsAutoOptimiseJob>();

    /// <summary>Cards recalled at gaps far longer than the default parameters expect, so a fit clearly beats them.</summary>
    private async Task SeedHistory()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var rng = new Random(11);
        var today = DateTime.UtcNow.Date;
        var span = Gaps.Sum() + 12;

        for (var c = 0; c < SeedCards; c++)
        {
            var at = today.AddDays(-span + rng.Next(0, 8)).AddHours(9);
            var logs = new List<FsrsReviewLog> { new() { Rating = FsrsRating.Good, ReviewDateTime = at } };
            foreach (var gap in Gaps)
            {
                at = at.AddDays(gap + rng.Next(0, 3));
                logs.Add(new FsrsReviewLog { Rating = rng.NextDouble() < 0.06 ? FsrsRating.Again : FsrsRating.Good, ReviewDateTime = at });
            }

            userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, FirstWordId + c, 0, state: FsrsState.Review,
                                              due: DateTime.SpecifyKind(today.AddDays(5 + c % 30), DateTimeKind.Utc), lastReview: at)
                                 {
                                     Stability = 30, Difficulty = 5, StabilityFast = 1, ReviewLogs = logs
                                 });
        }

        userDb.UserReviewDailies.Add(new UserReviewDaily
                                     {
                                         UserId = TestUsers.UserA, LocalDate = DateOnly.FromDateTime(today), ReviewCount = SeedCards * (Gaps.Length + 1)
                                     });
        await userDb.SaveChangesAsync();
    }

    private async Task<List<FsrsCard>> LoadCards()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.FsrsCards.AsNoTracking().Where(c => c.UserId == TestUsers.UserA).OrderBy(c => c.WordId).ToListAsync();
    }

    private async Task<UserFsrsSettings?> StoredSettings()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.UserFsrsSettings.AsNoTracking().SingleOrDefaultAsync(s => s.UserId == TestUsers.UserA);
    }

    private static string Csv(double[] parameters) => string.Join(",", parameters.Select(v => v.ToString(CultureInfo.InvariantCulture)));

    private Task<HttpResponseMessage> Put(object body)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/srs/settings").WithUser(TestUsers.UserA).WithJsonContent(body));

    private async Task<JsonElement> GetSettings()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/srs/settings").WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<List<JsonElement>> GetHistory()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/srs/settings/history").WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<JsonElement>>())!;
    }
}
