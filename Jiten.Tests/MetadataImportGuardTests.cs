using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Jobs;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.MetadataEdits;
using Jiten.Core.Data.Providers;
using Jiten.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jiten.Tests;

public class MetadataImportGuardTests : IDisposable
{
    private const int SchoolTagId = 1;
    private const int RomanceTagId = 2;
    private const int MagicTagId = 3;

    private readonly SqliteConnection _connection;

    public MetadataImportGuardTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    private JitenDbContext CreateContext()
    {
        var context = new JitenDbContext(new DbContextOptionsBuilder<JitenDbContext>().UseSqlite(_connection).Options);
        context.Database.EnsureCreated();
        return context;
    }

    private static DeckMetadataExclusionSet Exclusions(params (DeckMetadataExclusionKind Kind, string Value)[] rows) =>
        new(rows.Select(r => new DeckMetadataExclusion { DeckId = 1, Kind = r.Kind, Value = r.Value }));

    private async Task SeedMappingsAsync(JitenDbContext context)
    {
        context.Tags.AddRange(new Tag { TagId = SchoolTagId, Name = "School" }, new Tag { TagId = RomanceTagId, Name = "Romance" },
                              new Tag { TagId = MagicTagId, Name = "Magic" });
        context.ExternalGenreMappings.AddRange(
            new ExternalGenreMapping { Provider = LinkType.Vndb, ExternalGenreName = "Action", JitenGenre = Genre.Action },
            new ExternalGenreMapping { Provider = LinkType.Vndb, ExternalGenreName = "Comedy", JitenGenre = Genre.Comedy });
        context.ExternalTagMappings.AddRange(
            new ExternalTagMapping { Provider = LinkType.Vndb, ExternalTagName = "School", TagId = SchoolTagId },
            new ExternalTagMapping { Provider = LinkType.Vndb, ExternalTagName = "Romance", TagId = RomanceTagId },
            new ExternalTagMapping { Provider = LinkType.Vndb, ExternalTagName = "Magic", TagId = MagicTagId });
        await context.SaveChangesAsync();
    }

    private static Metadata ProviderMetadata() => new()
    {
        Genres = ["Action", "Comedy"],
        Tags =
        [
            new MetadataTag { Name = "School", Percentage = 90 },
            new MetadataTag { Name = "Romance", Percentage = 90 },
            new MetadataTag { Name = "Magic", Percentage = 50 }
        ]
    };

    private static Deck DeckWithTags() => new()
    {
        DeckId = 1,
        OriginalTitle = "テスト",
        DeckTags =
        [
            new DeckTag { DeckId = 1, TagId = SchoolTagId, Percentage = 10, IsManual = true },
            new DeckTag { DeckId = 1, TagId = RomanceTagId, Percentage = 10 }
        ]
    };

    [Fact]
    public async Task Mapping_skips_excluded_genres_and_tags_and_leaves_manual_tags_alone()
    {
        await using var context = CreateContext();
        await SeedMappingsAsync(context);
        var deck = DeckWithTags();

        await MetadataProviderHelper.ApplyGenreAndTagMappings(context, deck, ProviderMetadata(), LinkType.Vndb,
                                                              Exclusions((DeckMetadataExclusionKind.Genre, ((int)Genre.Comedy).ToString()),
                                                                         (DeckMetadataExclusionKind.Tag, MagicTagId.ToString())));

        deck.DeckGenres.Select(g => g.Genre).Should().BeEquivalentTo([Genre.Action]);
        deck.DeckTags.Should().HaveCount(2);
        deck.DeckTags.Single(t => t.TagId == SchoolTagId).Percentage.Should().Be(10);
        deck.DeckTags.Single(t => t.TagId == RomanceTagId).Percentage.Should().Be(90);
    }

    [Fact]
    public async Task Mapping_without_exclusions_adds_everything_the_provider_maps()
    {
        await using var context = CreateContext();
        await SeedMappingsAsync(context);
        var deck = DeckWithTags();

        await MetadataProviderHelper.ApplyGenreAndTagMappings(context, deck, ProviderMetadata(), LinkType.Vndb, DeckMetadataExclusionSet.Empty);

        deck.DeckGenres.Select(g => g.Genre).Should().BeEquivalentTo([Genre.Action, Genre.Comedy]);
        deck.DeckTags.Select(t => t.TagId).Should().BeEquivalentTo([SchoolTagId, RomanceTagId, MagicTagId]);
        deck.DeckTags.Single(t => t.TagId == SchoolTagId).Percentage.Should().Be(10);
    }

    [Fact]
    public void Alias_merge_never_re_adds_an_excluded_alias_whatever_its_case()
    {
        var deck = new Deck { DeckId = 1, OriginalTitle = "テスト" };

        MetadataProviderHelper.MergeAliases(deck, ["Junk Alias", "Real Alias"], Exclusions((DeckMetadataExclusionKind.Alias, "junk alias")));

        deck.Titles.Select(t => t.Title).Should().BeEquivalentTo(["Real Alias"]);
    }

