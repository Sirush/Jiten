using System.Net;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.MetadataEdits;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class SitemapLastUpdateTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTimeOffset Created = new(2025, 3, 25, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reparsed = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LastUpdate_follows_creation_and_applied_edits_not_reparses()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            db.Decks.AddRange(
                new Deck { DeckId = 1, OriginalTitle = "Untouched", MediaType = MediaType.Anime, CreationDate = Created, LastUpdate = Reparsed },
                new Deck { DeckId = 2, OriginalTitle = "Edited", MediaType = MediaType.Anime, CreationDate = Created, LastUpdate = Reparsed },
                new Deck { DeckId = 3, OriginalTitle = "Parent", MediaType = MediaType.Anime, CreationDate = Created, LastUpdate = Reparsed },
                new Deck { DeckId = 4, ParentDeckId = 3, OriginalTitle = "Child", MediaType = MediaType.Anime, CreationDate = Created });
            await db.SaveChangesAsync();

            db.MetadataEdits.AddRange(
                new MetadataEdit { DeckId = 2, Status = MetadataEditStatus.Applied, AppliedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) },
                new MetadataEdit { DeckId = 2, Status = MetadataEditStatus.Pending, CreatedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc) },
                new MetadataEdit { DeckId = 4, Status = MetadataEditStatus.Approved, AppliedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) });
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/media-deck/get-media-decks-sitemap");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var lastUpdate = doc.RootElement.EnumerateArray()
                              .ToDictionary(e => e.GetProperty("id").GetInt32(), e => e.GetProperty("lastUpdate").GetDateTimeOffset());

        lastUpdate.Keys.Should().BeEquivalentTo([1, 2, 3]);
        lastUpdate[1].Should().Be(Created);
        lastUpdate[2].Should().Be(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        lastUpdate[3].Should().Be(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
    }
}
