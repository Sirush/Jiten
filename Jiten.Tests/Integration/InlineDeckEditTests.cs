using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class InlineDeckEditTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int MainDeckId = 1;
    private const int OtherDeckId = 2;
    private const int TagAlpha = 11;
    private const int TagBeta = 22;

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Deck 1 with one genre, one tag, one link and one outgoing Sequel edge to deck 2.</summary>
    private async Task SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        db.DeckRelationships.RemoveRange(db.DeckRelationships);
        db.Decks.RemoveRange(db.Decks);
        db.Tags.RemoveRange(db.Tags);
        await db.SaveChangesAsync();

        db.Tags.AddRange(
            new Tag { TagId = TagAlpha, Name = "Alpha" },
            new Tag { TagId = TagBeta, Name = "Beta" });
        db.Decks.AddRange(
            new Deck { DeckId = MainDeckId, OriginalTitle = "Main", MediaType = MediaType.Anime },
            new Deck { DeckId = OtherDeckId, OriginalTitle = "Other", MediaType = MediaType.Novel });
        await db.SaveChangesAsync();

        db.Add(new DeckGenre { DeckId = MainDeckId, Genre = Genre.Action });
        db.Add(new DeckTag { DeckId = MainDeckId, TagId = TagAlpha, Percentage = 40 });
        // Link.Deck defaults to a fresh Deck instance; leaving it set would make Add insert a phantom deck.
        db.Add(new Link { DeckId = MainDeckId, LinkType = LinkType.Vndb, Url = "https://vndb.org/v1", Deck = null! });
        db.DeckRelationships.Add(new DeckRelationship
                                 {
                                     SourceDeckId = MainDeckId, TargetDeckId = OtherDeckId,
                                     RelationshipType = DeckRelationshipType.Sequel
                                 });
        await db.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> PatchAsync(object body, int deckId = MainDeckId, bool admin = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/admin/deck/{deckId}/metadata")
            .WithJsonContent(body);
        return _client.SendAsync(admin ? request.WithAdmin() : request.WithUser(TestUsers.UserA));
    }

    private async Task<DeckMetadataPatchResultDto> PatchOkAsync(object body, int deckId = MainDeckId)
    {
        var response = await PatchAsync(body, deckId);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var dto = await response.Content.ReadFromJsonAsync<DeckMetadataPatchResultDto>();
        dto.Should().NotBeNull();
        return dto!;
    }

    private static async Task<T> ReadDbAsync<T>(JitenWebApplicationFactory factory, Func<JitenDbContext, Task<T>> read)
    {
        using var scope = factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<JitenDbContext>());
    }

    [Fact]
    public async Task GenresOnlyPatch_LeavesOtherCollectionsUntouched()
    {
        await SeedAsync();

        var result = await PatchOkAsync(new { genres = new[] { (int)Genre.Drama, (int)Genre.Romance } });

        result.Genres.Should().BeEquivalentTo(new[] { Genre.Drama, Genre.Romance });
        result.Tags.Should().ContainSingle().Which.TagId.Should().Be(TagAlpha);
        result.Links.Should().ContainSingle().Which.Url.Should().Be("https://vndb.org/v1");
        result.Relationships.Should().ContainSingle().Which.TargetDeckId.Should().Be(OtherDeckId);
    }

    [Fact]
    public async Task EmptyCollectionClears_NullLeavesAlone()
    {
        await SeedAsync();

        var cleared = await PatchOkAsync(new { genres = Array.Empty<int>() });
        cleared.Genres.Should().BeEmpty();
        cleared.Tags.Should().ContainSingle();

        var untouched = await PatchOkAsync(new { hideDialoguePercentage = true });
        untouched.Tags.Should().ContainSingle();
        untouched.Links.Should().ContainSingle();
        untouched.Relationships.Should().ContainSingle();
        untouched.HideDialoguePercentage.Should().BeTrue();
    }

    [Fact]
    public async Task RelationshipReconcile_WhenEditedDeckIsTheStoredTarget()
    {
        await SeedAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            db.DeckRelationships.RemoveRange(db.DeckRelationships);
            db.DeckRelationships.Add(new DeckRelationship
                                     {
                                         SourceDeckId = OtherDeckId, TargetDeckId = MainDeckId,
                                         RelationshipType = DeckRelationshipType.Adaptation
                                     });
            await db.SaveChangesAsync();
        }

        // Deck 1 sees the edge as an inverse SourceMaterial; dropping it must delete the stored row.
        var result = await PatchOkAsync(new { relationships = Array.Empty<object>() });

        result.Relationships.Should().BeEmpty();
        var stored = await ReadDbAsync(factory, db => db.DeckRelationships.CountAsync());
        stored.Should().Be(0);
    }

    [Fact]
    public async Task AddingEdgeSourcedOnTheOtherDeck_StoresExactlyOneRow()
    {
        await SeedAsync();

        var result = await PatchOkAsync(new
                                        {
                                            relationships = new[]
                                                            {
                                                                new
                                                                {
                                                                    sourceDeckId = OtherDeckId,
                                                                    targetDeckId = MainDeckId,
                                                                    relationshipType = (int)DeckRelationshipType.Sequel
                                                                }
                                                            }
                                        });

        result.Relationships.Should().ContainSingle().Which.IsInverse.Should().BeTrue();

        var stored = await ReadDbAsync(factory, db => db.DeckRelationships.ToListAsync());
        stored.Should().ContainSingle();
        stored[0].SourceDeckId.Should().Be(OtherDeckId);
        stored[0].TargetDeckId.Should().Be(MainDeckId);
    }

    [Fact]
    public async Task Response_CarriesNestedTargetDeck()
    {
        await SeedAsync();

        var result = await PatchOkAsync(new { genres = new[] { (int)Genre.Drama } });

        var relationship = result.Relationships.Should().ContainSingle().Subject;
        relationship.TargetDeck.Should().NotBeNull();
        relationship.TargetDeck.OriginalTitle.Should().Be("Other");
    }

    [Fact]
    public async Task NonAdmin_IsForbidden()
    {
        await SeedAsync();

        var response = await PatchAsync(new { genres = Array.Empty<int>() }, admin: false);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UnknownDeck_Returns404()
    {
        await SeedAsync();

        var response = await PatchAsync(new { genres = Array.Empty<int>() }, deckId: 999);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TitlesAreTrimmed_AndBlankOriginalTitleIsRejected()
    {
        await SeedAsync();

        var result = await PatchOkAsync(new { originalTitle = "  Renamed  ", romajiTitle = "  Renamed romaji ", englishTitle = "  " });
        result.OriginalTitle.Should().Be("Renamed");
        result.RomajiTitle.Should().Be("Renamed romaji");
        result.EnglishTitle.Should().BeEmpty();

        var blank = await PatchAsync(new { originalTitle = "   " });
        blank.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DescriptionPatch_SetsTrimsAndClears()
    {
        await SeedAsync();

        const string multiline = "Line one\nLine two";

        var set = await PatchOkAsync(new { description = $"  {multiline}  " });
        set.Description.Should().Be(multiline);

        var untouched = await PatchOkAsync(new { genres = new[] { (int)Genre.Drama } });
        untouched.Description.Should().Be(multiline);

        var cleared = await PatchOkAsync(new { description = "" });
        cleared.Description.Should().BeEmpty();
        var stored = await ReadDbAsync(factory, db => db.Decks.Where(d => d.DeckId == MainDeckId).Select(d => d.Description).FirstAsync());
        stored.Should().BeNull();
    }

    [Fact]
    public async Task TagPatch_UpdatesPercentage_AndRejectsUnknownIds()
    {
        await SeedAsync();

        var result = await PatchOkAsync(new
                                        {
                                            tags = new[]
                                                   {
                                                       new { tagId = TagAlpha, percentage = 90 },
                                                       new { tagId = TagBeta, percentage = 50 }
                                                   }
                                        });
        result.Tags.Should().HaveCount(2);
        result.Tags[0].Percentage.Should().Be(90);

        var unknown = await PatchAsync(new { tags = new[] { new { tagId = 999, percentage = 50 } } });
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var outOfRange = await PatchAsync(new { tags = new[] { new { tagId = TagAlpha, percentage = 200 } } });
        outOfRange.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task InvalidGenreValue_IsRejected()
    {
        await SeedAsync();

        var response = await PatchAsync(new { genres = new[] { 999 } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData((int)DeckRelationshipType.Prequel, OtherDeckId)]
    [InlineData((int)DeckRelationshipType.Sequel, MainDeckId)]
    public async Task MalformedRelationship_IsRejected(int relationshipType, int targetDeckId)
    {
        await SeedAsync();

        var response = await PatchAsync(new
                                        {
                                            relationships = new[]
                                                            {
                                                                new
                                                                {
                                                                    sourceDeckId = MainDeckId,
                                                                    targetDeckId,
                                                                    relationshipType
                                                                }
                                                            }
                                        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RelationshipTouchingNeitherEndpoint_IsRejected()
    {
        await SeedAsync();

        var response = await PatchAsync(new
                                        {
                                            relationships = new[]
                                                            {
                                                                new
                                                                {
                                                                    sourceDeckId = OtherDeckId,
                                                                    targetDeckId = 999,
                                                                    relationshipType = (int)DeckRelationshipType.Sequel
                                                                }
                                                            }
                                        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task LinkPatch_EditsInPlace_ClearsOnEmpty_AndRejectsNonHttpSchemes()
    {
        await SeedAsync();

        var edited = await PatchOkAsync(new
                                        {
                                            links = new[]
                                                    {
                                                        new { linkType = (int)LinkType.Vndb, url = "https://vndb.org/v2" }
                                                    }
                                        });
        edited.Links.Should().ContainSingle().Which.Url.Should().Be("https://vndb.org/v2");
        var storedLinks = await ReadDbAsync(factory, db => db.Set<Link>().CountAsync());
        storedLinks.Should().Be(1);

        var hostile = await PatchAsync(new
                                       {
                                           links = new[] { new { linkType = (int)LinkType.Web, url = "javascript:alert(1)" } }
                                       });
        hostile.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var cleared = await PatchOkAsync(new { links = Array.Empty<object>() });
        cleared.Links.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownLinkType_IsRejected()
    {
        await SeedAsync();

        var response = await PatchAsync(new { links = new[] { new { linkType = 99, url = "https://example.com" } } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
