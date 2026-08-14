using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class ExampleSentenceImportTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.UserExampleSentences.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static object Item(int index, string text, int wordId = 1, byte readingIndex = 0, string? source = "Anki: Mining")
        => new { index, wordId, readingIndex, text, source };

    private Task<HttpResponseMessage> Import(params object[] items)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/user/example-sentences/import-batch")
                             .WithUser(TestUsers.UserA)
                             .WithJsonContent(new { items }));

    private async Task<(JsonElement Body, Dictionary<int, string> Statuses)> ImportOk(params object[] items)
    {
        var response = await Import(items);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var statuses = body.GetProperty("results")
                           .EnumerateArray()
                           .ToDictionary(r => r.GetProperty("index").GetInt32(),
                                         r => r.GetProperty("status").GetString()!);
        return (body, statuses);
    }

    private async Task<List<UserExampleSentence>> Saved()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.UserExampleSentences
                           .Where(e => e.UserId == TestUsers.UserA)
                           .OrderBy(e => e.WordId).ThenBy(e => e.SortOrder)
                           .ToListAsync();
    }

    private async Task<int> LimitPerWord()
    {
        var (body, _) = await ImportOk(Item(0, "これは**猫**です", wordId: 999_001));
        return body.GetProperty("limitPerWord").GetInt32();
    }

    [Fact]
    public async Task ImportBatch_StoresSentencesWithMarkersAndSource()
    {
        var (_, statuses) = await ImportOk(Item(0, "これは**猫**です"),
                                           Item(1, "あれは**犬**でした", wordId: 2));

        statuses.Values.Should().AllBe("ok");

        var saved = await Saved();
        saved.Should().HaveCount(2);
        saved[0].Text.Should().Be("これは**猫**です");
        saved[0].Source.Should().Be("Anki: Mining");
        saved[0].SortOrder.Should().Be(0);
    }

    /// <summary>Sentences attach to any resolvable word, unlike card media which requires a tracked form.</summary>
    [Fact]
    public async Task ImportBatch_ForAWordWithNoCard_Succeeds()
    {
        var (_, statuses) = await ImportOk(Item(0, "これは**猫**です", wordId: 987_654));

        statuses[0].Should().Be("ok");
        (await Saved()).Should().ContainSingle();
    }

    [Fact]
    public async Task ImportBatch_SentenceAlreadySavedWithADifferentHighlight_IsADuplicate()
    {
        await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/user/example-sentences/1/0/favourite")
                                .WithUser(TestUsers.UserA)
                                .WithJsonContent(new { text = "これは**猫**です", source = "Some deck" }));

        var (_, statuses) = await ImportOk(Item(0, "これは猫**で**す"));

        statuses[0].Should().Be("duplicate");
        (await Saved()).Should().ContainSingle();
    }

    [Fact]
    public async Task ImportBatch_SameSentenceTwiceInOneRequest_StoresItOnce()
    {
        var (_, statuses) = await ImportOk(Item(0, "これは**猫**です"), Item(1, "これは**猫**です"));

        statuses[0].Should().Be("ok");
        statuses[1].Should().Be("duplicate");
        (await Saved()).Should().ContainSingle();
    }

    [Fact]
    public async Task ImportBatch_PastThePerWordLimit_ReportsLimitReached()
    {
        var limit = await LimitPerWord();

        var items = Enumerable.Range(0, limit + 2)
                              .Select(i => Item(i, $"文{i}に**猫**がいる"))
                              .ToArray();

        var (_, statuses) = await ImportOk(items);

        statuses.Count(s => s.Value == "ok").Should().Be(limit);
        statuses.Count(s => s.Value == "limit_reached").Should().Be(2);
        (await Saved()).Count(s => s.WordId == 1).Should().Be(limit);
    }

    /// <summary>The limit is per form, so one full word must not starve another in the same request.</summary>
    [Fact]
    public async Task ImportBatch_CountsTheLimitPerWordNotPerRequest()
    {
        var limit = await LimitPerWord();

        var items = new List<object>();
        foreach (var wordId in new[] { 10, 20 })
            for (var i = 0; i < limit; i++)
                items.Add(Item(items.Count, $"文{i}に**猫**がいる", wordId: wordId));

        var (_, statuses) = await ImportOk(items.ToArray());

        statuses.Values.Should().AllBe("ok");
        var saved = await Saved();
        saved.Count(s => s.WordId == 10).Should().Be(limit);
        saved.Count(s => s.WordId == 20).Should().Be(limit);
    }

    [Fact]
    public async Task ImportBatch_TextWithoutAMarker_IsRejectedPerItem()
    {
        var (_, statuses) = await ImportOk(Item(0, "これは猫です"), Item(1, "あれは**犬**です"));

        statuses[0].Should().Be("no_marker");
        statuses[1].Should().Be("ok");
        (await Saved()).Should().ContainSingle();
    }

    [Fact]
    public async Task ImportBatch_TextOverTheColumnLength_ReportsTooLong()
    {
        var (_, statuses) = await ImportOk(Item(0, "**猫**" + new string('あ', 146)));

        statuses[0].Should().Be("too_long");
        (await Saved()).Should().BeEmpty();
    }

    /// <summary>Stored text is rendered through v-html, so markup must never reach the column.</summary>
    [Fact]
    public async Task ImportBatch_TextContainingMarkup_IsRejected()
    {
        var (_, statuses) = await ImportOk(Item(0, "これは<b>**猫**</b>です"));

        statuses[0].Should().Be("invalid");
        (await Saved()).Should().BeEmpty();
    }

    /// <summary>Deleting renumbers the remaining rows, so a naive count-based SortOrder would collide.</summary>
    [Fact]
    public async Task ImportBatch_AfterASentenceWasDeleted_DoesNotCollideOnSortOrder()
    {
        await ImportOk(Item(0, "一つ目の**猫**"), Item(1, "二つ目の**猫**"), Item(2, "三つ目の**猫**"));

        var first = (await Saved()).First();
        var delete = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"/api/user/example-sentences/{first.UserExampleSentenceId}")
                .WithUser(TestUsers.UserA));
        delete.StatusCode.Should().Be(HttpStatusCode.OK);

        var (_, statuses) = await ImportOk(Item(0, "四つ目の**猫**"));

        statuses[0].Should().Be("ok");
        var saved = await Saved();
        saved.Should().HaveCount(3);
        saved.Select(s => s.SortOrder).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ImportBatch_WithNoItems_ReturnsBadRequest()
    {
        var response = await Import();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ImportBatch_OverTheItemCap_ReturnsBadRequest()
    {
        var items = Enumerable.Range(0, 501).Select(i => Item(i, $"文{i}に**猫**がいる")).ToArray();

        var response = await Import(items);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Saved()).Should().BeEmpty();
    }
}

