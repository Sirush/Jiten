using FluentAssertions;
using Jiten.Api.Services.ReadingCheck;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class ReadingCheckDataTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private const int CharacterNameWord = 140_000;
    private const int SharedWord = 140_001;
    private const int DigitWord = 140_002;
    private const int CounterWord = 140_003;
    private const int ExpressionWord = 140_004;
    private const int NameEntry = SentenceComprehension.NameEntryFloor + 140_000;

    private int _seriesId;
    private List<int> _volumeIds = [];
    private int _otherNovelId;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        var series = new Deck { OriginalTitle = "Series", MediaType = MediaType.Novel };
        var otherNovel = new Deck { OriginalTitle = "Other novel", MediaType = MediaType.Novel };
        jitenDb.Decks.AddRange(series, otherNovel);
        await jitenDb.SaveChangesAsync();

        var volumes = Enumerable.Range(1, 3)
                                .Select(i => new Deck { OriginalTitle = $"Volume {i}", MediaType = MediaType.Novel, ParentDeckId = series.DeckId, DeckOrder = i })
                                .ToList();
        jitenDb.Decks.AddRange(volumes);
        await jitenDb.SaveChangesAsync();

        _seriesId = series.DeckId;
        _volumeIds = volumes.Select(v => v.DeckId).ToList();
        _otherNovelId = otherNovel.DeckId;

        if (!await jitenDb.WordForms.AnyAsync(wf => wf.WordId == CharacterNameWord))
        {
            AddWord(jitenDb, CharacterNameWord, "日南");
            AddWord(jitenDb, SharedWord, "教室");
            AddWord(jitenDb, DigitWord, "２通");
            AddWord(jitenDb, CounterWord, "一冊");
            AddWord(jitenDb, ExpressionWord, "目を閉じる", "exp");
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = NameEntry, PartsOfSpeech = ["name"] });
            jitenDb.WordForms.Add(new JmDictWordForm { WordId = NameEntry, ReadingIndex = 0, Text = "日南", RubyText = "日南[ひなみ]", FormType = JmDictFormType.KanjiForm });
        }

        foreach (var volume in volumes)
        {
            jitenDb.DeckWords.Add(new DeckWord { DeckId = volume.DeckId, WordId = CharacterNameWord, ReadingIndex = 0, Occurrences = 300 });
            jitenDb.DeckWords.Add(new DeckWord { DeckId = volume.DeckId, WordId = SharedWord, ReadingIndex = 0, Occurrences = 20 });
            foreach (var word in new[] { DigitWord, CounterWord, ExpressionWord })
                jitenDb.DeckWords.Add(new DeckWord { DeckId = volume.DeckId, WordId = word, ReadingIndex = 0, Occurrences = 20 });
        }

        jitenDb.DeckWords.Add(new DeckWord { DeckId = otherNovel.DeckId, WordId = CharacterNameWord, ReadingIndex = 0, Occurrences = 2 });
        jitenDb.DeckWords.Add(new DeckWord { DeckId = otherNovel.DeckId, WordId = SharedWord, ReadingIndex = 0, Occurrences = 20 });
        await jitenDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static void AddWord(JitenDbContext db, int wordId, string text, string pos = "n")
    {
        db.JMDictWords.Add(new JmDictWord { WordId = wordId, PartsOfSpeech = [pos] });
        db.Definitions.Add(new JmDictDefinition { WordId = wordId, SenseIndex = 0, EnglishMeanings = ["meaning"], PartsOfSpeech = [pos] });
        db.WordForms.Add(new JmDictWordForm { WordId = wordId, ReadingIndex = 0, Text = text, RubyText = text, FormType = JmDictFormType.KanjiForm });
    }

    [Fact]
    public async Task Candidates_NameSpreadOverOneSeriesVolumes_IsDropped_AndWorksCountSeriesOnce()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<IReadingCheckData>();

        var candidates = await data.GetCandidatesAsync(TestUsers.UserA, [.._volumeIds, _otherNovelId]);

        candidates.Should().NotContain(c => c.WordId == CharacterNameWord);
        candidates.Should().ContainSingle(c => c.WordId == SharedWord).Which.Works.Should().Be(2);
    }

    [Fact]
    public async Task Candidates_DropNumbersAndCounters_AndFlagExpressions()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<IReadingCheckData>();

        var candidates = await data.GetCandidatesAsync(TestUsers.UserA, _volumeIds);

        candidates.Should().NotContain(c => c.WordId == DigitWord || c.WordId == CounterWord);
        candidates.Should().ContainSingle(c => c.WordId == ExpressionWord).Which.Expression.Should().BeTrue();
        candidates.Should().ContainSingle(c => c.WordId == SharedWord).Which.Probeable.Should().BeTrue();
    }

    [Fact]
    public async Task Work_PickedBySeriesTitle_TicksEveryVolume()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<IReadingCheckData>();

        var work = await data.GetWorkAsync(TestUsers.UserA, _seriesId);

        work!.Units.Should().HaveCount(3).And.OnlyContain(u => u.Ticked);
    }

    [Fact]
    public async Task Work_PickedByOneVolume_TicksOnlyThatVolume()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<IReadingCheckData>();

        var work = await data.GetWorkAsync(TestUsers.UserA, _volumeIds[1]);

        work!.Units.Where(u => u.Ticked).Select(u => u.DeckId).Should().Equal(_volumeIds[1]);
    }
}
