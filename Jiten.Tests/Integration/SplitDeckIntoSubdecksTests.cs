using System.Net;
using System.Text;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class SplitDeckIntoSubdecksTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int DeckId = 1;
    private const string ParentText = "最初の巻の本文です。";
    private static readonly DateTime OldTimestamp = new(2025, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(long CompletedEntryId, long OngoingEntryId)> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        db.Decks.Add(new Deck
                     {
                         DeckId = DeckId, OriginalTitle = "Main", MediaType = MediaType.Novel, CharacterCount = ParentText.Length,
                         OriginalFileName = "volume1.txt", RawText = new DeckRawText(ParentText)
                     });
        await db.SaveChangesAsync();

        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var completed = new UserMediaListEntry
                        {
                            UserId = TestUsers.UserA, DeckId = DeckId, State = MediaListEntryState.Completed,
                            FinishedOn = new DateOnly(2025, 3, 1), CreatedAt = OldTimestamp
                        };
        var ongoing = new UserMediaListEntry
                      {
                          UserId = TestUsers.UserB, DeckId = DeckId, State = MediaListEntryState.InProgress, CharactersRead = 4,
                          CreatedAt = OldTimestamp
                      };
        userDb.UserMediaListEntries.AddRange(completed, ongoing);
        await userDb.SaveChangesAsync();

        userDb.UserDeckPreferences.AddRange(
            new UserDeckPreference { UserId = TestUsers.UserA, DeckId = DeckId, Status = DeckStatus.Completed, IsFavourite = true, CurrentEntryId = completed.Id },
            new UserDeckPreference { UserId = TestUsers.UserB, DeckId = DeckId, Status = DeckStatus.Ongoing, CurrentEntryId = ongoing.Id });
        await userDb.SaveChangesAsync();

        await userDb.UserMediaListEntries.ExecuteUpdateAsync(s => s.SetProperty(e => e.UpdatedAt, OldTimestamp));
        await userDb.UserDeckPreferences.ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedAt, OldTimestamp));
        return (completed.Id, ongoing.Id);
    }

    private Task<HttpResponseMessage> AddSecondVolumeAsync(bool keepParentText)
    {
        var form = new MultipartFormDataContent
                   {
                       { new StringContent(DeckId.ToString()), "deckId" },
                       { new StringContent(((int)MediaType.Novel).ToString()), "mediaType" },
                       { new StringContent("Main"), "originalTitle" },
                       { new StringContent("2020-01-01"), "releaseDate" },
                       { new StringContent("false"), "reparse" }
                   };

        var index = 0;
        if (keepParentText)
        {
            form.Add(new StringContent("Volume 1"), $"subdecks[{index}].originalTitle");
            form.Add(new StringContent("1"), $"subdecks[{index}].deckOrder");
            form.Add(new StringContent("true"), $"subdecks[{index}].fromParentText");
            index++;
        }

        form.Add(new StringContent("Volume 2"), $"subdecks[{index}].originalTitle");
        form.Add(new StringContent((index + 1).ToString()), $"subdecks[{index}].deckOrder");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("二巻目の本文です。")), $"subdecks[{index}].file", "volume2.txt");

        var message = new HttpRequestMessage(HttpMethod.Post, "/api/admin/update-deck") { Content = form }.WithAdmin();
        return _client.SendAsync(message);
    }

    [Fact]
    public async Task AddingSubdeckToLeafDeck_MovesItsTextIntoTheFirstSubdeck()
    {
        await SeedAsync();

        var response = await AddSecondVolumeAsync(keepParentText: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var parent = await db.Decks.AsNoTracking().Include(d => d.RawText).SingleAsync(d => d.DeckId == DeckId);
        var children = await db.Decks.AsNoTracking().Include(d => d.RawText)
                               .Where(d => d.ParentDeckId == DeckId).OrderBy(d => d.DeckOrder).ToListAsync();

        parent.RawText.Should().BeNull();
        children.Select(c => c.OriginalTitle).Should().Equal("Volume 1", "Volume 2");
        children[0].RawText!.RawText.Should().Be(ParentText);
        children[0].OriginalFileName.Should().Be("volume1.txt");
        children[1].RawText!.RawText.Should().Contain("二巻目");
    }

    [Fact]
    public async Task AddingSubdeckToLeafDeck_CopiesListStatusAndPassesToTheFirstSubdeck()
    {
        var (completedEntryId, ongoingEntryId) = await SeedAsync();

        var response = await AddSecondVolumeAsync(keepParentText: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var children = await db.Decks.AsNoTracking().Where(d => d.ParentDeckId == DeckId).OrderBy(d => d.DeckOrder).ToListAsync();
        var firstId = children[0].DeckId;

        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var preferences = await userDb.UserDeckPreferences.AsNoTracking().ToListAsync();
        var entries = await userDb.UserMediaListEntries.AsNoTracking().ToListAsync();

        preferences.Where(p => p.DeckId == DeckId).Should().HaveCount(2, "the parent stays on users' lists");
        entries.Where(e => e.DeckId == DeckId).Should().HaveCount(2);
        preferences.Should().NotContain(p => p.DeckId == children[1].DeckId);

        var copyA = entries.Single(e => e.DeckId == firstId && e.UserId == TestUsers.UserA);
        copyA.SeriesEntryId.Should().Be(completedEntryId);
        copyA.State.Should().Be(MediaListEntryState.Completed);
        copyA.FinishedOn.Should().Be(new DateOnly(2025, 3, 1));
        copyA.UpdatedAt.Should().Be(OldTimestamp);

        var copyB = entries.Single(e => e.DeckId == firstId && e.UserId == TestUsers.UserB);
        copyB.SeriesEntryId.Should().Be(ongoingEntryId);
        copyB.CharactersRead.Should().Be(4);

        var prefA = preferences.Single(p => p.DeckId == firstId && p.UserId == TestUsers.UserA);
        prefA.Status.Should().Be(DeckStatus.Completed);
        prefA.CurrentEntryId.Should().Be(copyA.Id);
        prefA.IsFavourite.Should().BeFalse("favourites belong to the title");
        prefA.UpdatedAt.Should().Be(OldTimestamp);

        preferences.Single(p => p.DeckId == firstId && p.UserId == TestUsers.UserB).CurrentEntryId.Should().Be(copyB.Id);
    }

    [Fact]
    public async Task AddingSubdeckWithoutTheParentTextRow_DiscardsTheTextAndCopiesNothing()
    {
        await SeedAsync();

        var response = await AddSecondVolumeAsync(keepParentText: false);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        (await db.Decks.AsNoTracking().Include(d => d.RawText).SingleAsync(d => d.DeckId == DeckId)).RawText.Should().BeNull();
        (await db.Decks.CountAsync(d => d.ParentDeckId == DeckId)).Should().Be(1);

        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await userDb.UserMediaListEntries.CountAsync(e => e.DeckId != DeckId)).Should().Be(0);
    }
}