    [Fact]
    public void Name_merge_never_re_adds_an_excluded_character_name()
    {
        var deck = new Deck { DeckId = 1, OriginalTitle = "テスト" };
        var entries = new List<DeckDictionaryEntry>
        {
            new() { Surface = "先生", EntryType = DeckDictionaryEntryType.Name },
            new() { Surface = "圭一", EntryType = DeckDictionaryEntryType.Name }
        };

        MetadataProviderHelper.MergeDictionaryEntries(deck, entries, Exclusions((DeckMetadataExclusionKind.CharacterName, "先生")));

        deck.DictionaryEntries.Select(e => e.Surface).Should().BeEquivalentTo(["圭一"]);
    }

    [Fact]
    public void Scalar_fill_skips_locked_fields()
    {
        var metadata = new Metadata { ReleaseDate = new DateTime(2004, 8, 13), Description = "From the provider" };
        var locked = new Deck { OriginalTitle = "テスト", MetadataLocks = MetadataField.ReleaseDate | MetadataField.Description };
        var unlocked = new Deck { OriginalTitle = "テスト" };

        MetadataProviderHelper.ApplyScalars(locked, metadata);
        MetadataProviderHelper.ApplyScalars(unlocked, metadata);

        locked.ReleaseDate.Should().Be(default);
        locked.Description.Should().BeNull();
        unlocked.ReleaseDate.Should().Be(new DateOnly(2004, 8, 13));
        unlocked.Description.Should().Be("From the provider");
    }

    [Fact]
    public void Bulk_reimport_keeps_locked_titles_and_media_type()
    {
        var existing = new Deck
        {
            OriginalTitle = "正しい題名", RomajiTitle = "Tadashii", EnglishTitle = "Old English", MediaType = MediaType.VisualNovel,
            MetadataLocks = MetadataField.OriginalTitle | MetadataField.RomajiTitle | MetadataField.MediaType
        };
        var imported = new Deck { OriginalTitle = "間違い", RomajiTitle = "Machigai", EnglishTitle = "New English", MediaType = MediaType.Novel };

        JitenHelper.ApplyImportedIdentity(existing, imported);

        existing.OriginalTitle.Should().Be("正しい題名");
        existing.RomajiTitle.Should().Be("Tadashii");
        existing.MediaType.Should().Be(MediaType.VisualNovel);
        existing.EnglishTitle.Should().Be("New English");
    }

    [Fact]
    public void Bulk_reimport_overwrites_unlocked_identity()
    {
        var existing = new Deck { OriginalTitle = "古い", RomajiTitle = "Furui", EnglishTitle = "Old", MediaType = MediaType.VisualNovel };
        var imported = new Deck { OriginalTitle = "新しい", RomajiTitle = "Atarashii", EnglishTitle = "New", MediaType = MediaType.Novel };

        JitenHelper.ApplyImportedIdentity(existing, imported);

        existing.OriginalTitle.Should().Be("新しい");
        existing.RomajiTitle.Should().Be("Atarashii");
        existing.EnglishTitle.Should().Be("New");
        existing.MediaType.Should().Be(MediaType.Novel);
    }

    [Fact]
    public void Provider_edit_is_null_when_nothing_changed()
    {
        var deck = new Deck { DeckId = 1, OriginalTitle = "テスト" };
        var snapshot = DeckMetadataSnapshot.Capture(deck);

        MetadataEditLog.ProviderEdit(1, LinkType.Vndb, snapshot, DeckMetadataSnapshot.Capture(deck)).Should().BeNull();
    }

    [Fact]
    public void Provider_edit_records_changed_fields_as_an_approved_provider_row()
    {
        var deck = new Deck { DeckId = 1, OriginalTitle = "テスト" };
        var before = DeckMetadataSnapshot.Capture(deck);
        deck.Titles.Add(new DeckTitle { DeckId = 1, Title = "Alias", TitleType = DeckTitleType.Alias });
        deck.DeckGenres.Add(new DeckGenre { DeckId = 1, Genre = Genre.Action });
        var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

        var edit = MetadataEditLog.ProviderEdit(1, LinkType.Vndb, before, DeckMetadataSnapshot.Capture(deck), now)!;

        edit.Should().NotBeNull();
        edit.Actor.Should().Be(MetadataEditActor.Provider);
        edit.Provider.Should().Be(LinkType.Vndb);
        edit.UserId.Should().BeNull();
        edit.Status.Should().Be(MetadataEditStatus.Approved);
        edit.Fields.Should().Be(MetadataField.Aliases | MetadataField.Genres);
        edit.SubmissionId.Should().NotBe(Guid.Empty);
        edit.CreatedAt.Should().Be(now);
        edit.AppliedAt.Should().Be(now);

        var changes = JsonSerializer.Deserialize<List<MetadataFieldChange>>(edit.ChangesJson, DeckMetadataSnapshot.Json)!;
        changes.Select(c => c.Field).Should().BeEquivalentTo([MetadataField.Aliases, MetadataField.Genres]);
        changes.Single(c => c.Field == MetadataField.Aliases).After.EnumerateArray().Select(e => e.GetString()).Should().Equal("Alias");
    }
}
