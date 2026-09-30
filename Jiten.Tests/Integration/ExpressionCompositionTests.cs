using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>異彩を放つ = 異彩 + を + 放つ, with を stored as a grammatical component.</summary>
public class ExpressionCompositionTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int Isai = 94001;
    private const int Wo = 94002;
    private const int Hanatsu = 94003;
    private const int IsaiWoHanatsu = 94004;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsCards.ExecuteDeleteAsync();

        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        if (!await jitenDb.WordForms.AnyAsync(wf => wf.WordId == IsaiWoHanatsu))
        {
            AddWord(jitenDb, Isai, ["n"], "異彩", "いさい");
            AddWord(jitenDb, Wo, ["prt"], null, "を");
            AddWord(jitenDb, Hanatsu, ["v5t"], "放つ", "はなつ");
            AddWord(jitenDb, IsaiWoHanatsu, ["exp", "v5t"], "異彩を放つ", "いさいをはなつ");
            await jitenDb.SaveChangesAsync();
        }

        await jitenDb.WordCompositions.Where(c => c.WordId == IsaiWoHanatsu).ExecuteDeleteAsync();
        jitenDb.WordCompositions.AddRange(
            Component(0, Isai, "異彩", false),
            Component(1, Wo, "を", true),
            Component(2, Hanatsu, "放つ", false));
        await jitenDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static void AddWord(JitenDbContext db, int wordId, List<string> pos, string? kanji, string kana)
    {
        db.JMDictWords.Add(new JmDictWord { WordId = wordId, PartsOfSpeech = pos });
        short index = 0;
        if (kanji != null)
            db.WordForms.Add(new JmDictWordForm
            {
                WordId = wordId, ReadingIndex = index++, Text = kanji, RubyText = kanji, FormType = JmDictFormType.KanjiForm
            });
        db.WordForms.Add(new JmDictWordForm
        {
            WordId = wordId, ReadingIndex = index, Text = kana, RubyText = kana, FormType = JmDictFormType.KanaForm
        });
    }

    private static JmDictWordComposition Component(short position, int componentWordId, string surface, bool grammatical)
        => new()
        {
            WordId = IsaiWoHanatsu, ReadingIndex = 0, Position = position,
            ComponentWordId = componentWordId, ComponentReadingIndex = 0, ComponentSurface = surface,
            IsGrammatical = grammatical
        };

    private async Task MasterWords(params int[] wordIds)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        foreach (var wordId in wordIds)
            userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, wordId, 0, state: FsrsState.Mastered));
        await userDb.SaveChangesAsync();
    }

    private async Task<List<int>> PreviewInference(string direction)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/srs/composition-inference/preview")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new { direction });
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("wordId").GetInt32()).ToList();
    }

    [Fact]
    public async Task ComposedOf_ListsTheParticleInOrder_FlaggedAndWithItsSurface()
    {
        var word = await _client.GetFromJsonAsync<WordDto>($"/api/vocabulary/{IsaiWoHanatsu}/0/info");

        word!.ComposedOf.Should().NotBeNull();
        word.ComposedOf!.Select(c => (c.WordId, c.Reading, c.IsGrammatical)).Should().Equal(
            (Isai, "異彩", false), (Wo, "を", true), (Hanatsu, "放つ", false));
    }

    [Fact]
    public async Task UsedIn_ListsTheExpressionOnContentWords_ButNotOnTheParticle()
    {
        var onIsai = await _client.GetFromJsonAsync<UsedInPageDto>($"/api/vocabulary/{Isai}/0/used-in");
        var onWo = await _client.GetFromJsonAsync<UsedInPageDto>($"/api/vocabulary/{Wo}/0/used-in");

        onIsai!.Items.Should().ContainSingle().Which.WordId.Should().Be(IsaiWoHanatsu);
        onWo!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task KnowingTheExpression_InfersItsContentWords_NotTheParticle()
    {
        await MasterWords(IsaiWoHanatsu);

        var inferred = await PreviewInference("compound-to-components");

        inferred.Should().BeEquivalentTo([Isai, Hanatsu]);
    }

    [Fact]
    public async Task KnowingTheContentWords_InfersTheExpression_WithoutNeedingTheParticle()
    {
        await MasterWords(Isai, Hanatsu);

        var inferred = await PreviewInference("components-to-compound");

        inferred.Should().Equal(IsaiWoHanatsu);
    }
}
