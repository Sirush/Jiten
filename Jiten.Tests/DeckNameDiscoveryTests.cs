using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Tests;

public sealed class DeckNameDiscoveryTests : IDisposable
{
    private const int DeckId = 1;

    private readonly SqliteConnection _connection;

    public DeckNameDiscoveryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var context = CreateContext();
        context.Database.EnsureCreated();
        context.Decks.Add(new Deck { DeckId = DeckId, OriginalTitle = "テスト", MediaType = MediaType.Novel });
        context.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private JitenDbContext CreateContext() => new(new DbContextOptionsBuilder<JitenDbContext>().UseSqlite(_connection).Options);

    private static DeckDictionaryEntry Entry(string surface, DeckDictionaryEntryType type) =>
        new() { DeckId = DeckId, Surface = surface, EntryType = type };

    [Fact]
    public void A_full_parse_receives_only_curated_names()
    {
        var entries = new List<DeckDictionaryEntry>
        {
            Entry("圭一", DeckDictionaryEntryType.Name), Entry("霞空", DeckDictionaryEntryType.DiscoveredName)
        };

        DeckNameDiscovery.CuratedOnly(entries)!.Select(e => e.Surface).Should().Equal("圭一");
        DeckNameDiscovery.CuratedOnly([Entry("霞空", DeckDictionaryEntryType.DiscoveredName)]).Should().BeNull();
    }

    [Fact]
    public void A_discovered_name_never_duplicates_a_curated_one()
    {
        var curated = new List<DeckDictionaryEntry> { Entry("圭一", DeckDictionaryEntryType.Name) };

        var entries = DeckNameDiscovery.ToEntries(["圭一", "霞空", "霞空"], curated);

        entries.Select(e => e.Surface).Should().Equal("霞空");
        entries.Should().OnlyContain(e => e.EntryType == DeckDictionaryEntryType.DiscoveredName);
    }

    [Fact]
    public void A_name_someone_lists_stops_being_a_discovered_one()
    {
        var deck = new Deck { DeckId = DeckId, DictionaryEntries = [Entry("霞空", DeckDictionaryEntryType.DiscoveredName)] };

        DeckNameDiscovery.Promote(deck, ["霞空"]);

        deck.DictionaryEntries.Single().EntryType.Should().Be(DeckDictionaryEntryType.Name);
    }

    [Fact]
    public async Task A_full_parse_replaces_the_discovered_names_and_leaves_curated_ones()
    {
        await using (var seed = CreateContext())
        {
            seed.DeckDictionaryEntries.AddRange(Entry("圭一", DeckDictionaryEntryType.Name),
                                                Entry("霞空", DeckDictionaryEntryType.DiscoveredName),
                                                Entry("古名", DeckDictionaryEntryType.DiscoveredName));
            await seed.SaveChangesAsync();
        }

        await using (var context = CreateContext())
            await DeckNameDiscovery.ReplaceAsync(context, DeckId, ["霞空", "水那", "圭一"]);

        await using var check = CreateContext();
        var stored = await check.DeckDictionaryEntries.Where(e => e.DeckId == DeckId).ToListAsync();
        stored.Select(e => (e.Surface, e.EntryType)).Should().BeEquivalentTo([
            ("圭一", DeckDictionaryEntryType.Name),
            ("霞空", DeckDictionaryEntryType.DiscoveredName),
            ("水那", DeckDictionaryEntryType.DiscoveredName)
        ]);
    }
}
