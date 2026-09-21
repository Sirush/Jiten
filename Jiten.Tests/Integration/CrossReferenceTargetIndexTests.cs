using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>其処 (930) with a "see also" chip naming あそこ, which is reading index 2 of 彼処 (931).</summary>
public class CrossReferenceTargetIndexTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int Soko = 930;
    private const int Asoko = 931;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        await db.JmDictCrossReferences.ExecuteDeleteAsync();

        if (!await db.WordForms.AnyAsync(wf => wf.WordId == Soko))
        {
            db.JMDictWords.Add(new JmDictWord { WordId = Soko, PartsOfSpeech = ["pn"] });
            db.WordForms.Add(Form(Soko, 0, "其処", JmDictFormType.KanjiForm));
            db.WordForms.Add(Form(Soko, 1, "そこ", JmDictFormType.KanaForm));
            db.Definitions.Add(new JmDictDefinition { WordId = Soko, SenseIndex = 0, EnglishMeanings = ["there"], PartsOfSpeech = ["pn"] });

            db.JMDictWords.Add(new JmDictWord { WordId = Asoko, PartsOfSpeech = ["pn"] });
            db.WordForms.Add(Form(Asoko, 0, "彼処", JmDictFormType.KanjiForm));
            db.WordForms.Add(Form(Asoko, 1, "彼所", JmDictFormType.KanjiForm));
            db.WordForms.Add(Form(Asoko, 2, "あそこ", JmDictFormType.KanaForm));
            db.Definitions.Add(new JmDictDefinition { WordId = Asoko, SenseIndex = 0, EnglishMeanings = ["over there"], PartsOfSpeech = ["pn"] });
        }

        db.JmDictCrossReferences.AddRange(
            new JmDictCrossReference
            {
                FromWordId = Soko, FromSenseIndex = 0, Type = CrossReferenceType.SeeAlso, TargetWordId = Asoko,
                TargetReading = "あそこ", RawText = "あそこ"
            },
            new JmDictCrossReference
            {
                FromWordId = Soko, FromSenseIndex = 0, Type = CrossReferenceType.SeeAlso, TargetWordId = Asoko,
                TargetKanji = "彼所", TargetReading = "あそこ", RawText = "彼所・あそこ"
            },
            new JmDictCrossReference
            {
                FromWordId = Soko, FromSenseIndex = 0, Type = CrossReferenceType.SeeAlso, TargetWordId = Asoko,
                TargetReading = "どこにもない", RawText = "どこにもない"
            });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static JmDictWordForm Form(int wordId, short index, string text, JmDictFormType type) =>
        new() { WordId = wordId, ReadingIndex = index, Text = text, RubyText = text, FormType = type };

    [Theory]
    [InlineData("")]
    [InlineData("/info")]
    public async Task ChipsCarryTheNamedFormsReadingIndex(string suffix)
    {
        var res = await _client.GetAsync($"/api/vocabulary/{Soko}/0{suffix}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        var xrefs = body.GetProperty("definitions")[0].GetProperty("crossReferences").EnumerateArray()
                        .ToDictionary(x => x.GetProperty("targetText").GetString()!,
                                      x => x.GetProperty("targetReadingIndex").GetInt32());

        xrefs["あそこ"].Should().Be(2);
        xrefs["彼所・あそこ"].Should().Be(1, "the kanji form wins when both xk and xr are set");
        xrefs["どこにもない"].Should().Be(0, "an unmatched text keeps the index 0 fallback");
    }

    [Fact]
    public void Resolver_PrefersActiveNonSearchOnlyForms_AndFallsBackToZero()
    {
        var forms = new Dictionary<(int, short), JmDictWordForm>
        {
            [(1, 0)] = new() { WordId = 1, ReadingIndex = 0, Text = "甲" },
            [(1, 1)] = new() { WordId = 1, ReadingIndex = 1, Text = "こう", IsSearchOnly = true },
            [(1, 2)] = new() { WordId = 1, ReadingIndex = 2, Text = "こう" },
            [(2, 3)] = new() { WordId = 2, ReadingIndex = 3, Text = "こう" }
        };

        ApiExtensions.ResolveTargetReadingIndex(new JmDictCrossReference { TargetWordId = 1, TargetReading = "こう" }, forms)
                     .Should().Be(2);
        ApiExtensions.ResolveTargetReadingIndex(new JmDictCrossReference { TargetWordId = 1, TargetKanji = "甲", TargetReading = "こう" }, forms)
                     .Should().Be(0);
        ApiExtensions.ResolveTargetReadingIndex(new JmDictCrossReference { TargetWordId = 1 }, forms).Should().Be(0);
        ApiExtensions.ResolveTargetReadingIndex(new JmDictCrossReference { TargetWordId = 1, TargetReading = "こう" }, null)
                     .Should().Be(0);
        ApiExtensions.ResolveTargetReadingIndex(new JmDictCrossReference { TargetWordId = null, TargetReading = "こう" }, forms)
                     .Should().Be(0);
    }
}
