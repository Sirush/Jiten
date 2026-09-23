using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class FsrsVersionSettingsTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsReviewLogs.Where(l => l.Card.UserId == TestUsers.UserA).ExecuteDeleteAsync();
        await userDb.FsrsCards.Where(c => c.UserId == TestUsers.UserA).ExecuteDeleteAsync();
        await userDb.UserFsrsSettings.Where(s => s.UserId == TestUsers.UserA).ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Get_ServesVersionAndDefaults()
    {
        var body = await GetSettings();

        body.GetProperty("version").GetInt32().Should().Be((int)FsrsVersions.Unoptimised);
        body.GetProperty("defaultParameters").EnumerateArray().Select(e => e.GetDouble())
            .Should().Equal(FsrsVersions.DefaultParameters(FsrsVersions.Unoptimised));
    }

    [Fact]
    public async Task RetentionOnlySave_StoresEmptyParameters()
    {
        var response = await Put(new { desiredRetention = 0.85 });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await StoredParametersJson()).Should().Be("[]");
        (await GetSettings()).GetProperty("desiredRetention").GetDouble().Should().BeApproximately(0.85, 1e-9);
    }

    [Fact]
    public async Task ExplicitDefaultPaste_StoresEmptyParameters()
    {
        var csv = string.Join(",", FsrsConstants.DefaultParameters.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var response = await Put(new { parameters = csv, desiredRetention = 0.85 });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await StoredParametersJson()).Should().Be("[]");
    }

    [Fact]
    public async Task Fsrs7Paste_SwitchesVersionAndRebuildsStatesWithoutRescheduling()
    {
        var due = await SeedReviewedCard();

        var response = await Put(new { parameters = Csv(FsrsConstants.DefaultParametersV7) });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetSettings()).GetProperty("version").GetInt32().Should().Be(7);
        var card = await LoadCard();
        card.StabilityFast.Should().NotBeNull();
        card.Due.Should().BeCloseTo(due, TimeSpan.FromSeconds(1));
        card.Stability.Should().BeApproximately(ReplayV7(), 1e-9);
    }

    [Fact]
    public async Task SwitchModel_ToFsrs7AndBack_RestoresTheEarlierParameters()
    {
        await SeedReviewedCard();
        var custom = FsrsConstants.DefaultParameters.ToArray();
        custom[0] = 0.5;
        (await Put(new { parameters = Csv(custom) })).StatusCode.Should().Be(HttpStatusCode.OK);

        var toV7 = await SwitchModel(7);
        toV7.GetProperty("version").GetInt32().Should().Be(7);
        toV7.GetProperty("parameters").GetString().Should().StartWith("0.1104,");
        (await LoadCard()).StabilityFast.Should().NotBeNull();

        var toV6 = await SwitchModel(6);
        toV6.GetProperty("version").GetInt32().Should().Be(6);
        toV6.GetProperty("parameters").GetString().Should().StartWith("0.5,");
        (await LoadCard()).StabilityFast.Should().BeNull();
    }

    [Fact]
    public async Task PastedSwitch_KeepsTheEarlierParametersForTheSelector()
    {
        var custom = FsrsConstants.DefaultParameters.ToArray();
        custom[0] = 0.5;
        (await Put(new { parameters = Csv(custom) })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Put(new { parameters = Csv(FsrsConstants.DefaultParametersV7) })).StatusCode.Should().Be(HttpStatusCode.OK);

        var back = await SwitchModel(6);
        back.GetProperty("parameters").GetString().Should().StartWith("0.5,");
    }

    [Fact]
    public async Task SwitchModel_RejectsUnknownVersions()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/settings/model")
                                               .WithUser(TestUsers.UserA).WithJsonContent(new { version = 5 }));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReviewUnderFsrs7_WritesTheFastTrace()
    {
        await SwitchModel(7);

        var review = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/review")
                                             .WithUser(TestUsers.UserA)
                                             .WithJsonContent(new { wordId = 1, readingIndex = 0, rating = 3 }));
        review.StatusCode.Should().Be(HttpStatusCode.OK);

        var card = await LoadCard(1);
        card.StabilityFast.Should().BeApproximately(FsrsConstants.DefaultParametersV7[2] * 0.8, 1e-9);
    }

    [Fact]
    public async Task WrongCountPaste_IsRejected()
    {
        var response = await Put(new { parameters = "1,2,3" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CustomFsrs6Paste_IsStoredAsIs()
    {
        var custom = FsrsConstants.DefaultParameters.ToArray();
        custom[0] = 0.5;
        var csv = string.Join(",", custom.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var response = await Put(new { parameters = csv });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await GetSettings();
        body.GetProperty("version").GetInt32().Should().Be(6);
        body.GetProperty("isDefault").GetBoolean().Should().BeFalse();
        body.GetProperty("parameters").GetString().Should().StartWith("0.5,");
    }

    private const int SeedWordId = 424242;
    private static readonly DateTime SeedStart = DateTime.UtcNow.Date.AddDays(-40);
    private static readonly (FsrsRating Rating, double Days)[] SeedReviews = [(FsrsRating.Good, 0), (FsrsRating.Good, 0.004), (FsrsRating.Again, 6), (FsrsRating.Good, 6.01), (FsrsRating.Hard, 20)];

    /// <summary>A reviewed card holding FSRS-6 state, the shape every existing card has before a switch.</summary>
    private async Task<DateTime> SeedReviewedCard()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsCards.Where(c => c.UserId == TestUsers.UserA).ExecuteDeleteAsync();

        var due = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(9), DateTimeKind.Utc);
        var card = new FsrsCard(TestUsers.UserA, SeedWordId, 0, state: FsrsState.Review, due: due, lastReview: SeedStart.AddDays(20))
                   {
                       Stability = 12.5, Difficulty = 6.1,
                       ReviewLogs = SeedReviews.Select(r => new FsrsReviewLog { Rating = r.Rating, ReviewDateTime = SeedStart.AddDays(r.Days) }).ToList()
                   };
        userDb.FsrsCards.Add(card);
        await userDb.SaveChangesAsync();
        return due;
    }

    private static double ReplayV7()
    {
        var w = FsrsConstants.DefaultParametersV7;
        var state = FsrsHelperV7.InitialState(SeedReviews[0].Rating, w);
        for (var i = 1; i < SeedReviews.Length; i++)
            state = FsrsHelperV7.NextState(state, SeedReviews[i].Days - SeedReviews[i - 1].Days, SeedReviews[i].Rating, w);
        return state.Stability;
    }

    private async Task<FsrsCard> LoadCard(int wordId = SeedWordId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.FsrsCards.AsNoTracking().SingleAsync(c => c.UserId == TestUsers.UserA && c.WordId == wordId);
    }

    private async Task<JsonElement> SwitchModel(int version)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/settings/model")
                                               .WithUser(TestUsers.UserA).WithJsonContent(new { version }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string Csv(double[] parameters)
        => string.Join(",", parameters.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private Task<HttpResponseMessage> Put(object body)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/srs/settings").WithUser(TestUsers.UserA).WithJsonContent(body));

    private async Task<JsonElement> GetSettings()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/srs/settings").WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string?> StoredParametersJson()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.UserFsrsSettings.Where(s => s.UserId == TestUsers.UserA).Select(s => s.ParametersJson).SingleOrDefaultAsync();
    }
}
