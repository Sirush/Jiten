using FluentAssertions;
using Jiten.Api.Services.SmartDeck;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.User;
using Jiten.Core.Services.SmartDeck;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class SmartDeckBuilderTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private const int CardWord = 900;
    private const int WindowWord = 901;
    private const int SharedWord = 902;
    private const int PassedOnlyWord = 903;
    private const int DroppedTitleWord = 904;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.FsrsCards.RemoveRange(userDb.FsrsCards.Where(c => c.UserId == TestUsers.UserA));
        userDb.UserSettings.RemoveRange(userDb.UserSettings.Where(s => s.UserId == TestUsers.UserA));
        await userDb.SaveChangesAsync();
        await SetPlus(true);
    }

    private async Task SetPlus(bool plus)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var user = await userDb.Users.FirstAsync(u => u.Id == TestUsers.UserA);
        user.AdminPremiumOverride = plus;
        await userDb.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<Jiten.Api.Services.IJitenPlusService>().InvalidateTier(TestUsers.UserA);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Rebuild_WritesWindowFirst_KeepsCards_DropsFinishedTitles()
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var series = new Deck { OriginalTitle = "Series", MediaType = MediaType.Anime };
        var dropped = new Deck { OriginalTitle = "Dropped", MediaType = MediaType.Anime };
        jitenDb.Decks.AddRange(series, dropped);
        await jitenDb.SaveChangesAsync();

        var ep1 = new Deck { OriginalTitle = "Ep 1", MediaType = MediaType.Anime, ParentDeckId = series.DeckId, DeckOrder = 0 };
        var ep2 = new Deck { OriginalTitle = "Ep 2", MediaType = MediaType.Anime, ParentDeckId = series.DeckId, DeckOrder = 1 };
        var ep3 = new Deck { OriginalTitle = "Ep 3", MediaType = MediaType.Anime, ParentDeckId = series.DeckId, DeckOrder = 2 };
        jitenDb.Decks.AddRange(ep1, ep2, ep3);
        await jitenDb.SaveChangesAsync();

        jitenDb.DeckWords.AddRange(
            new DeckWord { Deck = series, WordId = CardWord, ReadingIndex = 0, Occurrences = 500 },
            new DeckWord { Deck = series, WordId = SharedWord, ReadingIndex = 0, Occurrences = 40 },
            new DeckWord { Deck = series, WordId = PassedOnlyWord, ReadingIndex = 0, Occurrences = 40 },
            new DeckWord { Deck = series, WordId = WindowWord, ReadingIndex = 0, Occurrences = 2 },
            new DeckWord { Deck = ep1, WordId = PassedOnlyWord, ReadingIndex = 0, Occurrences = 40 },
            new DeckWord { Deck = ep2, WordId = WindowWord, ReadingIndex = 0, Occurrences = 2 },
            new DeckWord { Deck = ep2, WordId = SharedWord, ReadingIndex = 0, Occurrences = 1 },
            new DeckWord { Deck = dropped, WordId = DroppedTitleWord, ReadingIndex = 0, Occurrences = 999 });
        await jitenDb.SaveChangesAsync();

        userDb.UserDeckPreferences.AddRange(
            new UserDeckPreference { UserId = TestUsers.UserA, DeckId = series.DeckId, Status = DeckStatus.Ongoing },
            new UserDeckPreference { UserId = TestUsers.UserA, DeckId = ep1.DeckId, Status = DeckStatus.Completed },
            new UserDeckPreference { UserId = TestUsers.UserA, DeckId = dropped.DeckId, Status = DeckStatus.Dropped });
        userDb.FsrsCards.Add(new FsrsCard { UserId = TestUsers.UserA, WordId = CardWord, ReadingIndex = 0 });
        userDb.UserSettings.Add(new UserSettings { UserId = TestUsers.UserA, SmartDeckJson = new SmartDeckSettings { Enabled = true }.Serialize() });
        var smartDeck = new UserStudyDeck { UserId = TestUsers.UserA, DeckType = StudyDeckType.Smart, Name = "Smart Deck", IsActive = true };
        userDb.UserStudyDecks.Add(smartDeck);
        await userDb.SaveChangesAsync();

        var builder = scope.ServiceProvider.GetRequiredService<ISmartDeckBuilder>();
        var result = await builder.Rebuild(TestUsers.UserA);

        result.Built.Should().BeTrue(result.SkipReason);
        result.TitleCount.Should().Be(1);

        var rows = await userDb.UserStudyDeckWords.AsNoTracking()
                               .Where(w => w.UserStudyDeckId == smartDeck.UserStudyDeckId)
                               .OrderBy(w => w.SortOrder)
                               .ToListAsync();

        rows.Select(w => w.WordId).Should().Equal(CardWord, SharedWord, PassedOnlyWord, WindowWord);
        rows.Should().Contain(w => w.WordId == CardWord, "a carded word stays in so a study-decks-only reviewer keeps seeing it");
        rows.Should().NotContain(w => w.WordId == DroppedTitleWord, "dropped titles leave the set");
        rows[1].Occurrences.Should().BeGreaterThan(rows[2].Occurrences, "a window occurrence lifts a word above an equal whole-title count");
        rows.Select(w => w.SortOrder).Should().Equal(1, 2, 3, 4);

        var sources = await builder.LoadSources(TestUsers.UserA, new SmartDeckSettings { Enabled = true });
        sources.Windows[series.DeckId].CursorDeckId.Should().Be(ep1.DeckId);
        sources.Windows[series.DeckId].WindowDeckIds.Should().Equal(ep2.DeckId);
    }

    [Fact]
    public async Task LoadSources_UnorderedTitles_FollowOngoingUnits_OrHaveNoWindow()
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var vn = new Deck { OriginalTitle = "VN", MediaType = MediaType.VisualNovel };
        var game = new Deck { OriginalTitle = "Game", MediaType = MediaType.VideoGame };
        var anime = new Deck { OriginalTitle = "Anime", MediaType = MediaType.Anime };
        jitenDb.Decks.AddRange(vn, game, anime);
        await jitenDb.SaveChangesAsync();

        var common = new Deck { OriginalTitle = "Common", MediaType = MediaType.VisualNovel, ParentDeckId = vn.DeckId, DeckOrder = 0 };
        var routeA = new Deck { OriginalTitle = "Route A", MediaType = MediaType.VisualNovel, ParentDeckId = vn.DeckId, DeckOrder = 1 };
        var routeB = new Deck { OriginalTitle = "Route B", MediaType = MediaType.VisualNovel, ParentDeckId = vn.DeckId, DeckOrder = 2 };
        var main = new Deck { OriginalTitle = "Main", MediaType = MediaType.VideoGame, ParentDeckId = game.DeckId, DeckOrder = 0 };
        var side = new Deck { OriginalTitle = "Side", MediaType = MediaType.VideoGame, ParentDeckId = game.DeckId, DeckOrder = 1 };
        var ep1 = new Deck { OriginalTitle = "Ep 1", MediaType = MediaType.Anime, ParentDeckId = anime.DeckId, DeckOrder = 0 };
        var ep2 = new Deck { OriginalTitle = "Ep 2", MediaType = MediaType.Anime, ParentDeckId = anime.DeckId, DeckOrder = 1 };
        jitenDb.Decks.AddRange(common, routeA, routeB, main, side, ep1, ep2);
        await jitenDb.SaveChangesAsync();

        userDb.UserDeckPreferences.AddRange(
            new UserDeckPreference { UserId = TestUsers.UserA, DeckId = vn.DeckId, Status = DeckStatus.Ongoing },
            new UserDeckPreference { UserId = TestUsers.UserA, DeckId = common.DeckId, Status = DeckStatus.Completed },
            new UserDeckPreference { UserId = TestUsers.UserA, DeckId = routeB.DeckId, Status = DeckStatus.Ongoing },
            new UserDeckPreference { UserId = TestUsers.UserA, DeckId = game.DeckId, Status = DeckStatus.Ongoing },
            new UserDeckPreference { UserId = TestUsers.UserA, DeckId = main.DeckId, Status = DeckStatus.Completed },
            new UserDeckPreference { UserId = TestUsers.UserA, DeckId = anime.DeckId, Status = DeckStatus.Ongoing },
            new UserDeckPreference { UserId = TestUsers.UserA, DeckId = ep1.DeckId, Status = DeckStatus.Completed });
        await userDb.SaveChangesAsync();

        var builder = scope.ServiceProvider.GetRequiredService<ISmartDeckBuilder>();
        var sources = await builder.LoadSources(TestUsers.UserA, new SmartDeckSettings { Enabled = true });

        sources.Windows[vn.DeckId].Source.Should().Be(SmartDeckWindowSource.Ongoing);
        sources.Windows[vn.DeckId].WindowDeckIds.Should().Equal(new[] { routeB.DeckId }, "the route marked Ongoing wins over the next one in DeckOrder");
        sources.Windows[vn.DeckId].CompletedUnits.Should().Be(1);

        sources.Windows[game.DeckId].Source.Should().Be(SmartDeckWindowSource.None);
        sources.Windows[game.DeckId].WindowDeckIds.Should().BeEmpty("a game's subdecks are categories, not a sequence");

        sources.Windows[anime.DeckId].Source.Should().Be(SmartDeckWindowSource.Sequence);
        sources.Windows[anime.DeckId].WindowDeckIds.Should().Equal(ep2.DeckId);

        var forced = await builder.LoadSources(TestUsers.UserA, new SmartDeckSettings
        {
            Enabled = true,
            SequenceOverrides = { [game.DeckId] = true, [anime.DeckId] = false },
        });
        forced.Windows[game.DeckId].WindowDeckIds.Should().Equal(new[] { side.DeckId }, "an override turns the DeckOrder cursor on");
        forced.Windows[anime.DeckId].WindowDeckIds.Should().BeEmpty("an override turns it off");
    }

    [Fact]
    public async Task Rebuild_SkipsWhenDisabledOrMissing()
    {
        using var scope = factory.Services.CreateScope();
        var builder = scope.ServiceProvider.GetRequiredService<ISmartDeckBuilder>();

        (await builder.Rebuild(TestUsers.UserA)).SkipReason.Should().Be("no smart deck");

        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserStudyDecks.Add(new UserStudyDeck { UserId = TestUsers.UserA, DeckType = StudyDeckType.Smart, Name = "Smart Deck" });
        await userDb.SaveChangesAsync();

        (await builder.Rebuild(TestUsers.UserA)).SkipReason.Should().Be("disabled");
    }

    [Fact]
    public async Task TierLapse_DeactivatesDeck_KeepsRows_ResubscribeLeavesItPaused()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var deck = new UserStudyDeck { UserId = TestUsers.UserA, DeckType = StudyDeckType.Smart, Name = "Smart Deck", IsActive = true };
        userDb.UserStudyDecks.Add(deck);
        userDb.UserSettings.Add(new UserSettings { UserId = TestUsers.UserA, SmartDeckJson = new SmartDeckSettings { Enabled = true }.Serialize() });
        await userDb.SaveChangesAsync();
        userDb.UserStudyDeckWords.Add(new UserStudyDeckWord { UserStudyDeckId = deck.UserStudyDeckId, WordId = 1, ReadingIndex = 0, SortOrder = 1 });
        await userDb.SaveChangesAsync();

        await SetPlus(false);
        var builder = scope.ServiceProvider.GetRequiredService<ISmartDeckBuilder>();
        (await builder.Rebuild(TestUsers.UserA)).SkipReason.Should().Be("not plus");

        await userDb.Entry(deck).ReloadAsync();
        deck.IsActive.Should().BeFalse();
        (await userDb.UserStudyDeckWords.CountAsync(w => w.UserStudyDeckId == deck.UserStudyDeckId)).Should().Be(1, "rows survive a lapse");

        await SetPlus(true);
        (await builder.Rebuild(TestUsers.UserA)).Built.Should().BeTrue();
        await userDb.Entry(deck).ReloadAsync();
        deck.IsActive.Should().BeFalse("pause belongs to the user; the job never resumes a deck");
    }

    [Fact]
    public async Task StatusWrite_MarksTheUserDirty()
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var deck = new Deck { OriginalTitle = "Solo", MediaType = MediaType.Anime };
        jitenDb.Decks.Add(deck);
        await jitenDb.SaveChangesAsync();

        var recorder = factory.Services.GetRequiredService<RecordingSmartDeckDirtyService>();
        recorder.Marks.Clear();

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/user/deck-preferences/{deck.DeckId}/status")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { status = (int)DeckStatus.Ongoing });
        (await client.SendAsync(request)).EnsureSuccessStatusCode();

        recorder.Marks.Should().Contain(TestUsers.UserA);
    }
}
