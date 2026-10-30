using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class ReaderLeechTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.UserFsrsSettings.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SetLeechThreshold(int threshold)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserFsrsSettings.Add(new UserFsrsSettings
                                    {
                                        UserId = TestUsers.UserA,
                                        SettingsJson = JsonSerializer.Serialize(new StudySettingsDto { LeechThreshold = threshold })
                                    });
        await userDb.SaveChangesAsync();
    }

    private async Task SeedCard(int wordId, int lapses, FsrsState state = FsrsState.Review, double stability = 4)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, wordId, 0, state: state, stability: stability, difficulty: 8,
                                          due: DateTime.UtcNow.AddDays(1), lastReview: DateTime.UtcNow.AddDays(-3)) { Lapses = lapses });
        await userDb.SaveChangesAsync();
    }

    private async Task<JsonElement> Lookup(params int[] wordIds)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/reader/lookup-vocabulary")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new { words = wordIds.Select(id => new[] { id, 0 }).ToArray() }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<bool> Leeches(JsonElement body)
        => body.GetProperty("leeches").EnumerateArray().Select(e => e.GetBoolean()).ToList();

    [Fact]
    public async Task LookupVocabulary_FlagsOnlyActiveLeeches_InRequestOrder()
    {
        await SeedCard(1, lapses: 9);
        await SeedCard(2, lapses: 3);
        await SeedCard(3, lapses: 9, stability: 60);
        await SeedCard(4, lapses: 9, state: FsrsState.Mastered);
        await SeedCard(5, lapses: 9, state: FsrsState.Blacklisted);

        var body = await Lookup(2, 1, 3, 4, 5, 6);

        Leeches(body).Should().Equal(false, true, false, false, false, false);
    }

    [Fact]
    public async Task LookupVocabulary_SuspendedLeech_KeepsItsStatesAndIsFlagged()
    {
        await SeedCard(1, lapses: 9, state: FsrsState.Suspended);

        var body = await Lookup(1);

        Leeches(body).Should().Equal(true);
        body.GetProperty("result")[0].EnumerateArray().Select(s => (KnownState)s.GetInt32())
            .Should().Contain(KnownState.Suspended);
    }

    [Fact]
    public async Task LookupVocabulary_UsesTheUsersThreshold()
    {
        await SetLeechThreshold(3);
        await SeedCard(1, lapses: 3);

        Leeches(await Lookup(1)).Should().Equal(true);
    }

    [Fact]
    public async Task LookupVocabulary_ThresholdZero_FlagsNothing()
    {
        await SetLeechThreshold(0);
        await SeedCard(1, lapses: 20);

        Leeches(await Lookup(1)).Should().Equal(false);
    }
}