public class ResolveWordsTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // Resolution itself needs a populated JMDict, which the SQLite harness does not have; these cover the
    // request contract, which is all that can be exercised without the dictionary.
    private Task<HttpResponseMessage> Resolve(object body)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/resolve-words")
                             .WithUser(TestUsers.UserA)
                             .WithJsonContent(body));

    [Fact]
    public async Task ResolveWords_WithNoPairs_ReturnsBadRequest()
    {
        var response = await Resolve(new { pairs = Array.Empty<object>() });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResolveWords_OverTheDirectLookupCap_ReturnsBadRequest()
    {
        var pairs = Enumerable.Range(0, 2001).Select(i => new { word = $"語{i}", reading = "" }).ToArray();

        var response = await Resolve(new { pairs });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResolveWords_OverTheParsedCap_ReturnsBadRequest()
    {
        var pairs = Enumerable.Range(0, 501).Select(i => new { word = $"語{i}", reading = "" }).ToArray();

        var response = await Resolve(new { pairs, parseWords = true });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResolveWords_WithOnlyBlankWords_ResolvesNothing()
    {
        var response = await Resolve(new { pairs = new[] { new { word = "   ", reading = "" } } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("resolved").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ResolveWords_Unauthenticated_IsRejected()
    {
        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/resolve-words")
                .WithJsonContent(new { pairs = new[] { new { word = "猫", reading = "ねこ" } } }));

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
