using System.Net.Http.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>揺り籠から墓場まで has several spellings that all contain 墓場; Used in lists the word once.</summary>
public class UsedInFormGroupingTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int Hakaba = 94101;
    private const int Yurikago = 94102;
    private const int HakabaArashi = 94103;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        if (!await db.WordForms.AnyAsync(wf => wf.WordId == Hakaba))
        {
            AddWord(db, Hakaba, Kanji(0, "墓場"), Kana(1, "はかば"));
            AddWord(db, Yurikago,
                    Kanji(0, "揺り籠から墓場まで"),
                    Kanji(1, "揺籃から墓場まで", "rK"),
                    Kanji(2, "ゆりかごから墓場まで"),
                    Kana(3, "ゆりかごからはかばまで"));
            AddWord(db, HakabaArashi, Kanji(0, "墓塲荒らし"), Kanji(1, "墓場荒らし"), Kana(2, "はかばあらし"));

            db.WordFormFrequencies.AddRange(
                Rank(Yurikago, 0, 200), Rank(Yurikago, 1, 900), Rank(Yurikago, 2, 100),
                Rank(HakabaArashi, 0, 0), Rank(HakabaArashi, 1, 5000));
            await db.SaveChangesAsync();
        }

        await db.WordCompositions.Where(c => c.ComponentWordId == Hakaba).ExecuteDeleteAsync();
        db.WordCompositions.AddRange(
            Slot(Yurikago, 0, 0, "墓場"), Slot(Yurikago, 1, 0, "墓場"), Slot(Yurikago, 2, 0, "墓場"),
            Slot(Yurikago, 3, 1, "はかば"),
            Slot(HakabaArashi, 0, 0, "墓塲"), Slot(HakabaArashi, 1, 0, "墓場"), Slot(HakabaArashi, 2, 1, "はかば"));
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static JmDictWordForm Kanji(short index, string text, params string[] tags)
        => new()
        {
            ReadingIndex = index, Text = text, RubyText = text, FormType = JmDictFormType.KanjiForm,
            InfoTags = tags.Length > 0 ? [.. tags] : null
        };

    private static JmDictWordForm Kana(short index, string text)
        => new() { ReadingIndex = index, Text = text, RubyText = text, FormType = JmDictFormType.KanaForm };

    private static void AddWord(JitenDbContext db, int wordId, params JmDictWordForm[] forms)
    {
        db.JMDictWords.Add(new JmDictWord { WordId = wordId, PartsOfSpeech = ["n"] });
        foreach (var form in forms)
        {
            form.WordId = wordId;
            db.WordForms.Add(form);
        }
    }

    private static JmDictWordFormFrequency Rank(int wordId, short index, int rank)
        => new() { WordId = wordId, ReadingIndex = index, FrequencyRank = rank };

    private static JmDictWordComposition Slot(int wordId, short readingIndex, short componentReadingIndex, string surface)
        => new()
        {
            WordId = wordId, ReadingIndex = readingIndex, Position = 0,
            ComponentWordId = Hakaba, ComponentReadingIndex = componentReadingIndex, ComponentSurface = surface
        };

    private async Task<UsedInPageDto> UsedIn(short readingIndex)
        => (await _client.GetFromJsonAsync<UsedInPageDto>($"/api/vocabulary/{Hakaba}/{readingIndex}/used-in"))!;

    [Fact]
    public async Task ListsEachWordOnce_AsItsHeadword_RankedByItsBestForm()
    {
        var page = await UsedIn(0);

        page.Total.Should().Be(2);
        page.Items.Select(i => (i.WordId, i.ReadingIndex, i.FrequencyRank)).Should().Equal(
            (Yurikago, (byte)0, (int?)100), (HakabaArashi, (byte)1, (int?)5000));
    }

    [Fact]
    public async Task SkipsAHeadwordThatSpellsTheComponentDifferently()
    {
        var page = await UsedIn(0);

        var arashi = page.Items.Single(i => i.WordId == HakabaArashi);
        arashi.Reading.Should().Be("墓場荒らし");
        arashi.MatchSurface.Should().Be("墓場");
    }

    [Fact]
    public async Task OnTheKanaPage_ShowsTheKanaSpelling()
    {
        var page = await UsedIn(1);

        page.Items.Select(i => i.Reading).Should().Equal("ゆりかごからはかばまで", "はかばあらし");
    }
}
