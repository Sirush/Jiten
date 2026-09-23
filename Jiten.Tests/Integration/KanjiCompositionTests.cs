using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

// Each test reads distinct characters: GET /api/kanji responses are response-cached across tests.
public class KanjiCompositionTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static Kanji Kanji(string character, string meaning, int? rank = null) =>
        new() { Character = character, Meanings = [meaning], StrokeCount = 4, FrequencyRank = rank };

    private static KanjiComponent Node(string kanji, short index, short? parent, string component, string? original = null,
                                       bool radical = false, bool phonetic = false) =>
        new()
        {
            KanjiCharacter = kanji, NodeIndex = index, ParentIndex = parent, Component = component, Original = original,
            IsRadical = radical, IsPhonetic = phonetic
        };

    private async Task Seed(IEnumerable<Kanji> kanji, IEnumerable<KanjiComponent> components, IEnumerable<KanjiStrokes>? strokes = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        db.Kanjis.AddRange(kanji);
        db.KanjiComponents.AddRange(components);
        if (strokes != null)
            db.KanjiStrokes.AddRange(strokes);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetKanji_ReturnsTopLevelComponentsWithLinksAndStrokes()
    {
        await Seed([Kanji("時", "time", 50), Kanji("日", "day", 10), Kanji("寺", "temple", 900), Kanji("土", "soil")],
                   [
                       Node("時", 0, null, "日", radical: true), Node("時", 1, null, "寺", phonetic: true),
                       Node("時", 2, 1, "土"), Node("時", 3, 1, "寸")
                   ],
                   [new KanjiStrokes { Character = "時", Paths = ["M1,1", "M2,2"], NumberPositions = [1, 2, 3, 4] }]);

        var response = await _client.GetAsync("/api/kanji/時");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var components = body.GetProperty("components").EnumerateArray().ToList();
        components.Select(c => c.GetProperty("character").GetString()).Should().Equal("日", "寺");
        components[0].GetProperty("linkCharacter").GetString().Should().Be("日");
        components[0].GetProperty("meaning").GetString().Should().Be("day");
        components[0].GetProperty("isRadical").GetBoolean().Should().BeTrue();
        components[1].GetProperty("isPhonetic").GetBoolean().Should().BeTrue();

        var strokes = body.GetProperty("strokes");
        strokes.GetProperty("paths").GetArrayLength().Should().Be(2);
        strokes.GetProperty("numberPositions").GetArrayLength().Should().Be(4);
    }

    [Fact]
    public async Task GetKanji_VariantComponentLinksToOriginal_AndUsedInMatchesAnyDepthAndVariants()
    {
        await Seed([Kanji("人", "person", 5), Kanji("休", "rest", 300), Kanji("体", "body", 100), Kanji("木", "tree", 20), Kanji("葉", "leaf", 800)],
                   [
                       Node("休", 0, null, "亻", original: "人", radical: true), Node("休", 1, null, "木"),
                       Node("体", 0, null, "亻", original: "人", radical: true), Node("体", 1, null, "本"),
                       Node("葉", 0, null, "艹", original: "艸", radical: true), Node("葉", 1, null, "枼", phonetic: true), Node("葉", 2, 1, "木")
                   ]);

        var restBody = await _client.GetFromJsonAsync<JsonElement>("/api/kanji/休");
        var person = restBody.GetProperty("components")[0];
        person.GetProperty("character").GetString().Should().Be("亻");
        person.GetProperty("original").GetString().Should().Be("人");
        person.GetProperty("linkCharacter").GetString().Should().Be("人");
        person.GetProperty("meaning").GetString().Should().Be("person");
        restBody.GetProperty("strokes").ValueKind.Should().Be(JsonValueKind.Null);

        var personBody = await _client.GetFromJsonAsync<JsonElement>("/api/kanji/人");
        personBody.GetProperty("usedInTotal").GetInt32().Should().Be(2);
        personBody.GetProperty("usedIn").EnumerateArray().Select(k => k.GetProperty("character").GetString()).Should().Equal("体", "休");
        personBody.GetProperty("components").GetArrayLength().Should().Be(0);

        var treeUsedIn = await _client.GetFromJsonAsync<JsonElement>("/api/kanji/木/used-in");
        treeUsedIn.EnumerateArray().Select(k => k.GetProperty("character").GetString()).Should().Equal("休", "葉");
    }

    [Fact]
    public async Task GetKanjiUsedIn_UnknownKanji_Returns404()
    {
        var response = await _client.GetAsync("/api/kanji/龘/used-in");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
