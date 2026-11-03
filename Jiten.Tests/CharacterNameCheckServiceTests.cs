using FluentAssertions;
using Jiten.Api.Services.MetadataEdits;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Jiten.Tests;

public class CharacterNameCheckServiceTests : IDisposable
{
    private const int ParentDeckId = 1;
    private const int ChildDeckId = 2;
    private const int SenseiWordId = 1000;
    private const int MaeharaNameWordId = 1001;

    private const string ChildText = "前原圭一は先生と話した。\n「圭一くん！」とレナが呼んだ。\nはるが来た。\n前原圭一、前原圭一。\n正君は黙っていた。";

    private readonly SqliteConnection _connection;
    private readonly Factory _factory;

    public CharacterNameCheckServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = new Factory(_connection);
        Seed();
    }

    public void Dispose() => _connection.Dispose();

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<JitenDbContext>
    {
        public JitenDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<JitenDbContext>().UseSqlite(connection).Options);
    }

    private void Seed()
    {
        using var context = _factory.CreateDbContext();
        context.Database.EnsureCreated();

        context.Decks.Add(new Deck { DeckId = ParentDeckId, OriginalTitle = "ひぐらし", MediaType = MediaType.VisualNovel });
        context.Decks.Add(new Deck
        {
            DeckId = ChildDeckId, ParentDeckId = ParentDeckId, OriginalTitle = "鬼隠し編", MediaType = MediaType.VisualNovel,
            RawText = new DeckRawText(ChildText)
        });

        context.JMDictWords.Add(new JmDictWord { WordId = SenseiWordId, PartsOfSpeech = ["n"], Priorities = ["ichi1"] });
        context.WordForms.Add(new JmDictWordForm
        {
            WordId = SenseiWordId, ReadingIndex = 0, Text = "先生", RubyText = "先[せん]生[せい]", FormType = JmDictFormType.KanjiForm,
            Priorities = ["ichi1", "news1"]
        });
        context.Definitions.Add(new JmDictDefinition { WordId = SenseiWordId, SenseIndex = 0, EnglishMeanings = ["teacher", "instructor"] });

        context.JMDictWords.Add(new JmDictWord { WordId = MaeharaNameWordId, PartsOfSpeech = ["surname"] });
        context.WordForms.Add(new JmDictWordForm
        {
            WordId = MaeharaNameWordId, ReadingIndex = 0, Text = "前原", RubyText = "前[まえ]原[はら]", FormType = JmDictFormType.KanjiForm,
            Priorities = ["spec1"]
        });

        context.SaveChanges();
    }

    private CharacterNameCheckService CreateService(JitenDbContext context) => new(context, _factory, new MemoryCache(new MemoryCacheOptions()));

    private async Task<CharacterNameCheckResult> Check(string name, params string[] existing)
    {
        await using var context = _factory.CreateDbContext();
        return (await CreateService(context).CheckManyAsync(ParentDeckId, existing, [name]))[0];
    }

    private static CharacterNameSurfaceCheck Surface(CharacterNameCheckResult result, string surface) =>
        result.Surfaces.Single(s => s.Surface == surface);

    [Fact]
    public async Task Spaced_name_derives_the_full_name_and_both_parts_counted_across_subdecks()
    {
        var result = await Check("前原 圭一");

        result.Error.Should().BeNull();
        result.Surfaces.Select(s => s.Surface).Should().BeEquivalentTo(["前原圭一", "前原", "圭一"]);
        Surface(result, "前原圭一").Occurrences.Should().Be(3);
        Surface(result, "圭一").Occurrences.Should().Be(4);
        result.Surfaces.Should().OnlyContain(s => s.Status == CharacterNameSurfaceStatus.Ok);
    }

    [Fact]
    public async Task Name_pos_entries_are_not_flagged_even_with_a_priority_tag()
    {
        var result = await Check("前原 圭一");

        Surface(result, "前原").Status.Should().Be(CharacterNameSurfaceStatus.Ok);
    }

    [Fact]
    public async Task Common_word_is_flagged_with_its_first_gloss_and_still_kept()
    {
        var result = await Check("先生");

        result.Error.Should().BeNull();
        var surface = Surface(result, "先生");
        surface.Status.Should().Be(CharacterNameSurfaceStatus.Flagged);
        surface.Detail.Should().Be("Also a common word (teacher).");
        result.Kept.Should().Equal("先生");
    }

    [Fact]
    public async Task Part_missing_from_the_text_is_dropped_but_the_name_is_accepted()
    {
        var result = await Check("竜宮 レナ");

        result.Error.Should().BeNull();
        Surface(result, "竜宮レナ").Status.Should().Be(CharacterNameSurfaceStatus.NotInText);
        Surface(result, "竜宮").Status.Should().Be(CharacterNameSurfaceStatus.NotInText);
        Surface(result, "レナ").Status.Should().Be(CharacterNameSurfaceStatus.Ok);
        result.Kept.Should().Equal("レナ");
    }

    [Fact]
    public async Task Name_absent_from_the_text_is_rejected()
    {
        var result = await Check("園崎 魅音");

        result.Error.Should().Be(CharacterNameCheckService.NotInTextError);
        result.Kept.Should().BeEmpty();
    }

    [Fact]
    public async Task Short_hiragana_name_is_risky_and_rejected()
    {
        var result = await Check("はる");

        Surface(result, "はる").Status.Should().Be(CharacterNameSurfaceStatus.Risky);
        Surface(result, "はる").Occurrences.Should().BeGreaterThan(0);
        result.Error.Should().Be(CharacterNameCheckService.NothingToAddError);
    }

    [Fact]
    public async Task Existing_surfaces_are_duplicates()
    {
        var result = await Check("前原 圭一", "圭一");

        Surface(result, "圭一").Status.Should().Be(CharacterNameSurfaceStatus.Duplicate);
        result.Error.Should().BeNull();
        result.Kept.Should().BeEquivalentTo(["前原圭一", "前原"]);
    }

    [Theory]
    [InlineData("Keiichi")]
    [InlineData("前原,圭一")]
    [InlineData("前原圭一1")]
    public async Task Characters_outside_kanji_and_kana_are_rejected(string name)
    {
        var result = await Check(name);

        result.Error.Should().Be(CharacterNameCheckService.InvalidCharactersError);
        result.Surfaces.Should().BeEmpty();
    }

    [Theory]
    [InlineData("圭")]
    [InlineData("あいうえおかきくけこさしすせそたちつてとな")]
    public async Task Names_outside_the_length_limits_are_rejected(string name)
    {
        var result = await Check(name);

        result.Error.Should().Be(CharacterNameCheckService.InvalidLengthError);
    }

    [Fact]
    public async Task Later_names_see_earlier_names_as_existing()
    {
        await using var context = _factory.CreateDbContext();
        var results = await CreateService(context).CheckManyAsync(ParentDeckId, [], ["前原 圭一", "前原圭一"]);

        results[0].Error.Should().BeNull();
        results[1].Surfaces.Should().OnlyContain(s => s.Status == CharacterNameSurfaceStatus.Duplicate);
        results[1].Error.Should().Be(CharacterNameCheckService.AlreadyListedError);
    }

    [Theory]
    [InlineData("椎野 さん")]
    [InlineData("椎野さん")]
    [InlineData("吉沢先生")]
    [InlineData("鹿之助 くん")]
    [InlineData("圭一　ちゃん")]
    [InlineData("前原 君")]
    [InlineData("前原 様")]
    [InlineData("レナちゃん")]
    public async Task Names_with_an_honorific_are_rejected(string name)
    {
        CharacterNameCheckService.EndsWithHonorific(name).Should().BeTrue();

        var result = await Check(name);

        result.Error.Should().Be(CharacterNameCheckService.HonorificError);
        result.Surfaces.Should().BeEmpty();
    }

    [Theory]
    [InlineData("正君")]
    [InlineData("前原圭一")]
    [InlineData("前原 圭一")]
    [InlineData("先生")]
    [InlineData("さん")]
    public void Names_without_an_honorific_suffix_are_not_caught(string name) =>
        CharacterNameCheckService.EndsWithHonorific(name).Should().BeFalse();

    [Fact]
    public async Task A_given_name_ending_in_a_one_character_honorific_without_a_space_stays_allowed()
    {
        var result = await Check("正君");

        result.Error.Should().BeNull();
        Surface(result, "正君").Status.Should().Be(CharacterNameSurfaceStatus.Ok);
        result.Kept.Should().Equal("正君");
    }

    [Theory]
    [InlineData("圭一圭一圭一", "圭一", 3)]
    [InlineData("ああああ", "ああ", 2)]
    [InlineData("abc", "", 0)]
    public void CountOrdinal_counts_non_overlapping_matches(string text, string value, int expected) =>
        CharacterNameCheckService.CountOrdinal(text, value).Should().Be(expected);
}
