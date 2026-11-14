using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class WordSetRuleTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int Tokyo = 5000001;
    private const int LondonName = 5000002;
    private const int Suzuki = 5000006;
    private const int LondonVocabulary = 1003;
    private const int Terebi = 1004;
    private const int Baka = 1005;

    private int _setId;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        await db.WordSets.ExecuteDeleteAsync();
        await db.WordForms.ExecuteDeleteAsync();
        await db.JMDictWords.ExecuteDeleteAsync();
        await scope.ServiceProvider.GetRequiredService<UserDbContext>().UserWordSetStates.ExecuteDeleteAsync();

        AddWord(db, Tokyo, ["place"], ("東京", JmDictFormType.KanjiForm), ("とうきょう", JmDictFormType.KanaForm));
        AddWord(db, LondonName, ["place"], ("ロンドン", JmDictFormType.KanaForm));
        AddWord(db, Suzuki, ["surname"], ("鈴木", JmDictFormType.KanjiForm), ("すずき", JmDictFormType.KanaForm));
        AddWord(db, LondonVocabulary, ["n"], ("ロンドン", JmDictFormType.KanaForm));
        AddWord(db, Terebi, ["n", "abbr"], ("テレビ", JmDictFormType.KanaForm));
        AddWord(db, Baka, ["n"], ("馬鹿", JmDictFormType.KanjiForm), ("ばか", JmDictFormType.KanaForm), ("バカ", JmDictFormType.KanaForm));

        var set = new WordSet { Name = "Places", Slug = "places" };
        db.WordSets.Add(set);
        await db.SaveChangesAsync();
        _setId = set.SetId;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static void AddWord(JitenDbContext db, int wordId, List<string> pos, params (string Text, JmDictFormType Type)[] forms)
    {
        db.JMDictWords.Add(new JmDictWord { WordId = wordId, PartsOfSpeech = pos });
        for (short i = 0; i < forms.Length; i++)
        {
            db.WordForms.Add(new JmDictWordForm
            {
                WordId = wordId, ReadingIndex = i, Text = forms[i].Text, RubyText = forms[i].Text, FormType = forms[i].Type
            });
        }
    }

    private static object PlaceRule => new { includePos = new[] { "place" }, addKanaForm = true };

    private async Task<JsonElement> Post(string path, object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/admin/word-sets/{_setId}/{path}")
                                               .WithAdmin().WithJsonContent(body));
        response.StatusCode.Should().Be(expected);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<JsonElement> Apply(object rule, int unmatched = 0) => Post("rule/apply", new { rule, unmatched });

    private async Task<List<WordSetMember>> Members()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        return await db.WordSetMembers.AsNoTracking().Where(m => m.SetId == _setId).OrderBy(m => m.Position).ToListAsync();
    }

    private static (int, short)[] Keys(IEnumerable<WordSetMember> members) => members.Select(m => (m.WordId, m.ReadingIndex)).ToArray();

    [Fact]
    public async Task Preview_ReportsTheDiff_AndWritesNothing()
    {
        var preview = await Post("rule/preview", new { rule = PlaceRule });

        preview.GetProperty("toAddCount").GetInt32().Should().Be(3);
        preview.GetProperty("toAdd").EnumerateArray().Select(m => m.GetProperty("text").GetString())
               .Should().BeEquivalentTo("東京", "とうきょう", "ロンドン");
        (await Members()).Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_AddsKanjiFormsWithTheirKanaForm_AndIsIdempotent()
    {
        var first = await Apply(PlaceRule);

        first.GetProperty("added").GetInt32().Should().Be(3);
        first.GetProperty("wordCount").GetInt32().Should().Be(2);
        var members = await Members();
        Keys(members).Should().BeEquivalentTo([(Tokyo, (short)0), (Tokyo, (short)1), (LondonName, (short)0)]);

        var second = await Apply(PlaceRule);

        second.GetProperty("added").GetInt32().Should().Be(0);
        (await Members()).Select(m => m.Position).Should().Equal(members.Select(m => m.Position));
    }

    [Fact]
    public async Task ManualMembers_SurviveARegenerationThatRemovesUnmatchedOnes()
    {
        await Apply(new { includePos = new[] { "place", "surname" } });
        await Post("members", new { members = new[] { new { wordId = LondonVocabulary, readingIndex = 0 } } });

        var result = await Apply(PlaceRule, unmatched: 2);

        result.GetProperty("removed").GetInt32().Should().Be(2);
        var members = await Members();
        members.Should().NotContain(m => m.WordId == Suzuki);
        members.Should().ContainSingle(m => m.WordId == LondonVocabulary).Which.IsManual.Should().BeTrue();
    }

    [Fact]
    public async Task UnmatchedMembers_CanBeKeptAsManual()
    {
        await Apply(new { includePos = new[] { "place", "surname" } });

        var result = await Apply(PlaceRule, unmatched: 1);

        result.GetProperty("markedManual").GetInt32().Should().Be(2);
        (await Members()).Where(m => m.IsManual).Select(m => m.WordId).Distinct().Should().Equal(Suzuki);
    }

    [Fact]
    public async Task RemovedMember_StaysOut_UntilItIsAddedBack()
    {
        await Apply(PlaceRule);
        var london = new { members = new[] { new { wordId = LondonName, readingIndex = 0 } } };
        await Post("members/remove", london);

        (await Apply(PlaceRule)).GetProperty("added").GetInt32().Should().Be(0);
        (await Members()).Should().NotContain(m => m.WordId == LondonName);

        await Post("members", london);
        var preview = await Post("rule/preview", new { rule = PlaceRule });

        preview.GetProperty("excluded").GetInt32().Should().Be(0);
        (await Members()).Should().Contain(m => m.WordId == LondonName);
    }

    [Fact]
    public async Task KatakanaWordRule_TakesOnlyWordsWrittenEntirelyInKatakana()
    {
        await Apply(new { script = 1, dictionary = 1 });

        (await Members()).Select(m => m.WordId).Should().BeEquivalentTo([LondonVocabulary, Terebi]);
    }

    [Fact]
    public async Task KatakanaFormRule_TakesKatakanaFormsOfAnyWord()
    {
        await Apply(new { script = 2, dictionary = 1 });

        Keys(await Members()).Should().BeEquivalentTo([(LondonVocabulary, (short)0), (Terebi, (short)0), (Baka, (short)2)]);
    }

    [Fact]
    public async Task Preview_FlagsPartOfSpeechTagsNoWordCarries()
    {
        var preview = await Post("rule/preview", new { rule = new { includePos = new[] { "place", "plase" } } });

        preview.GetProperty("unknownPos").EnumerateArray().Select(p => p.GetString()).Should().Equal("plase");
    }

    [Fact]
    public async Task RuleWithoutCriteria_IsRejected()
    {
        await Post("rule/apply", new { rule = new { excludePos = new[] { "n" } }, unmatched = 0 }, HttpStatusCode.BadRequest);

        (await Members()).Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_MarksSubscriberCoverageDirty_AndStoresTheRule()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            await userDb.UserMetadatas.ExecuteDeleteAsync();
            userDb.UserWordSetStates.Add(new UserWordSetState
            {
                UserId = TestUsers.UserA, SetId = _setId, State = WordSetStateType.Mastered
            });
            await userDb.SaveChangesAsync();
        }

        await Apply(PlaceRule);

        using var check = factory.Services.CreateScope();
        (await check.ServiceProvider.GetRequiredService<UserDbContext>().UserMetadatas
                    .AnyAsync(m => m.UserId == TestUsers.UserA && m.CoverageDirty)).Should().BeTrue();

        var stored = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/admin/word-sets/{_setId}/rule").WithAdmin());
        var body = await stored.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("rule").GetProperty("includePos").EnumerateArray().Select(p => p.GetString()).Should().Equal("place");
        body.GetProperty("regeneratedAt").ValueKind.Should().Be(JsonValueKind.String);
    }
}
