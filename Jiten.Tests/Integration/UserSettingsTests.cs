using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class UserSettingsTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private const string Route = "/api/user/settings/media-filter-presets";

    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserSettings.RemoveRange(userDb.UserSettings);
        await userDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static MediaFilterPresetDto Preset(string name, params (string Key, string Value)[] query) =>
        new() { Name = name, Query = query.ToDictionary(q => q.Key, q => q.Value), CreatedAt = 1_700_000_000_000 };

    private async Task<MediaFilterPresetsDto> Put(string userId, MediaFilterPresetsDto body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, Route).WithUser(userId).WithJsonContent(body);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<MediaFilterPresetsDto>())!;
    }

    private async Task<MediaFilterPresetsDto> Get(string userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Route).WithUser(userId);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<MediaFilterPresetsDto>())!;
    }

    [Fact]
    public async Task Get_WithNothingSaved_ReturnsEmpty()
    {
        var dto = await Get(TestUsers.UserA);

        dto.Presets.Should().BeEmpty();
        dto.DefaultPreset.Should().BeNull();
    }

    [Fact]
    public async Task Put_ThenGet_RoundTripsPresets()
    {
        var body = new MediaFilterPresetsDto
                   {
                       Presets =
                       [
                           Preset("Hard novels", ("mediaType", "7"), ("sortBy", "difficulty"), ("sortOrder", "1"), ("difficultyMin", "4")),
                           Preset("Nearly known", ("totalCoverageMin", "90"), ("uTotalCoverageMin", "85")),
                       ],
                       DefaultPreset = "Nearly known",
                   };

        await Put(TestUsers.UserA, body);
        var stored = await Get(TestUsers.UserA);

        stored.Presets.Select(p => p.Name).Should().Equal("Hard novels", "Nearly known");
        stored.Presets[0].Query.Should().Equal(new Dictionary<string, string>
                                               {
                                                   ["mediaType"] = "7", ["sortBy"] = "difficulty", ["sortOrder"] = "1", ["difficultyMin"] = "4",
                                               });
        stored.Presets[0].CreatedAt.Should().Be(1_700_000_000_000);
        stored.DefaultPreset.Should().Be("Nearly known");
    }

    [Fact]
    public async Task Put_ReplacesTheWholeList()
    {
        await Put(TestUsers.UserA, new MediaFilterPresetsDto { Presets = [Preset("First"), Preset("Second")] });
        await Put(TestUsers.UserA, new MediaFilterPresetsDto { Presets = [Preset("Only")] });

        (await Get(TestUsers.UserA)).Presets.Select(p => p.Name).Should().Equal("Only");
    }

    [Fact]
    public async Task Presets_AreIsolatedPerUser()
    {
        await Put(TestUsers.UserA, new MediaFilterPresetsDto { Presets = [Preset("Mine", ("sortBy", "title"))], DefaultPreset = "Mine" });

        var otherUser = await Get(TestUsers.UserB);
        otherUser.Presets.Should().BeEmpty();
        otherUser.DefaultPreset.Should().BeNull();
    }

    [Fact]
    public async Task Put_TruncatesTheListToTheCap()
    {
        var body = new MediaFilterPresetsDto
                   {
                       Presets = Enumerable.Range(0, 51).Select(i => Preset($"Preset {i}", ("sortBy", "title"))).ToList(),
                   };

        var saved = await Put(TestUsers.UserA, body);

        saved.Presets.Should().HaveCount(50);
        saved.Presets.Last().Name.Should().Be("Preset 49");
        (await Get(TestUsers.UserA)).Presets.Should().HaveCount(50);
    }

    [Fact]
    public async Task Put_StripsQueryKeysTheBrowserDoesNotOwn()
    {
        var body = new MediaFilterPresetsDto
                   {
                       Presets = [Preset("Sneaky", ("sortBy", "title"), ("offset", "40"), ("wordId", "1234"), ("__proto__", "x"))],
                   };

        var saved = await Put(TestUsers.UserA, body);

        saved.Presets[0].Query.Should().Equal(new Dictionary<string, string> { ["sortBy"] = "title" });
    }

    [Fact]
    public async Task Put_DropsOverlongValuesAndTruncatesNames()
    {
        var body = new MediaFilterPresetsDto
                   {
                       Presets = [Preset(new string('n', 60), ("sortBy", "title"), ("title", new string('x', 501)))],
                   };

        var saved = await Put(TestUsers.UserA, body);

        saved.Presets[0].Name.Should().Be(new string('n', 40));
        saved.Presets[0].Query.Should().ContainKey("sortBy").And.NotContainKey("title");
    }

    [Fact]
    public async Task Put_DropsBlankAndDuplicateNames()
    {
        var body = new MediaFilterPresetsDto
                   {
                       Presets = [Preset("Reading", ("sortBy", "title")), Preset("  "), Preset("reading", ("sortBy", "difficulty"))],
                   };

        var saved = await Put(TestUsers.UserA, body);

        saved.Presets.Should().HaveCount(1);
        saved.Presets[0].Query["sortBy"].Should().Be("title");
    }

    [Fact]
    public async Task Put_ClearsADefaultPointingAtNothing()
    {
        var body = new MediaFilterPresetsDto { Presets = [Preset("Reading")], DefaultPreset = "Deleted preset" };

        (await Put(TestUsers.UserA, body)).DefaultPreset.Should().BeNull();
    }

    [Fact]
    public async Task Unauthenticated_IsRejected()
    {
        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Route))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var put = new HttpRequestMessage(HttpMethod.Put, Route).WithJsonContent(new MediaFilterPresetsDto());
        (await _client.SendAsync(put)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
