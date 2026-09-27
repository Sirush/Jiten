using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class DisplayProfilesTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private const string Route = "/api/user/settings/display-profiles";

    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserSettings.RemoveRange(userDb.UserSettings);
        await userDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static Dictionary<string, JsonElement> Values(object values) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;

    private static DisplayProfileUpsertRequest Body(string name, object? values = null, List<List<string>>? statColumns = null) =>
        new() { Name = name, Values = values == null ? null : Values(values), StatColumns = statColumns };

    private static List<List<string>> Columns(params List<string>[] columns) => [..columns];

    private async Task<HttpResponseMessage> Put(string userId, string id, DisplayProfileUpsertRequest body) =>
        await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"{Route}/{id}").WithUser(userId).WithJsonContent(body));

    private async Task<DisplayProfileDto> PutOk(string userId, string id, DisplayProfileUpsertRequest body)
    {
        var response = await Put(userId, id, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<DisplayProfileDto>())!;
    }

    private async Task<DisplayProfilesDto> Get(string userId)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Route).WithUser(userId));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<DisplayProfilesDto>())!;
    }

    private async Task<HttpResponseMessage> Delete(string userId, string id) =>
        await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{Route}/{id}").WithUser(userId));

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Route))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var put = new HttpRequestMessage(HttpMethod.Put, $"{Route}/desk").WithJsonContent(Body("Desk"));
        (await _client.SendAsync(put)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{Route}/desk"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_WithNothingSaved_ReturnsEmptyWithCap()
    {
        var dto = await Get(TestUsers.UserA);

        dto.Profiles.Should().BeEmpty();
        dto.MaxProfiles.Should().Be(10);
    }

    [Fact]
    public async Task Put_ThenGet_RoundTripsValuesAndStats()
    {
        var colours = new { @new = "#f43f5e", young = "#f59e0b", due = (string?)null, mature = (string?)null, redundant = "#0ea5e9", ignored = "#9ca3af" };
        await PutOk(TestUsers.UserA, "desk", Body("Desk",
            new { titleLanguage = 2, headwordFurigana = "unknown", headwordSize = "lg", readingSpeed = 12000, hideTags = true, stateColours = colours },
            [["difficulty"], [], ["wordCount"]]));

        var stored = (await Get(TestUsers.UserA)).Profiles.Should().ContainSingle().Subject;

        stored.Id.Should().Be("desk");
        stored.Name.Should().Be("Desk");
        stored.Values["titleLanguage"].GetInt32().Should().Be(2);
        stored.Values["headwordFurigana"].GetString().Should().Be("unknown");
        stored.Values["headwordSize"].GetString().Should().Be("lg");
        stored.Values["readingSpeed"].GetInt32().Should().Be(12000);
        stored.Values["hideTags"].GetBoolean().Should().BeTrue();
        stored.Values["stateColours"].GetProperty("new").GetString().Should().Be("#f43f5e");
        stored.Values["stateColours"].GetProperty("due").ValueKind.Should().Be(JsonValueKind.Null);
        stored.StatColumns.Should().BeEquivalentTo(Columns(["difficulty"], [], ["wordCount"]), o => o.WithStrictOrdering());
        stored.UpdatedAt.Should().BePositive();
    }

    [Fact]
    public async Task Put_KeepsFuriganaPitchPaletteAndPerDifficultySpeeds()
    {
        var saved = await PutOk(TestUsers.UserA, "desk", Body("Desk", new
        {
            furiganaSize = "xl",
            furiganaOnHover = true,
            pitchAccentDisplay = "both",
            pitchAccentColours = true,
            difficultyPalette = "redGreen",
            readingSpeedByDifficulty = true,
            readingSpeeds = new[] { 20000, 17000, 14000, 11000, 9000, 7000 },
        }));

        saved.Values.Keys.Should().BeEquivalentTo("furiganaSize", "furiganaOnHover", "pitchAccentDisplay", "pitchAccentColours", "difficultyPalette",
                                                  "readingSpeedByDifficulty", "readingSpeeds");
        saved.Values["readingSpeeds"].GetArrayLength().Should().Be(6);
    }

    [Theory]
    [InlineData(new[] { 20000, 17000, 14000 })]
    [InlineData(new[] { 20000, 17000, 14000, 11000, 9000, 50 })]
    public async Task Put_DropsMalformedPerDifficultySpeeds(int[] speeds)
    {
        var saved = await PutOk(TestUsers.UserA, "desk", Body("Desk", new { readingSpeeds = speeds, difficultyPalette = "rainbow" }));

        saved.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task Put_DropsUnknownKeysAndInvalidValues()
    {
        var saved = await PutOk(TestUsers.UserA, "desk", Body("Desk", new
        {
            titleLanguage = 7,
            headwordFurigana = "sometimes",
            readingSpeed = 5,
            hideGenres = "yes",
            stateColours = new { @new = "red" },
            evil = "<script>",
            japaneseFont = "kyokasho",
        }));

        saved.Values.Keys.Should().Equal("japaneseFont");
        saved.Values["japaneseFont"].GetString().Should().Be("kyokasho");
    }

    [Theory]
    [InlineData("Meiryo", true)]
    [InlineData("Hiragino Sans W3", true)]
    [InlineData("游ゴシック", true)]
    [InlineData("", true)]
    [InlineData("Evil\"; } body { display: none", false)]
    [InlineData(@"back\slash", false)]
    public async Task Put_KeepsOnlyCssSafeCustomFontNames(string name, bool kept)
    {
        var saved = await PutOk(TestUsers.UserA, "desk", Body("Desk", new { japaneseFont = "custom", japaneseCustomFont = name }));

        saved.Values["japaneseFont"].GetString().Should().Be("custom");
        saved.Values.ContainsKey("japaneseCustomFont").Should().Be(kept);
    }

    [Fact]
    public async Task Put_SanitizesStatColumns_KeepsEmptyColumns()
    {
        var saved = await PutOk(TestUsers.UserA, "desk",
            Body("Desk", statColumns: [["wordCount", "bogus"], ["wordCount", "difficulty"], [], ["dialogue"]]));
        saved.StatColumns.Should().BeEquivalentTo(Columns(["wordCount"], ["difficulty"], []), o => o.WithStrictOrdering());

        var hiddenAll = await PutOk(TestUsers.UserA, "desk", Body("Desk", statColumns: []));
        hiddenAll.StatColumns.Should().HaveCount(3).And.OnlyContain(column => column.Count == 0);

        var builtIn = await PutOk(TestUsers.UserA, "desk", Body("Desk"));
        builtIn.StatColumns.Should().BeNull();
    }

    [Fact]
    public async Task Put_KeepsOnlyCompleteSectionLayouts()
    {
        var saved = await PutOk(TestUsers.UserA, "desk",
            Body("Desk", new { mediaCardSectionLayout = new { top = new[] { "tags", "description" }, bottom = new[] { "relations", "genres" } } }));
        var layout = saved.Values["mediaCardSectionLayout"];
        layout.GetProperty("top").EnumerateArray().Select(e => e.GetString()).Should().Equal("tags", "description");
        layout.GetProperty("bottom").EnumerateArray().Select(e => e.GetString()).Should().Equal("relations", "genres");

        var missing = await PutOk(TestUsers.UserA, "desk",
            Body("Desk", new { mediaCardSectionLayout = new { top = new[] { "tags" }, bottom = new[] { "description" } } }));
        missing.Values.Should().NotContainKey("mediaCardSectionLayout");

        var repeated = await PutOk(TestUsers.UserA, "desk",
            Body("Desk", new { mediaCardSectionLayout = new { top = new[] { "tags", "tags" }, bottom = new[] { "description", "genres" } } }));
        repeated.Values.Should().NotContainKey("mediaCardSectionLayout");
    }

    [Fact]
    public async Task Put_TruncatesLongNames_RejectsEmptyNamesAndBadIds()
    {
        var saved = await PutOk(TestUsers.UserA, "desk", Body(new string('a', 60)));
        saved.Name.Should().HaveLength(40);

        (await Put(TestUsers.UserA, "desk", Body("   "))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Put(TestUsers.UserA, "bad id!", Body("Desk"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_RefusesEleventhProfile_ButStillUpdatesExisting()
    {
        for (var i = 0; i < 10; i++)
            await PutOk(TestUsers.UserA, $"p{i}", Body($"Profile {i}"));

        (await Put(TestUsers.UserA, "p10", Body("One too many"))).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var renamed = await PutOk(TestUsers.UserA, "p3", Body("Renamed"));
        renamed.Name.Should().Be("Renamed");
        (await Get(TestUsers.UserA)).Profiles.Should().HaveCount(10);
    }

    [Fact]
    public async Task Put_OneProfile_LeavesOthersUntouched()
    {
        var phone = await PutOk(TestUsers.UserA, "phone", Body("Phone", new { headwordSize = "xl" }, [["wordCount"], [], []]));
        await PutOk(TestUsers.UserA, "desk", Body("Desk", new { headwordSize = "sm" }));

        await PutOk(TestUsers.UserA, "desk", Body("Desk", new { headwordSize = "md" }));

        var profiles = (await Get(TestUsers.UserA)).Profiles;
        profiles.Select(p => p.Id).Should().Equal("phone", "desk");
        var storedPhone = profiles[0];
        storedPhone.Values["headwordSize"].GetString().Should().Be("xl");
        storedPhone.StatColumns![0].Should().Equal("wordCount");
        storedPhone.UpdatedAt.Should().Be(phone.UpdatedAt);
        profiles[1].Values["headwordSize"].GetString().Should().Be("md");
    }

    [Fact]
    public async Task Delete_RemovesProfile_RefusesLastOne_And404sMissing()
    {
        await PutOk(TestUsers.UserA, "desk", Body("Desk"));
        await PutOk(TestUsers.UserA, "phone", Body("Phone"));

        (await Delete(TestUsers.UserA, "phone")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Delete(TestUsers.UserA, "phone")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Delete(TestUsers.UserA, "desk")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await Get(TestUsers.UserA)).Profiles.Select(p => p.Id).Should().Equal("desk");
    }

    [Fact]
    public async Task Profiles_AreScopedToTheirOwner()
    {
        await PutOk(TestUsers.UserA, "desk", Body("Desk"));

        (await Get(TestUsers.UserB)).Profiles.Should().BeEmpty();
        (await Delete(TestUsers.UserB, "desk")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
